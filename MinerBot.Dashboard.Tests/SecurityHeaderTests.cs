using System.Net;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // Every kind of response the dashboard gives has every header, with exactly these values, including the bare 404 of
    // the proxy key, the rate limit's 429 and the error page of a database that's down. Each also asks Cloudflare not to
    // transform it, which keeps Cloudflare from compressing it.
    [Collection("Database")]
    public class SecurityHeaderTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        // Written out here rather than read from the app, so a change to the policy has to change this too
        static readonly Dictionary<string, string> Expected = new()
        {
            ["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' https://cdn.discordapp.com https://murderminershub.com/wp-content/uploads/ https://www.murderminershub.com/wp-content/uploads/; connect-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'; object-src 'none'",
            ["Strict-Transport-Security"] = "max-age=31536000",
            ["X-Content-Type-Options"] = "nosniff",
            ["Referrer-Policy"] = "same-origin",
            ["X-Frame-Options"] = "DENY",
            ["Cross-Origin-Opener-Policy"] = "same-origin",
            ["Cross-Origin-Resource-Policy"] = "same-origin",
            ["Permissions-Policy"] = "camera=(), geolocation=(), microphone=(), payment=(), usb=()",
            ["X-Robots-Tag"] = "noindex, nofollow"
        };

        static IReadOnlyList<string> Values(HttpResponseMessage response, string name) =>
            response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values) ? values.ToList() : [];

        // Cache-Control's directives, in whatever order HttpClient puts them
        static IReadOnlyList<string> Directives(HttpResponseMessage response) =>
            Values(response, "Cache-Control").SelectMany(value => value.Split(',')).Select(directive => directive.Trim()).Where(directive => directive.Length > 0).ToList();

        // Every header with its value, never stored unless it's the stylesheet, and never transformed, so Cloudflare
        // compresses nothing. CloudflareAccessTests checks its refusals with this too.
        internal static void AssertHeaders(string what, HttpResponseMessage response, bool cacheable = false)
        {
            foreach (var (name, value) in Expected)
                Assert.True(Values(response, name) is [var only] && only == value, $"{what}: {name} is [{string.Join(" | ", Values(response, name))}]");

            var cacheControl = string.Join(",", Values(response, "Cache-Control"));

            if (cacheable)
            {
                Assert.Contains("max-age=604800", cacheControl);
                Assert.DoesNotContain("no-store", cacheControl);
            }
            else
            {
                Assert.True(cacheControl.Contains("no-store", StringComparison.Ordinal), $"{what}: Cache-Control is {cacheControl}");
            }

            Assert.True(Directives(response).Count(directive => directive == "no-transform") == 1, $"{what}: Cache-Control is {cacheControl}");

            Assert.Empty(Values(response, "Server"));
            Assert.Empty(Values(response, "X-Powered-By"));
        }

        [Fact]
        public void PolicyAllowsOnlyTheDashboardAndTheImageHosts()
        {
            Assert.Equal(Expected["Content-Security-Policy"], SecurityHeaders.ContentSecurityPolicy);
        }

        [Fact]
        public async Task EveryResponseHasEveryHeader()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var visitor = factory.Visitor();
            var responses = new List<(string What, HttpResponseMessage Response, HttpStatusCode Status, bool Cacheable)>();

            async Task Add(string what, Task<HttpResponseMessage> request, HttpStatusCode status, bool cacheable = false) =>
                responses.Add((what, await request, status, cacheable));

            await Add("home", visitor.Client.GetAsync("/", Token), HttpStatusCode.OK);
            await Add("sign-in", visitor.Client.GetAsync("/signin", Token), HttpStatusCode.Redirect);
            await Add("denied", visitor.Client.GetAsync("/denied?reason=unlisted", Token), HttpStatusCode.OK);
            await Add("error", visitor.Client.GetAsync("/error", Token), HttpStatusCode.OK);
            await Add("unknown page", visitor.Client.GetAsync("/nothing-here", Token), HttpStatusCode.NotFound);
            await Add("no proxy key", factory.Visitor(proxyKey: false).Client.GetAsync("/", Token), HttpStatusCode.NotFound);
            await Add("refused origin", SignInSteps.PostAsync(visitor, "/signout", "made-up", origin: "https://murderminershub.com"), HttpStatusCode.Forbidden);
            await Add("stylesheet", visitor.Client.GetAsync("/css/site.css", Token), HttpStatusCode.OK, cacheable: true);

            using (var local = factory.Local())
                await Add("health", local.GetAsync("/health/live", Token), HttpStatusCode.OK);

            var limited = factory.Visitor("203.0.113.77");

            for (var i = 0; i < RateLimits.SignInPerMinute; i++)
                Assert.Equal(HttpStatusCode.Redirect, (await limited.Client.GetAsync("/signin", Token)).StatusCode);

            await Add("rate limit", limited.Client.GetAsync("/signin", Token), HttpStatusCode.TooManyRequests);

            var admin = factory.Visitor("203.0.113.78");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            await Add("overview", admin.Client.GetAsync("/admin", Token), HttpStatusCode.OK);

            // A database that's down, behind a cookie that has to be looked up
            var dead = new DeadDatabase();
            await using var deadFactory = new DashboardFactory(db, new DashboardFactoryOptions { SiteConnection = dead.SiteConnection, BotConnection = dead.BotConnection });
            var deadVisitor = deadFactory.Visitor();
            deadVisitor.Jar.Add(DashboardFactory.PublicAddress, new Cookie(DashboardAuth.SessionCookie, SessionKeys.New()) { Secure = true });
            await Add("database down", deadVisitor.Client.GetAsync("/admin", Token), HttpStatusCode.ServiceUnavailable);

            foreach (var (what, response, status, cacheable) in responses)
            {
                Assert.True(response.StatusCode == status, $"{what} answered {response.StatusCode}");
                AssertHeaders(what, response, cacheable);
            }

            // Antiforgery would add SAMEORIGIN to a page with a form, and the overview has the sign-out form
            var overview = responses.Single(response => response.What == "overview").Response;
            Assert.Contains("__RequestVerificationToken", await overview.Content.ReadAsStringAsync(Token));
            Assert.Equal(["DENY"], Values(overview, "X-Frame-Options"));

            // no-transform joins a Cache-Control that's already there, and makes one with no-store where there's none
            Assert.Equal(["no-store", "no-transform"], Directives(responses.Single(response => response.What == "home").Response).Order(StringComparer.Ordinal));
            Assert.Equal(["max-age=604800", "no-transform", "public"], Directives(responses.Single(response => response.What == "stylesheet").Response).Order(StringComparer.Ordinal));
        }
    }
}
