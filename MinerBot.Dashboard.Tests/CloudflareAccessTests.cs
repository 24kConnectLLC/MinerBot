using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Tests.Support;
using static MinerBot.Dashboard.Security.CloudflareAccessToken;

namespace MinerBot.Dashboard.Tests
{
    // The whole app with the Cloudflare Access check on, and the fake Cloudflare as the key server: what needs the token,
    // that the check comes before sign-in, and what a refused visitor sees. With the settings empty, nothing changes.
    [Collection("Database")]
    public partial class CloudflareAccessTests(MariaDbFixture fixture, AccessTestKeys keys) : IClassFixture<AccessTestKeys>
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        const string Refusal = "This part of the dashboard is behind Cloudflare Access, and this request didn't come through it. Open https://dashboard.test/admin and sign in with Cloudflare first.";

        // Written out, in the case Cloudflare's path rule has
        const string AdminPath = "/admin";

        // The app with the fake as Cloudflare, on its clock. On unless told otherwise, and with the fixture's databases
        // unless it's given one that's down. A test can add services of its own after the fake's.
        static DashboardFactory Factory(MariaDbFixture db, FakeCloudflareAccess access, bool on = true, DeadDatabase? dead = null, Action<IServiceCollection>? more = null)
        {
            var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                SiteConnection = dead?.SiteConnection,
                BotConnection = dead?.BotConnection,
                Settings = on
                    ? new Dictionary<string, string?> { ["CloudflareAccess:TeamDomain"] = FakeCloudflareAccess.TeamDomain, ["CloudflareAccess:Audience"] = access.Audience }
                    : new Dictionary<string, string?>(),
                Services = services =>
                {
                    access.Install(services);
                    more?.Invoke(services);
                }
            });

            access.Clock = factory.Clock;
            return factory;
        }

        // The admin overview at a second address outside /admin. It keeps the Admin folder's policy, so only the check's
        // reading of the page's policy can guard it there.
        static void ElsewhereToo(IServiceCollection services) =>
            services.Configure<RazorPagesOptions>(options => options.Conventions.AddPageRoute("/Admin/Index", "elsewhere"));

        // For the requests that never reach the database: the app on one that isn't there, so these need neither the
        // fixture's database nor Docker
        static DashboardFactory WithoutDatabase(MariaDbFixture db, FakeCloudflareAccess access, Action<IServiceCollection>? more = null) =>
            Factory(db, access, dead: new DeadDatabase(), more: more);

        static Task<HttpResponseMessage> GetAsync(TestBrowser browser, string path, params string[] tokens)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);

            foreach (var token in tokens)
                request.Headers.TryAddWithoutValidation(CloudflareAccessCheck.HeaderName, token);

            return browser.Client.SendAsync(request, Token);
        }

        // Off, an anonymous visitor to /admin is sent to sign in as before, the header is ignored, and Cloudflare is never asked
        [Fact]
        public async Task OffChangesNothing()
        {
            var access = new FakeCloudflareAccess(keys);
            await using var factory = Factory(await fixture.ReadyAsync(), access, on: false);

            var response = await GetAsync(factory.Visitor(), "/admin", access.Mint(signWith: keys.Other));

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/signin?returnUrl=%2Fadmin", SignInSteps.PathOf(response.Headers.Location));
            Assert.Equal(0, access.Fetches);
            Assert.Contains(factory.Logs.Entries, entry => entry.Message == "The Cloudflare Access check is off");
        }

        // The start says so, and nothing asks Cloudflare until an admin page is opened
        [Fact]
        public async Task OnSaysSoAndFetchesNothingUntilAnAdminPage()
        {
            var access = new FakeCloudflareAccess(keys);
            await using var factory = Factory(await fixture.ReadyAsync(), access);
            var visitor = factory.Visitor("203.0.113.80");

            foreach (var path in new[] { "/", "/denied", "/error", "/css/site.css", "/signin" })
                Assert.NotEqual(HttpStatusCode.Forbidden, (await GetAsync(visitor, path)).StatusCode);

            Assert.Equal(0, access.Fetches);
            Assert.Contains(factory.Logs.Entries, entry => entry.Message == "The Cloudflare Access check is on for " + FakeCloudflareAccess.TeamDomain);

            Assert.Equal(HttpStatusCode.Redirect, (await GetAsync(visitor, "/admin", access.Mint())).StatusCode);
            Assert.Equal(1, access.Fetches);
        }

        // Refused before sign-in: an anonymous visitor gets the 403, not the redirect to sign in, so no session is looked up
        [Fact]
        public async Task MissingOrDoubledHeaderIsRefusedBeforeSignIn()
        {
            var access = new FakeCloudflareAccess(keys);
            await using var factory = Factory(await fixture.ReadyAsync(), access);
            var visitor = factory.Visitor("203.0.113.81");

            var missing = await GetAsync(visitor, "/admin");
            var doubled = await GetAsync(visitor, "/admin", access.Mint(), access.Mint());

            foreach (var response in new[] { missing, doubled })
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType?.ToString());
                Assert.Equal(Refusal, await response.Content.ReadAsStringAsync(Token));
                SecurityHeaderTests.AssertHeaders("refused", response);
            }

            Assert.Contains(factory.Logs.Entries, entry => entry.Message.Contains("refused by the Cloudflare Access check: missing (request ", StringComparison.Ordinal));
            Assert.Contains(factory.Logs.Entries, entry => entry.Message.Contains("refused by the Cloudflare Access check: more than one (request ", StringComparison.Ordinal));
        }

        // Before sign-in, so a refusal costs no session lookup: with the database down, a visitor with a session cookie and
        // no token still gets the check's 403. With a good token the session is looked up, and the database's 503 shows.
        // Fails if the check moves after UseAuthentication.
        [Fact]
        public async Task RefusalCostsNoSessionLookup()
        {
            var access = new FakeCloudflareAccess(keys);
            await using var factory = Factory(await fixture.ReadyAsync(), access, dead: new DeadDatabase());
            var visitor = factory.Visitor("203.0.113.93");
            visitor.Jar.Add(DashboardFactory.PublicAddress, new Cookie(DashboardAuth.SessionCookie, SessionKeys.New()) { Secure = true });

            var refused = await GetAsync(visitor, "/admin");
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(Refusal, await refused.Content.ReadAsStringAsync(Token));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await GetAsync(visitor, "/admin", access.Mint())).StatusCode);
        }

        // Both are needed: the token gets a visitor past Access, and the session past sign-in
        [Fact]
        public async Task ValidTokenStillNeedsASession()
        {
            var access = new FakeCloudflareAccess(keys);
            await using var factory = Factory(await fixture.ReadyAsync(), access);

            var anonymous = await GetAsync(factory.Visitor("203.0.113.82"), "/admin", access.Mint());
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.Equal("/signin?returnUrl=%2Fadmin", SignInSteps.PathOf(anonymous.Headers.Location));

            var admin = factory.Visitor("203.0.113.83");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            var overview = await GetAsync(admin, "/admin", access.Mint());
            Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
            Assert.Contains("You're signed in as Test Admin", WebUtility.HtmlDecode(await overview.Content.ReadAsStringAsync(Token)));

            // And a session alone isn't enough
            Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(admin, "/admin")).StatusCode);
        }

        // A signed-in Admin with each kind of bad token is refused, each with its reason in the log
        [Fact]
        public async Task EachBadTokenIsRefused()
        {
            var access = new FakeCloudflareAccess(keys);
            await using var factory = Factory(await fixture.ReadyAsync(), access);
            var admin = factory.Visitor("203.0.113.84");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            var now = factory.Clock.GetUtcNow().ToUnixTimeSeconds();

            var tokens = new (string Token, string Reason)[]
            {
                (access.Mint(claims => claims["exp"] = now - 60), Expired),
                (access.Mint(claims => claims["nbf"] = now + 61), NotValidYet),
                (access.Mint(claims => claims["aud"] = new[] { new string('0', 64) }), WrongAudience),
                (access.Mint(claims => claims["iss"] = "https://evil-team.cloudflareaccess.com"), WrongIssuer),
                (access.Mint(header: head => head["alg"] = "RS512", hash: HashAlgorithmName.SHA512), NotRs256),
                (access.Mint(header: head => head.Remove("kid")), Unreadable),
                (access.Mint(signWith: keys.Other), BadSignature),
                (access.Mint(header: head => head["kid"] = "made-up-key"), UnknownKey),
                ("not-a-token", Unreadable),
                (new string('a', MaxLength + 1), TooLong)
            };

            foreach (var (token, reason) in tokens)
            {
                var response = await GetAsync(admin, "/admin", token);

                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"A token that's {reason} got {response.StatusCode}");
                Assert.Equal(Refusal, await response.Content.ReadAsStringAsync(Token));
                Assert.Contains(factory.Logs.Entries, entry => entry.Message.Contains($"refused by the Cloudflare Access check: {reason} (request ", StringComparison.Ordinal));
            }

            // The same session with a good token still gets in
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(admin, "/admin", access.Mint())).StatusCode);
        }

        // With Cloudflare unreachable and no keys from before, nothing can be checked, so nothing is shown
        [Fact]
        public async Task UnreachableCloudflareGives503()
        {
            var access = new FakeCloudflareAccess(keys);
            access.FailWith(AccessFailure.Throw);
            await using var factory = Factory(await fixture.ReadyAsync(), access);
            var admin = factory.Visitor("203.0.113.85");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            var response = await GetAsync(admin, "/admin", access.Mint());

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(["60"], response.Headers.GetValues("Retry-After"));
            Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType?.ToString());
            Assert.Equal("Cloudflare Access couldn't be checked just now, so this page isn't shown. Try again in a minute.", await response.Content.ReadAsStringAsync(Token));
            SecurityHeaderTests.AssertHeaders("unavailable", response);
        }

        // However the address is written, an admin page needs the token, and an address below /admin that isn't a page
        // too. The public pages, the stylesheet, Discord's callback and the health checks never do.
        [Fact]
        public async Task OnlyAdminAddressesNeedTheToken()
        {
            var access = new FakeCloudflareAccess(keys);
            await using var factory = Factory(await fixture.ReadyAsync(), access);
            var visitor = factory.Visitor("203.0.113.86");

            foreach (var path in new[] { "/admin", "/ADMIN", "/Admin/", "/%61dmin", "/admin/nothing-here" })
                Assert.True((await GetAsync(visitor, path)).StatusCode == HttpStatusCode.Forbidden, $"{path} was let through");

            foreach (var path in new[] { "/", "/signin", "/signin-discord", "/denied", "/error", "/css/site.css" })
                Assert.True((await GetAsync(visitor, path)).StatusCode != HttpStatusCode.Forbidden, $"{path} was refused");

            using var local = factory.Local();
            Assert.Equal(HttpStatusCode.OK, (await local.GetAsync("/health/live", Token)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await local.GetAsync("/health/ready", Token)).StatusCode);
        }

        // Where the check sits in the pipeline. Each of these fails if it moves: before routing, before the proxy key,
        // before the limits, or before the origin check.

        // A page with the Admin policy needs the token wherever it is, not only under /admin. That works only because the
        // check runs after routing and sees the page's policy. Fails if the check runs before routing.
        [Fact]
        public async Task AdminPageAtAnotherAddressStillNeedsTheToken()
        {
            var access = new FakeCloudflareAccess(keys);
            await using var factory = WithoutDatabase(fixture, access, ElsewhereToo);
            var visitor = factory.Visitor("203.0.113.94");

            var refused = await GetAsync(visitor, "/elsewhere");
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(Refusal, await refused.Content.ReadAsStringAsync(Token));

            // With a token it's the admin page, so sign-in comes next: the address does reach the page
            var withToken = await GetAsync(visitor, "/elsewhere", access.Mint());
            Assert.Equal(HttpStatusCode.Redirect, withToken.StatusCode);
            Assert.Equal("/signin?returnUrl=%2Felsewhere", SignInSteps.PathOf(withToken.Headers.Location));
        }

        // And a session alone isn't enough there: a signed-in Admin without the token is refused, and with it gets the page
        [Fact]
        public async Task SignedInAdminAtAnotherAddressStillNeedsTheToken()
        {
            var access = new FakeCloudflareAccess(keys);
            await using var factory = Factory(await fixture.ReadyAsync(), access, more: ElsewhereToo);
            var admin = factory.Visitor("203.0.113.95");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            var refused = await GetAsync(admin, "/elsewhere");
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(Refusal, await refused.Content.ReadAsStringAsync(Token));

            var page = await GetAsync(admin, "/elsewhere", access.Mint());
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("You're signed in as Test Admin", WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync(Token)));
        }

        // The proxy key comes first, so anything on the server that calls the dashboard without it gets the bare 404 for
        // /admin too: no refusal naming the public address, no log line and no key fetch, whatever token it sends. Fails if
        // the check moves ahead of the proxy key.
        [Fact]
        public async Task AdminAddressWithoutTheProxyKeyIsTheBare404()
        {
            var access = new FakeCloudflareAccess(keys);
            await using var factory = WithoutDatabase(fixture, access);
            var outsider = factory.Visitor("203.0.113.96", proxyKey: false);
            var answers = new List<HttpResponseMessage>
            {
                await GetAsync(outsider, "/admin"),
                await GetAsync(outsider, "/admin", access.Mint()),
                await GetAsync(outsider, "/admin", access.Mint(header: head => head["kid"] = "made-up-key"))
            };

            using (var local = factory.Local())
                answers.Add(await local.GetAsync("/admin", Token));

            foreach (var response in answers)
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Empty(await response.Content.ReadAsByteArrayAsync(Token));
            }

            Assert.Equal(0, access.Fetches);
            Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Message.Contains("Cloudflare Access check:", StringComparison.Ordinal));
        }

        // The limits come first, so whoever reaches the dashboard around Access is held to the visitor's limit, refusals and
        // their log lines included, and one over it never reaches the check. Fails if the check moves ahead of the limiter.
        [Fact]
        public async Task RefusedAdminRequestsCountTowardTheVisitorsLimit()
        {
            var access = new FakeCloudflareAccess(keys);
            await using var factory = WithoutDatabase(fixture, access);
            var visitor = factory.Visitor("203.0.113.97");

            for (var i = 0; i < RateLimits.VisitorPerMinute; i++)
                Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(visitor, "/admin")).StatusCode);

            var limited = await GetAsync(visitor, "/admin", access.Mint(header: head => head["kid"] = "made-up-key"));
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Equal(RateLimits.Rejection, await limited.Content.ReadAsStringAsync(Token));

            // Its made-up key was never looked up
            Assert.Equal(0, access.Fetches);
        }

        // A change from another site meets the origin check before any token is looked at. Both are 403s, so this only
        // pins the order. Fails if the check moves ahead of the origin check.
        [Fact]
        public async Task CrossSitePostToAnAdminAddressMeetsTheOriginCheckFirst()
        {
            var access = new FakeCloudflareAccess(keys);
            await using var factory = WithoutDatabase(fixture, access);

            var response = await SignInSteps.PostAsync(factory.Visitor("203.0.113.98"), "/admin", null, origin: "https://murderminershub.com");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(OriginCheck.Refusal, await response.Content.ReadAsStringAsync(Token));
        }

        // No token, good or bad, appears in a log line, a header or a page
        [Fact]
        public async Task NoTokenIsShownOrLogged()
        {
            var access = new FakeCloudflareAccess(keys);
            await using var factory = Factory(await fixture.ReadyAsync(), access);
            var admin = factory.Visitor("203.0.113.87");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            var seen = new List<HttpResponseMessage>
            {
                await GetAsync(admin, "/admin", access.Mint()),
                await GetAsync(admin, "/admin", access.Mint(signWith: keys.Other)),
                await GetAsync(admin, "/admin", access.Mint(claims => claims["exp"] = 1)),
                await GetAsync(admin, "/admin/nothing-here", access.Mint()),
                await GetAsync(admin, "/", access.Mint()),
                await GetAsync(admin, "/nothing-here", access.Mint())
            };

            Assert.Equal(HttpStatusCode.OK, seen[0].StatusCode);
            var logs = factory.Logs.Text;

            foreach (var response in seen)
            {
                var text = string.Join('\n', response.Headers.Concat(response.Content.Headers).SelectMany(header => header.Value)) + await response.Content.ReadAsStringAsync(Token);

                foreach (var token in access.Minted)
                    Assert.False(text.Contains(token, StringComparison.Ordinal), "A response shows a token");
            }

            foreach (var token in access.Minted)
            {
                Assert.False(logs.Contains(token, StringComparison.Ordinal), "A log line holds a token");

                // Nor any part of one, such as the signature
                foreach (var part in token.Split('.').Where(part => part.Length > 20))
                    Assert.False(logs.Contains(part, StringComparison.Ordinal), "A log line holds part of a token");
            }
        }

        // Cloudflare's path rule is "admin", so the app's own links to admin pages, and the sign-in's way back to them, are
        // in small letters. Fails if LowercaseUrls goes, or a link is written in another case.
        [Fact]
        public async Task LinksToAdminPagesAndTheWayBackAreLowercase()
        {
            var access = new FakeCloudflareAccess(keys);
            var db = await fixture.ReadyAsync();
            await db.AddArmorAsync(ArmorSteps.Submission(1));
            await using var factory = Factory(db, access, on: false);

            // Each admin page's address, as the app makes it, with a value for each part of its route, such as an armor's ID
            var links = factory.Services.GetRequiredService<LinkGenerator>();
            var pages = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .Select(endpoint => (Page: endpoint.Metadata.GetMetadata<PageActionDescriptor>()?.ViewEnginePath, endpoint.RoutePattern.Parameters))
                .Where(page => page.Page?.StartsWith("/Admin", StringComparison.OrdinalIgnoreCase) == true)
                .DistinctBy(page => page.Page)
                .ToList();

            Assert.NotEmpty(pages);
            Assert.Contains(pages, page => page.Parameters.Count > 0);

            foreach (var (page, parameters) in pages)
            {
                var path = links.GetPathByPage(page!, values: new RouteValueDictionary(parameters.ToDictionary(parameter => parameter.Name, _ => (object?)1)));

                Assert.NotNull(path);
                Assert.StartsWith(AdminPath, path, StringComparison.Ordinal);
                Assert.Equal(path.ToLowerInvariant(), path);
            }

            // The sign-in's way back, and the way to sign in from an admin page
            var admin = factory.Visitor("203.0.113.88");
            var signIn = await SignInSteps.SignInAsync(factory, admin, TestUsers.Admin);
            Assert.Equal(AdminPath, SignInSteps.PathOf(signIn.Headers.Location));

            var anonymous = factory.Visitor("203.0.113.89");
            Assert.Equal("/signin?returnUrl=%2Fadmin", SignInSteps.PathOf((await anonymous.Client.GetAsync("/admin", Token)).Headers.Location));

            // Every link and form on the pages, signed in and not, that leads to an admin page, directly or as the way back
            var html = string.Join('\n',
                await anonymous.Client.GetStringAsync("/", Token),
                await anonymous.Client.GetStringAsync("/denied?reason=mfa", Token),
                await anonymous.Client.GetStringAsync("/signout", Token),
                await admin.Client.GetStringAsync("/", Token),
                await admin.Client.GetStringAsync("/admin", Token),
                await admin.Client.GetStringAsync("/admin/armor", Token),
                await admin.Client.GetStringAsync("/admin/armor/1", Token),
                await admin.Client.GetStringAsync("/signout", Token));

            var targets = Targets().Matches(html)
                .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))
                .Select(WhereItLeads)
                .Where(target => target.StartsWith(AdminPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.NotEmpty(targets);
            Assert.All(targets, target => Assert.StartsWith(AdminPath, target, StringComparison.Ordinal));
        }

        // A link's path, or for a sign-in link, the path it returns to
        static string WhereItLeads(string link)
        {
            var path = link.Split('?', 2)[0];

            if (path.Equals("/signin", StringComparison.OrdinalIgnoreCase) && link.Contains('?'))
                return QueryHelpers.ParseQuery(link[link.IndexOf('?')..]).TryGetValue("returnUrl", out var back) ? back.ToString() : path;

            return path;
        }

        [GeneratedRegex("(?:href|action)=\"([^\"]*)\"")]
        private static partial Regex Targets();
    }
}
