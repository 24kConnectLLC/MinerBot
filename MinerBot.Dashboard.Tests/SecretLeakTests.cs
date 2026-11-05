using System.Net;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Tests.Support;
using MySql.Data.MySqlClient;

namespace MinerBot.Dashboard.Tests
{
    // A crawl of every page, as an anonymous visitor, the Owner and an Admin, that fails if any secret the app holds, or
    // any Discord gave it, appears in a status line, a header or a body
    [Collection("Database")]
    public class SecretLeakTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        [Fact]
        public async Task NoPageHeaderOrRedirectHoldsASecret()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var seen = new List<(string What, string Text, HttpResponseMessage Response)>();

            // Every GET endpoint without route parameters, so a new page is crawled without changing this test
            var pages = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .Where(endpoint => endpoint.RoutePattern.Parameters.Count == 0)
                .Select(endpoint => ("/" + endpoint.RoutePattern.RawText!.Trim('/')).ToLowerInvariant())
                .Concat(["/denied?reason=unlisted", "/denied?reason=mfa", "/denied?reason=scope", "/denied?reason=cancelled", "/denied?reason=failed",
                    "/error", "/nothing-here", "/signout", "/signin", "/css/site.css"])
                .Distinct()
                .ToList();

            async Task Crawl(string who, TestBrowser browser)
            {
                foreach (var page in pages)
                {
                    var response = await browser.Client.GetAsync(page, Token);
                    seen.Add(($"{who} {page}", await response.Content.ReadAsStringAsync(Token), response));
                }
            }

            await Crawl("anonymous", factory.Visitor("203.0.113.50"));

            var owner = factory.Visitor("203.0.113.51");
            var signIn = await SignInSteps.SignInAsync(factory, owner, TestUsers.Owner);
            var ownerKey = owner.SessionKey!;
            await Crawl("owner", owner);

            var admin = factory.Visitor("203.0.113.52");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            var adminKey = admin.SessionKey!;
            await Crawl("admin", admin);

            using (var local = factory.Local())
            {
                foreach (var health in new[] { "/health/live", "/health/ready" })
                {
                    var response = await local.GetAsync(health, Token);
                    seen.Add(($"local {health}", await response.Content.ReadAsStringAsync(Token), response));
                }
            }

            var site = new MySqlConnectionStringBuilder(db.SiteConnection);
            var bot = new MySqlConnectionStringBuilder(db.BotConnection);
            var secrets = new List<string>
            {
                factory.ClientSecret,
                factory.ProxyKey,
                site.Password,
                site.UserID,
                bot.Password,
                bot.UserID,
                ownerKey,
                adminKey,
                owner.Cookie(DashboardAuth.AntiforgeryCookie)!,
                admin.Cookie(DashboardAuth.AntiforgeryCookie)!
            };

            // Every code, access and refresh token and PKCE verifier of the sign-ins
            secrets.AddRange(factory.Discord.Secrets);
            Assert.All(secrets, secret => Assert.False(string.IsNullOrEmpty(secret)));

            seen.Add(("owner sign-in", "", signIn));

            foreach (var (what, body, response) in seen)
            {
                var headers = response.Headers.Concat(response.Content.Headers)
                    .SelectMany(header => header.Value.Select(value => (header.Key, Value: value)))
                    // A cookie's own Set-Cookie holds its value, which is where it belongs
                    .Where(header => !(header.Key == "Set-Cookie" && (header.Value.StartsWith(DashboardAuth.SessionCookie + "=", StringComparison.Ordinal)
                        || header.Value.StartsWith(DashboardAuth.AntiforgeryCookie + "=", StringComparison.Ordinal))))
                    .Select(header => $"{header.Key}: {header.Value}");

                var text = $"{(int)response.StatusCode} {response.ReasonPhrase}\n{string.Join('\n', headers)}\n{body}";

                foreach (var secret in secrets)
                    Assert.False(text.Contains(secret, StringComparison.Ordinal), $"{what} shows a secret");
            }

            Assert.Contains(seen, page => page.What == "owner /admin" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "admin /admin" && page.Response.StatusCode == HttpStatusCode.OK);
        }
    }
}
