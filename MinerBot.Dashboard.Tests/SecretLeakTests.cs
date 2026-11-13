using System.Net;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Armor;
using MySql.Data.MySqlClient;

namespace MinerBot.Dashboard.Tests
{
    // A crawl of every page, as an anonymous visitor, the Owner and an Admin, that fails if any secret the app holds, or
    // any Discord gave it, appears in a status line, a header or a body
    [Collection("Database")]
    public class SecretLeakTests(MariaDbFixture fixture, AccessTestKeys keys) : IClassFixture<AccessTestKeys>
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        [Fact]
        public Task NoPageHeaderOrRedirectHoldsASecret() => CrawlAsync(null);

        // The same with the Cloudflare Access check on. Every request carries a token, as Cloudflare adds one to the admin
        // pages, and every token is a secret too.
        [Fact]
        public Task NoPageHeaderOrRedirectHoldsASecretWithAccessOn() => CrawlAsync(new FakeCloudflareAccess(keys));

        async Task CrawlAsync(FakeCloudflareAccess? access)
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db, access == null ? null : new DashboardFactoryOptions
            {
                Settings = new Dictionary<string, string?>
                {
                    ["CloudflareAccess:TeamDomain"] = FakeCloudflareAccess.TeamDomain,
                    ["CloudflareAccess:Audience"] = access.Audience
                },
                Services = services => access.Install(services)
            });

            if (access != null)
                access.Clock = factory.Clock;

            // A visitor, with a token of its own when the check is on
            TestBrowser Visitor(string address)
            {
                var browser = factory.Visitor(address);

                if (access != null)
                    browser.Client.DefaultRequestHeaders.Add(CloudflareAccessCheck.HeaderName, access.Mint());

                return browser;
            }

            var seen = new List<(string What, string Text, HttpResponseMessage Response)>();

            // Armor for the pages that show one, with its history
            await db.AddArmorAsync(ArmorSteps.Submission(1));
            await db.AddArmorAsync(ArmorSteps.Submission(2, ArmorStatus.Approved, "Test Armor 2"));
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.ReviewTable} (armor_id, from_status, to_status, reason, reviewed_by, reviewer_name, reviewed_at) " +
                "VALUES (2, 'Pending Approval', 'Approved', 'Looks right', 1000000000000000002, 'Test Admin', UTC_TIMESTAMP(3))");

            // A blacklist entry with its history, an account no longer on it, and a row no account number can be, each with
            // the address of whoever made the change, which only the Owner's audit page shows
            await db.AddEntriesAsync(BlacklistSteps.Entry(12345));
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username) VALUES (5000000000, 'Cut Player')");
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.AuditTable} (at, actor_id, actor_name, actor_role, action, outcome, target_type, target_id, after_json, ip) " +
                "VALUES (UTC_TIMESTAMP(3), 1000000000000000002, 'Test Admin', 'admin', 'blacklist.add', 'ok', 'blacklist', '12345', '{\"Name\":\"Test Player\"}', '192.0.2.50'), " +
                "(UTC_TIMESTAMP(3), 1000000000000000002, 'Test Admin', 'admin', 'blacklist.remove', 'ok', 'blacklist', '54321', NULL, '192.0.2.51')");

            // Every GET endpoint without route parameters, so a new page is crawled without changing this test, and those
            // with one, here
            var pages = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .Where(endpoint => endpoint.RoutePattern.Parameters.Count == 0)
                .Select(endpoint => ("/" + endpoint.RoutePattern.RawText!.Trim('/')).ToLowerInvariant())
                .Concat(["/denied?reason=unlisted", "/denied?reason=mfa", "/denied?reason=scope", "/denied?reason=cancelled", "/denied?reason=failed",
                    "/error", "/nothing-here", "/signout", "/signin", "/css/site.css",
                    "/admin/armor?status=all", "/admin/armor/1", "/admin/armor/2", "/admin/armor/2?done=approved", "/admin/armor/999",
                    "/admin/blacklist/12345", "/admin/blacklist/12345?done=added", "/admin/blacklist/54321", "/admin/blacklist/99999",
                    "/admin/audit?type=blacklist&id=12345"])
                .Distinct()
                .ToList();

            // The review page's and a blacklist entry's routes are the ones with a parameter, so a new one fails here until
            // it's crawled too
            Assert.Equal(["Admin/Armor/{id:long}", "Admin/Blacklist/{account:long}"], factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>().Where(endpoint => endpoint.RoutePattern.Parameters.Count > 0).Select(endpoint => endpoint.RoutePattern.RawText!.Trim('/'))
                .Order(StringComparer.Ordinal));

            async Task Crawl(string who, TestBrowser browser)
            {
                foreach (var page in pages)
                {
                    var response = await browser.Client.GetAsync(page, Token);
                    seen.Add(($"{who} {page}", await response.Content.ReadAsStringAsync(Token), response));
                }
            }

            await Crawl("anonymous", Visitor("203.0.113.50"));

            var owner = Visitor("203.0.113.51");
            var signIn = await SignInSteps.SignInAsync(factory, owner, TestUsers.Owner);
            var ownerKey = owner.SessionKey!;
            await Crawl("owner", owner);

            var admin = Visitor("203.0.113.52");
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

            // Every code, access and refresh token and PKCE verifier of the sign-ins, and every Access token
            secrets.AddRange(factory.Discord.Secrets);
            secrets.AddRange(access?.Minted ?? []);
            Assert.All(secrets, secret => Assert.False(string.IsNullOrEmpty(secret)));

            if (access != null)
            {
                Assert.Equal(3, access.Minted.Count);

                foreach (var token in access.Minted)
                    Assert.False(factory.Logs.Text.Contains(token, StringComparison.Ordinal), "A log line holds an Access token");
            }

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
            Assert.Contains(seen, page => page.What == "owner /admin/armor/2" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "admin /admin/armor/1" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "admin /admin/armor" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "admin /admin/blacklist" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "admin /admin/blacklist/add" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "owner /admin/blacklist/12345" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "admin /admin/blacklist/54321" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "admin /admin/blacklist/99999" && page.Response.StatusCode == HttpStatusCode.NotFound);
            Assert.Contains(seen, page => page.What == "owner /admin/audit" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "admin /admin/audit" && page.Response.StatusCode == HttpStatusCode.OK);

            // The visitors' addresses are the Owner's alone: on the Owner's audit page, and nowhere an Admin or a visitor sees
            Assert.Contains("192.0.2.50", seen.Single(page => page.What == "owner /admin/audit").Text);
            Assert.All(seen.Where(page => !page.What.StartsWith("owner ", StringComparison.Ordinal) && !page.What.StartsWith("local ", StringComparison.Ordinal)),
                page => Assert.False(page.Text.Contains("192.0.2.5", StringComparison.Ordinal), $"{page.What} shows an address"));
        }
    }
}
