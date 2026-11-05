using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Services;
using MinerBot.Dashboard.Tests.Support;
using static MinerBot.Dashboard.Tests.Support.FakeDiscord;

namespace MinerBot.Dashboard.Tests
{
    // Discord sign-in through ASP.NET Core's own OAuth handler, against the fake Discord: PKCE, the identify scope alone,
    // the token revoked as soon as the user has been read, and a refusal that leaves no session
    [Collection("Database")]
    public class SignInTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static string S256(string verifier) =>
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        static IEnumerable<string> SetCookies(HttpResponseMessage response) =>
            response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [];

        static async Task AssertRefusedAsync(MariaDbFixture db, HttpResponseMessage response, string reason)
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/denied?reason=" + reason, SignInSteps.PathOf(response.Headers.Location));
            Assert.DoesNotContain(SetCookies(response), cookie => cookie.StartsWith(DashboardAuth.SessionCookie + "=", StringComparison.Ordinal));
            Assert.Empty(await db.SessionsAsync());
        }

        // Fails if PKCE is off, the order changes, or the revoke is missing
        [Fact]
        public async Task OwnerSignsIn()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor();

            var authorize = await SignInSteps.StartAsync(owner);

            Assert.Equal("code", authorize.Query["response_type"]);
            Assert.Equal(DashboardFactory.ClientId, authorize.Query["client_id"]);
            Assert.Equal("identify", authorize.Query["scope"]);
            Assert.Equal("https://dashboard.test/signin-discord", authorize.RedirectUri);
            Assert.Equal("S256", authorize.Query["code_challenge_method"]);
            Assert.Matches("^[A-Za-z0-9_-]{43}$", authorize.CodeChallenge);
            Assert.NotEmpty(authorize.State);

            var code = factory.Discord.Expect(authorize.CodeChallenge, TestUsers.Owner);
            var callback = await SignInSteps.CallbackAsync(owner, code, authorize.State);

            Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
            Assert.Equal("/admin", SignInSteps.PathOf(callback.Headers.Location));

            var overview = await owner.Client.GetAsync("/admin", Token);
            var html = await overview.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
            Assert.Contains("with the Owner role", html);
            Assert.Contains("Test Owner", html);

            var calls = factory.Discord.Calls;
            Assert.Equal([Endpoint.Token, Endpoint.User, Endpoint.Revoke], calls.Select(call => call.Endpoint));

            var exchange = calls[0];
            Assert.Equal(code, exchange.Form["code"]);
            Assert.Equal(authorize.CodeChallenge, S256(exchange.Form["code_verifier"]));
            Assert.Equal(factory.ClientSecret, exchange.Form["client_secret"]);
            Assert.Equal("https://dashboard.test/signin-discord", exchange.Form["redirect_uri"]);

            var accessToken = calls[1].Authorization!["Bearer ".Length..];
            Assert.StartsWith("Bearer ", calls[1].Authorization);

            var revoke = calls[2];
            Assert.StartsWith("Basic ", revoke.Authorization);
            Assert.Equal(accessToken, revoke.Form["token"]);
            Assert.Equal("access_token", revoke.Form["token_type_hint"]);
            Assert.True(factory.Discord.IsRevoked(accessToken));

            var signedIn = Assert.Single(await db.AuditAsync());
            Assert.Equal("auth.signin", signedIn.action);
            Assert.Equal(AuditRole.Owner, signedIn.actor_role);
        }

        [Fact]
        public async Task AdminSignsIn()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = factory.Visitor();

            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            var html = await admin.Client.GetStringAsync("/admin", Token);
            Assert.Contains("with the Admin role", html);
            Assert.Contains("Test Admin", html);
            Assert.Equal(AuditRole.Admin, Assert.Single(await db.AuditAsync()).actor_role);
        }

        [Fact]
        public async Task UnlistedAccountIsRefused()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);

            var response = await SignInSteps.SignInAsync(factory, factory.Visitor(), TestUsers.Visitor);

            await AssertRefusedAsync(db, response, "unlisted");

            var refused = Assert.Single(await db.AuditAsync());
            Assert.Equal("auth.refused", refused.action);
            Assert.Equal(AuditOutcome.Refused, refused.outcome);
            Assert.Equal(AuditRole.Visitor, refused.actor_role);
            Assert.Equal(TestUsers.Visitor.Id, refused.actor_id);
            Assert.Equal("Test Visitor", refused.actor_name);
            Assert.Equal("session", refused.target_type);

            // The token was still revoked
            var revoke = Assert.Single(factory.Discord.CallsTo(Endpoint.Revoke));
            Assert.True(factory.Discord.IsRevoked(revoke.Form["token"]));
        }

        // Two-factor off, or Discord saying nothing about it. The lists are checked first, so this one is listed.
        [Theory]
        [InlineData(false)]
        [InlineData(null)]
        public async Task ListedAccountWithoutTwoFactorIsRefused(bool? mfaEnabled)
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);

            var response = await SignInSteps.SignInAsync(factory, factory.Visitor(), TestUsers.Admin with { MfaEnabled = mfaEnabled });

            await AssertRefusedAsync(db, response, "mfa");

            var refused = Assert.Single(await db.AuditAsync());
            Assert.Equal("auth.refused", refused.action);
            Assert.Equal(AuditRole.Admin, refused.actor_role);
            Assert.Equal("Discord two-factor is off", refused.detail);
            Assert.Single(factory.Discord.CallsTo(Endpoint.Revoke));
        }

        // An unlisted account learns nothing about the two-factor rule
        [Fact]
        public async Task UnlistedWithoutTwoFactorIsRefusedAsUnlisted()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);

            var response = await SignInSteps.SignInAsync(factory, factory.Visitor(), TestUsers.Visitor with { MfaEnabled = false });

            await AssertRefusedAsync(db, response, "unlisted");
        }

        [Theory]
        [InlineData("identify guilds")]
        [InlineData("identify email")]
        [InlineData("guilds")]
        [InlineData("")]
        public async Task WrongScopeIsRefused(string scope)
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            factory.Discord.GrantedScope = scope;

            var response = await SignInSteps.SignInAsync(factory, factory.Visitor(), TestUsers.Owner);

            await AssertRefusedAsync(db, response, "scope");
            Assert.Empty(factory.Discord.CallsTo(Endpoint.User));
            Assert.Single(factory.Discord.CallsTo(Endpoint.Revoke));

            var refused = Assert.Single(await db.AuditAsync());
            Assert.Null(refused.actor_id);
            Assert.Equal(AuditRole.Visitor, refused.actor_role);
        }

        // Fails on an open redirect
        [Theory]
        [InlineData("https://evil.example.test/")]
        [InlineData("//evil.example.test")]
        [InlineData("/\\evil.example.test")]
        [InlineData("http:evil.example.test")]
        public async Task ExternalReturnUrlIsRefused(string returnUrl)
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var response = await SignInSteps.SignInAsync(factory, factory.Visitor(), TestUsers.Owner, returnUrl);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/admin", SignInSteps.PathOf(response.Headers.Location));
        }

        [Fact]
        public async Task LocalReturnUrlIsKept()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var response = await SignInSteps.SignInAsync(factory, factory.Visitor(), TestUsers.Owner, "/denied?reason=x");

            Assert.Equal("/denied?reason=x", SignInSteps.PathOf(response.Headers.Location));
        }

        // Kestrel won't send a Location header with these, and after Discord the redirect comes once the session has been
        // stored, so the sign-in would fail with a row nobody can use. TestServer doesn't check headers, so this looks at
        // where the redirect goes instead. The characters are written by number, so neither can turn into something else
        // in the source.
        public static TheoryData<string> NonAsciiReturnUrls => new() { "/caf" + (char)0xE9, "/admin" + (char)0x2028 };

        [Theory]
        [MemberData(nameof(NonAsciiReturnUrls))]
        public async Task NonAsciiReturnUrlGoesToTheOverview(string returnUrl)
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var owner = factory.Visitor();

            var signIn = await SignInSteps.SignInAsync(factory, owner, TestUsers.Owner, returnUrl);

            Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
            Assert.Equal(ReturnUrls.Default, SignInSteps.PathOf(signIn.Headers.Location));

            // Someone already signed in skips Discord and goes straight there
            var again = await owner.Client.GetAsync("/signin?returnUrl=" + Uri.EscapeDataString(returnUrl), Token);

            Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
            Assert.Equal(ReturnUrls.Default, SignInSteps.PathOf(again.Headers.Location));
        }

        [Fact]
        public async Task CancelledAtDiscord()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var visitor = factory.Visitor();
            var authorize = await SignInSteps.StartAsync(visitor);

            var response = await visitor.Client.GetAsync($"/signin-discord?error=access_denied&error_description=The+resource+owner+denied+the+request&state={Uri.EscapeDataString(authorize.State)}", Token);

            await AssertRefusedAsync(db, response, "cancelled");
            Assert.Empty(factory.Discord.Calls);
        }

        // The state can't be made without the app's keys, so a changed one is the forgery
        [Fact]
        public async Task BadStateFails()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var visitor = factory.Visitor();
            var authorize = await SignInSteps.StartAsync(visitor);
            var code = factory.Discord.Expect(authorize.CodeChallenge, TestUsers.Owner);

            var middle = authorize.State.Length / 2;
            var changed = authorize.State[..middle] + (authorize.State[middle] == 'A' ? 'B' : 'A') + authorize.State[(middle + 1)..];

            await AssertRefusedAsync(db, await SignInSteps.CallbackAsync(visitor, code, changed), "failed");
            Assert.Empty(factory.Discord.Calls);
        }

        // Another browser can't finish a sign-in this one started: that's how a site would sign someone in as itself
        [Fact]
        public async Task MissingCorrelationCookieFails()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var authorize = await SignInSteps.StartAsync(factory.Visitor());
            var code = factory.Discord.Expect(authorize.CodeChallenge, TestUsers.Owner);

            await AssertRefusedAsync(db, await SignInSteps.CallbackAsync(factory.Visitor(), code, authorize.State), "failed");
            Assert.Empty(factory.Discord.Calls);
        }

        // A code works once, so the exchange is never tried again
        [Fact]
        public async Task TokenEndpointFailureIsNotRetried()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            factory.Discord.Failures[Endpoint.Token] = HttpStatusCode.InternalServerError;

            await AssertRefusedAsync(db, await SignInSteps.SignInAsync(factory, factory.Visitor(), TestUsers.Owner), "failed");

            Assert.Single(factory.Discord.CallsTo(Endpoint.Token));
            Assert.Empty(factory.Discord.CallsTo(Endpoint.User));
        }

        [Fact]
        public async Task UserReadFailureRevokesTheToken()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            factory.Discord.Failures[Endpoint.User] = HttpStatusCode.InternalServerError;

            await AssertRefusedAsync(db, await SignInSteps.SignInAsync(factory, factory.Visitor(), TestUsers.Owner), "failed");

            var user = Assert.Single(factory.Discord.CallsTo(Endpoint.User));
            var revoke = Assert.Single(factory.Discord.CallsTo(Endpoint.Revoke));
            Assert.Equal(user.Authorization!["Bearer ".Length..], revoke.Form["token"]);
        }

        [Fact]
        public async Task RevokeThatFailsStillSignsIn()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            factory.Discord.Failures[Endpoint.Revoke] = HttpStatusCode.InternalServerError;

            var key = await SignInSteps.SignedInAsync(factory, factory.Visitor(), TestUsers.Owner);

            Assert.NotNull(await db.SessionAsync(key));
            Assert.Contains(factory.Logs.Entries, entry => entry.Message.Contains("couldn't be revoked", StringComparison.Ordinal) || entry.Message.Contains("asked to revoke", StringComparison.Ordinal));
        }

        // A revoke that never answers is given up after its own 5 seconds. Only Discord's callback is timed, once the app
        // has started, so the client's own 10-second timeout would fail this.
        [Fact]
        public async Task RevokeThatNeverAnswersIsGivenUp()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            factory.Discord.Hangs[Endpoint.Revoke] = true;
            var owner = factory.Visitor();
            var authorize = await SignInSteps.StartAsync(owner);
            var code = factory.Discord.Expect(authorize.CodeChallenge, TestUsers.Owner);
            var watch = Stopwatch.StartNew();

            var callback = await SignInSteps.CallbackAsync(owner, code, authorize.State);
            var elapsed = watch.Elapsed;

            Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
            Assert.Equal(ReturnUrls.Default, SignInSteps.PathOf(callback.Headers.Location));
            Assert.InRange(elapsed, TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(9));

            var key = owner.SessionKey;
            Assert.NotNull(key);
            Assert.NotNull(await db.SessionAsync(key));
        }

        // The fake answers 401 to a revoked token, so this fails if the revoke came before the user was read
        [Fact]
        public async Task RevokedTokenIsNeverUsedAgain()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var owner = factory.Visitor();
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var calls = factory.Discord.Calls.Count;

            for (var i = 0; i < 3; i++)
                Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync("/admin", Token)).StatusCode);

            Assert.Equal(3, calls);
            Assert.Equal(calls, factory.Discord.Calls.Count);
        }

        [Fact]
        public async Task TokensAreNeverSaved()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var key = await SignInSteps.SignedInAsync(factory, factory.Visitor(), TestUsers.Owner);
            var row = (await db.SessionAsync(key))!;

            var protector = factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(DbTicketStore.TicketPurpose).CreateProtector(key);
            var ticket = TicketSerializer.Default.Deserialize(protector.Unprotect(row.ticket))!;
            var secrets = factory.Discord.Secrets;

            Assert.DoesNotContain(ticket.Properties.Items.Keys, item => item.StartsWith(".Token.", StringComparison.Ordinal));
            Assert.DoesNotContain(ticket.Principal.Claims, claim => secrets.Contains(claim.Value));

            foreach (var secret in secrets)
            {
                var bytes = Encoding.UTF8.GetBytes(secret);

                Assert.True(row.ticket.AsSpan().IndexOf(bytes) < 0);
                Assert.DoesNotContain(secret, row.user_name + row.ip + row.user_agent);
            }
        }

        [Fact]
        public async Task AlreadySignedInSkipsDiscord()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var owner = factory.Visitor();
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var calls = factory.Discord.Calls.Count;

            var response = await owner.Client.GetAsync("/signin?returnUrl=%2Fdenied", Token);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/denied", SignInSteps.PathOf(response.Headers.Location));
            Assert.Equal(calls, factory.Discord.Calls.Count);
        }

        [Fact]
        public async Task HandlerSettings()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var oauth = factory.Services.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get(DashboardAuth.DiscordScheme);

            Assert.Equal(TimeSpan.FromSeconds(10), oauth.BackchannelTimeout);
            Assert.Equal(TimeSpan.FromSeconds(10), oauth.Backchannel.Timeout);
            Assert.True(oauth.UsePkce);
            Assert.False(oauth.SaveTokens);
            Assert.Equal(["identify"], oauth.Scope);
            Assert.Equal("/signin-discord", oauth.CallbackPath.Value);
            Assert.Equal(DashboardAuth.CookieScheme, oauth.SignInScheme);
            Assert.Equal("__Host-mb-signin.", oauth.CorrelationCookie.Name);
            Assert.Equal("/", oauth.CorrelationCookie.Path);

            var discordClient = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(DiscordUserApi));
            Assert.Equal(TimeSpan.FromSeconds(10), discordClient.Timeout);
        }

        // The handler's own failure text, without the response headers and body that a non-standard token error carries
        [Theory]
        [InlineData("OAuth token endpoint failure: Status: BadGateway;Headers: Set-Cookie: __dcfduid=made-up;Body: {};", "OAuth token endpoint failure: Status: BadGateway")]
        [InlineData("The oauth state was missing or invalid.", "The oauth state was missing or invalid.")]
        [InlineData("invalid_grant;Description=Invalid code", "invalid_grant;Description=Invalid code")]
        public void FailureIsDescribedWithoutWhatDiscordSent(string message, string described)
        {
            Assert.Equal(described, DiscordSignInEvents.Describe(new InvalidOperationException(message)));
            Assert.Equal(300, DiscordSignInEvents.Describe(new InvalidOperationException(new string('x', 1000))).Length);
            Assert.Equal("no reason given", DiscordSignInEvents.Describe(null));
        }

        // __Host-, so another site under murderminershub.com can't plant one; Lax, so it comes back with Discord's redirect
        [Fact]
        public async Task CorrelationCookieIsAHostCookie()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var response = await factory.Visitor().Client.GetAsync("/signin", Token);

            var cookie = Assert.Single(SetCookies(response), header => header.StartsWith("__Host-mb-signin.", StringComparison.Ordinal));
            var attributes = cookie.Split(';').Skip(1).Select(attribute => attribute.Trim().ToLowerInvariant()).ToList();

            Assert.Contains("secure", attributes);
            Assert.Contains("httponly", attributes);
            Assert.Contains("samesite=lax", attributes);
            Assert.Contains("path=/", attributes);
            Assert.DoesNotContain(attributes, attribute => attribute.StartsWith("domain", StringComparison.Ordinal));
        }
    }
}
