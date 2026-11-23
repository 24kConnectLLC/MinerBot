using System.Buffers.Text;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Hosting;
using MinerBot.Dashboard.Requests;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Tests.Support
{
    // A request as a page's handler sees it, for calling the sender as a page does: a scope of the app's own services and an
    // HttpContext signed in as the browser is. Disposing it ends the scope.
    internal sealed class RequestScope(AsyncServiceScope scope, HttpContext context) : IAsyncDisposable
    {
        public HttpContext Context => context;

        public Task<SendOutcome> SendAsync(SendOrder order) => context.RequestServices.GetRequiredService<RequestSender>().SendAsync(order, context);

        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }

    // Requests to the bot in a test: written as the dashboard writes one, signed with the factory's key, and played as the bot
    // plays it, through the bot's own repository on the fixture, so pages read what the two apps really write
    internal static partial class RequestSteps
    {
        // The bot's run in these tests, and an earlier one
        public const ulong Run = StatusSteps.RunId;
        public const ulong OtherRun = 42;

        // A server the requests can be about, which StatusSteps.ServersAsync puts in the copy, and one of its channels
        public static readonly ulong ServerId = StatusSteps.Id(StatusSteps.ServerId);
        public const ulong OtherServerId = 1000000000000000703;
        public const ulong ChannelId = 3001;

        // The words a form's box is typed with, for each kind that needs one, and the text each kind takes
        public static Confirmation? Typed(string kind) => kind switch
        {
            RequestKinds.BotRestart => new Confirmation.Word("restart"),
            RequestKinds.PostsPause => new Confirmation.Word("pause"),
            RequestKinds.SteamReconnect => new Confirmation.Word("reconnect"),
            RequestKinds.SteamRenew => new Confirmation.Word("renew"),
            RequestKinds.GuildLeave => new Confirmation.Server(StatusSteps.ServerId, "Test Server", ServerId),
            RequestKinds.StatusSet => new Confirmation.StatusText(),
            _ => null
        };

        public static string? Text(string kind) => RequestKinds.Find(kind)!.Argument switch
        {
            RequestArgument.Reason => "Testing the controls",
            RequestArgument.StatusText => "Back soon",
            RequestArgument.CustomLinkName => "gabelogannewell",
            _ => null
        };

        // An order a page would make for the kind, filled in and confirmed as a form would be, with a new key
        public static SendOrder Order(string kind, ulong? guildId = null)
        {
            var details = RequestKinds.Find(kind)!;

            return new SendOrder(kind, RequestFields.NewKey(), details.NeedsServer ? guildId ?? ServerId : null, details.NeedsChannel ? ChannelId : null, Text(kind),
                Typed(kind));
        }

        // As the dashboard writes one, at the time given, by the person given, signed with the factory's key
        public static Task<ulong> AddAsync(MariaDbFixture db, DashboardFactory factory, string kind, DateTime at, TestUser? user = null, ulong? guildId = null,
            string? argument = null, string? name = null, string? key = null)
        {
            var details = RequestKinds.Find(kind)!;
            user ??= TestUsers.Owner;
            var role = user.Id == TestUsers.Owner.Id ? RequestRoles.Owner : RequestRoles.Admin;
            var fields = RequestFields.New(key ?? RequestFields.NewKey(), details, user.Id, name ?? user.DisplayName, role, details.NeedsServer ? guildId ?? ServerId : null,
                details.NeedsChannel ? ChannelId : null, argument ?? Text(kind), at);
            var signature = RequestSigning.Sign(Convert.FromHexString(factory.RequestSigningKey), fields);
            var queue = db.RequestQueue();

            return queue.RunInTransaction(transaction => queue.AddRequest(fields, signature, transaction));
        }

        // The bot's part: taking a request, ending one it took, deciding one without taking it, and its message to the owner
        public static async Task ClaimAsync(MariaDbFixture db, ulong id, DateTime at, ulong run = Run) =>
            Assert.True(await db.Requests().Claim(id, run, at), $"The bot couldn't take request {id}");

        public static async Task FinishAsync(MariaDbFixture db, ulong id, string state, string result, DateTime at, ulong? number = null, ulong run = Run) =>
            Assert.True(await db.Requests().Finish(id, run, state, result, number, at), $"The bot couldn't end request {id}");

        public static async Task DecideAsync(MariaDbFixture db, ulong id, string state, string result, DateTime at, ulong run = Run) =>
            Assert.True(await db.Requests().Decide(id, run, state, result, at), $"The bot couldn't decide request {id}");

        public static async Task DmAsync(MariaDbFixture db, ulong id, string dm) =>
            Assert.True(await db.Requests().SetDm(id, dm), $"The bot couldn't record its message about request {id}");

        // Requests a person asked for at the times given, as the table holds them once the bot refused them, written as root in
        // one call: what a person's counts read, which nothing verifies again once the bot has decided it
        public static Task AddRefusedAsync(MariaDbFixture db, string kind, TestUser user, IEnumerable<DateTime> times)
        {
            var details = RequestKinds.Find(kind)!;
            var role = user.Id == TestUsers.Owner.Id ? RequestRoles.Owner : RequestRoles.Admin;

            var rows = times.Select(at => new
            {
                key = RequestFields.NewKey(),
                kind,
                actor = user.Id,
                name = user.DisplayName,
                role,
                guild = details.NeedsServer ? (ulong?)ServerId : null,
                channel = details.NeedsChannel ? (ulong?)ChannelId : null,
                argument = Text(kind),
                at,
                expires = at + details.Expiry,
                signature = new byte[RequestSigning.SignatureLength],
                run = Run
            }).ToList();

            return db.RootExecuteAsync($@"INSERT INTO {MariaDbFixture.RequestTable} (request_key, kind, actor_id, actor_name, actor_role, guild_id, channel_id, argument,
                created_at, expires_at, signature, state, run_id, ended_at, result)
                VALUES (@key, @kind, @actor, @name, @role, @guild, @channel, @argument, @at, @expires, @signature, 'refused', @run, @at, 'Refused for a test')", rows);
        }

        // Asked for at the time given, then taken a second later
        public static async Task<ulong> TakenAsync(MariaDbFixture db, DashboardFactory factory, string kind, DateTime at, TestUser? user = null, ulong? guildId = null)
        {
            var id = await AddAsync(db, factory, kind, at, user, guildId);
            await ClaimAsync(db, id, at.AddSeconds(1));
            return id;
        }

        // Every request to the bot, as the table holds it
        public static Task<List<BotRequestRow>> RequestsAsync(this MariaDbFixture db) =>
            db.RootQueryAsync<BotRequestRow>($"SELECT * FROM {MariaDbFixture.RequestTable} ORDER BY id");

        // Every request but the sign-ins' messages to the owner, which every sign-in leaves
        public static async Task<List<BotRequestRow>> SentAsync(this MariaDbFixture db) =>
            (await db.RequestsAsync()).Where(row => row.kind != RequestKinds.OwnerSigninAlert).ToList();

        // The request as a page's handler sees it: the browser's session cookie, through the app's own authentication, which
        // reads the session's row, puts its sign-in time on the ticket and adds the role from the lists. The result goes
        // where the authentication middleware puts it, which is where FreshSignIn reads the time.
        public static async Task<RequestScope> ScopeAsync(DashboardFactory factory, TestBrowser browser, string address = "203.0.113.90")
        {
            var scope = factory.Services.CreateAsyncScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, TraceIdentifier = "made-up-trace-" + Guid.NewGuid().ToString("N")[..8] };

            context.Request.Method = HttpMethods.Post;
            context.Request.Scheme = "https";
            context.Request.Host = new HostString(DashboardFactory.PublicAddress.Host);
            context.Connection.RemoteIpAddress = IPAddress.Parse(address);

            if (browser.SessionKey is { } key)
                context.Request.Headers.Cookie = DashboardAuth.SessionCookie + "=" + key;

            var result = await context.AuthenticateAsync();
            context.Features.Set<IAuthenticateResultFeature>(new ResultFeature(result));

            if (result.Succeeded)
                context.User = result.Principal;

            return new RequestScope(scope, context);
        }

        sealed class ResultFeature(AuthenticateResult result) : IAuthenticateResultFeature
        {
            public AuthenticateResult? AuthenticateResult { get; set; } = result;
        }

        // The controls page as a browser got it, signed in, which has to answer 200
        public static async Task<ControlsPage> ControlsAsync(TestBrowser browser, string path = "/admin/bot/controls")
        {
            var response = await browser.Client.GetAsync(path, TestContext.Current.CancellationToken);
            var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.True(response.StatusCode == HttpStatusCode.OK, $"The controls page answered {response.StatusCode}");
            return ReadControls(html);
        }

        // Each form's key by its handler, from the markup the page drew
        public static ControlsPage ReadControls(string html) => new(html, SignInSteps.FormToken(html),
            ControlForm().Matches(html).ToDictionary(match => match.Groups["handler"].Value, match => match.Groups["key"].Value, StringComparer.Ordinal));

        // One of the controls page's forms as a browser sends it from the dashboard, with the key the page drew it with, or a
        // new one for a form the page didn't draw, and the form's other fields
        public static Task<HttpResponseMessage> PostControlAsync(TestBrowser browser, ControlsPage page, string handler, params (string Name, string Value)[] fields) =>
            PostControlAsync(browser, handler, page.Token, page.Keys.GetValueOrDefault(handler) ?? RequestFields.NewKey(), fields);

        // With exactly the key given, none when it's null
        public static Task<HttpResponseMessage> PostControlAsync(TestBrowser browser, string handler, string token, string? key, params (string Name, string Value)[] fields)
        {
            var sent = new List<KeyValuePair<string, string>>();

            if (key != null)
                sent.Add(new("formkey", key));

            sent.AddRange(fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));
            return SignInSteps.PostAsync(browser, "/admin/bot/controls?handler=" + handler, token, origin: DashboardFactory.PublicOrigin, fields: sent);
        }

        // A form's answer when it wasn't sent on: a 302 with no body, to the page's own path with ?notice= and 32 hex digits,
        // never the form's handler, so the address names the page, and without a fragment, so the alert at the top shows
        // first, and the notice's cookie. The address, whose GET draws the notice.
        public static async Task<string> NoticeOfAsync(HttpResponseMessage response)
        {
            Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"The form answered {response.StatusCode}");
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

            var location = SignInSteps.PathOf(response.Headers.Location) ?? throw new InvalidOperationException("The redirect has no Location");
            Assert.DoesNotContain('#', location);
            Assert.DoesNotContain("handler=", location, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(response.RequestMessage!.RequestUri!.AbsolutePath, location.Split('?')[0], ignoreCase: true);
            Assert.Matches("^[0-9a-f]{32}$", NoticeId(location));
            Assert.True(NoticeCookie(response) != null, "The form's answer kept no notice");
            return location;
        }

        // The GET of a form's notice, in the same browser, as a browser follows the redirect
        public static async Task<HttpResponseMessage> AnsweredAsync(TestBrowser browser, HttpResponseMessage response) =>
            await browser.Client.GetAsync(await NoticeOfAsync(response), TestContext.Current.CancellationToken);

        // The ID ?notice= gives, or empty
        public static string NoticeId(string location) =>
            QueryHelpers.ParseQuery(location.Contains('?', StringComparison.Ordinal) ? location[location.IndexOf('?', StringComparison.Ordinal)..] : "")[FormNotices.Parameter].ToString();

        // The Set-Cookie that keeps a notice, or null
        public static string? NoticeCookie(HttpResponseMessage response) =>
            SetCookies(response).SingleOrDefault(cookie => cookie.StartsWith(DashboardAuth.NoticeCookie + "=", StringComparison.Ordinal)
                && !cookie.StartsWith(DashboardAuth.NoticeCookie + "=;", StringComparison.Ordinal));

        // Whether an answer deletes the notice's cookie, and whether it writes it at all, kept or deleted
        public static bool DeletesNotice(HttpResponseMessage response) =>
            SetCookies(response).Any(cookie => cookie.StartsWith(DashboardAuth.NoticeCookie + "=;", StringComparison.Ordinal));

        public static bool TouchesNotice(HttpResponseMessage response) =>
            SetCookies(response).Any(cookie => cookie.StartsWith(DashboardAuth.NoticeCookie + "=", StringComparison.Ordinal));

        public static IReadOnlyList<string> SetCookies(HttpResponseMessage response) =>
            response.Headers.TryGetValues("Set-Cookie", out var cookies) ? [.. cookies] : [];

        // The notice the browser holds, opened as the app opens it: with the app's keys and the browser's session key
        public static FormNotice OpenNotice(DashboardFactory factory, TestBrowser browser) =>
            JsonSerializer.Deserialize<FormNotice>(NoticeJson(factory, browser)) ?? throw new InvalidOperationException("The notice holds no JSON");

        // Its JSON, as the cookie carries it, encrypted
        public static byte[] NoticeJson(DashboardFactory factory, TestBrowser browser)
        {
            var cookie = browser.Cookie(DashboardAuth.NoticeCookie) ?? throw new InvalidOperationException("The browser holds no notice");
            var key = browser.SessionKey ?? throw new InvalidOperationException("The browser has no session");

            return factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(FormNotices.Purpose).CreateProtector(key)
                .Unprotect(Base64Url.DecodeFromChars(cookie));
        }

        // A form whose POST failed: a 303 with no body to the error page with why and the request's ID, and that page's GET,
        // in the same browser
        public static async Task<(HttpResponseMessage Page, string RequestId)> FailedAsync(TestBrowser browser, HttpResponseMessage response, string failed)
        {
            Assert.True(response.StatusCode == HttpStatusCode.SeeOther, $"The form answered {response.StatusCode}");
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

            var location = SignInSteps.PathOf(response.Headers.Location) ?? "";
            var match = Regex.Match(location, "^/error\\?failed=" + failed + "&request=(?<id>[^&#]+)$");
            Assert.True(match.Success, $"The form was sent to {location}");

            return (await browser.Client.GetAsync(location, TestContext.Current.CancellationToken), Uri.UnescapeDataString(match.Groups["id"].Value));
        }

        // Whether following the link loads the page, from a browser on the address shown, by HTML's navigate rule: a link
        // whose URL is the document's own but for a fragment it has is a fragment navigation, which only scrolls. So it loads
        // when it has no fragment, or differs from the address shown before the fragment. The link is decoded first, as the
        // page writes & as &amp;.
        public static bool LoadsAgain(string shown, string link)
        {
            var target = WebUtility.HtmlDecode(link);
            return !target.Contains('#', StringComparison.Ordinal) || target.Split('#')[0] != shown.Split('#')[0];
        }

        // A page's Check again, decoded, or null for a page without one
        public static string? CheckAgainLink(string html) => CheckAgain().Match(html) is { Success: true } match ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;

        // Check again as a browser without the script presses it, from the address a form's redirect leads to: that page's
        // link has to load it again, at #result, and so does the link on the page that loads, which leads back to the
        // redirect's address. HttpClient never sends a fragment, so each address is kept with its own.
        public static async Task CheckAgainWalkAsync(TestBrowser browser, HttpResponseMessage sent)
        {
            var token = TestContext.Current.CancellationToken;
            Assert.True(sent.StatusCode == HttpStatusCode.Redirect, $"The form answered {sent.StatusCode}");

            var redirect = SignInSteps.PathOf(sent.Headers.Location) ?? throw new InvalidOperationException("The redirect has no Location");
            Assert.EndsWith("#result", redirect, StringComparison.Ordinal);

            var first = CheckAgainLink(await browser.Client.GetStringAsync(redirect, token)) ?? throw new InvalidOperationException($"{redirect} has no Check again");
            Assert.True(LoadsAgain(redirect, first), $"Check again on {redirect} is {first}, which only scrolls");
            Assert.EndsWith("#result", first, StringComparison.Ordinal);

            var second = CheckAgainLink(await browser.Client.GetStringAsync(first, token)) ?? throw new InvalidOperationException($"{first} has no Check again");
            Assert.True(LoadsAgain(first, second), $"Check again on {first} is {second}, which only scrolls");
            Assert.EndsWith("#result", second, StringComparison.Ordinal);
            Assert.Equal(redirect.Split('#')[0], second.Split('#')[0]);
        }

        // A form of the controls page: the handler its action names, and its key, its first field
        [GeneratedRegex("""action="/admin/bot/controls\?handler=(?<handler>[A-Za-z]+)"[^>]*>\s*<input type="hidden" name="formkey" value="(?<key>[0-9a-f]{32})" />""")]
        private static partial Regex ControlForm();

        [GeneratedRegex("""<a href="([^"]*)">Check again</a>""")]
        private static partial Regex CheckAgain();
    }

    // The controls page as a browser got it: its markup, the forms' antiforgery token, and each form's key by its handler
    internal sealed record ControlsPage(string Html, string Token, IReadOnlyDictionary<string, string> Keys)
    {
        public string Text => WebUtility.HtmlDecode(Html);

        // The handlers whose forms the page drew, in its order
        public IReadOnlyCollection<string> Handlers => [.. Keys.Keys];
    }
}
