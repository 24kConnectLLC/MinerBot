using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Armor;
using MinerBot.Shared.Delivery;
using MinerBot.Shared.Requests;
using MySql.Data.MySqlClient;

namespace MinerBot.Dashboard.Tests
{
    // A crawl of every page, as an anonymous visitor, the Owner and an Admin, that fails if any secret the app holds, or
    // any Discord gave it, appears in a status line, a header or a body, or if an address shows anywhere but the Owner's
    // pages, a session's or an ended session's included
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
            var db = await fixture.ReadyBotAsync();
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
            // the address of whoever made the change, which only the Owner's pages show
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
                    "/admin/audit?type=blacklist&id=12345", "/js/refresh.js", "/admin?handler=status", "/admin/bot?routine=1",
                    "/admin/servers?show=all", $"/admin/servers/{StatusSteps.ServerId}", $"/admin/servers/{StatusSteps.SettingsOnlyId}", "/admin/servers/99999",
                    "/admin/requests/1", "/admin/requests/1?handler=result", "/admin/requests/2", "/admin/requests/99999", "/admin/bot/controls?request=1",
                    "/admin/armor/2?done=approved&request=1", $"/admin/servers/{StatusSteps.ServerId}?request=3", $"/admin/servers/{StatusSteps.ServerId}/leave",
                    $"/admin/servers/{StatusSteps.SettingsOnlyId}/leave", "/admin/requests/3", "/admin/requests/4", "/admin/requests/4?handler=result", "/admin/sessions?done=ended"])
                .Distinct()
                .ToList();

            // A server with a hidden channel, settings and a change of its own, a server the website has settings for and the
            // bot isn't in, and the bot's status with every part of the status box, so the status pages show all they can
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            await StatusSteps.ServersAsync(db);
            await StatusSteps.StatusAsync(db, now);
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.AuditTable} (at, actor_id, actor_name, actor_role, action, outcome, guild_id, target_type, ip) " +
                $"VALUES (UTC_TIMESTAMP(3), 1000000000000000002, 'Test Admin', 'admin', 'auth.signin', 'ok', {StatusSteps.ServerId}, 'session', '192.0.2.52')");

            // The server's switches, and the bot's record of its posts there: approved armor 2 posted, and lobbies whose post
            // Discord refused, so the servers pages, the Overview and the review page draw them
            var server = StatusSteps.Id(StatusSteps.ServerId);
            await PostSteps.SwitchesAsync(db, server, armor: true, maps: false, lobbies: true, now.AddMinutes(-30));
            await PostSteps.SentAsync(db, await PostSteps.QueueAsync(db, PostSource.Armor, 2, now.AddMinutes(-20), server), 3001, now.AddMinutes(-20), 5000000000000000002);
            await PostSteps.FailedAsync(db, await PostSteps.QueueAsync(db, PostSource.Lobbies, 9, now.AddMinutes(-10), server), 3001, now.AddMinutes(-10),
                DeliveryReasons.DiscordPermissions, 403, 50013, now.AddMinutes(20));

            // Requests to the bot with every part the controls page and the banners draw: a cache clear the bot did, a restart
            // whose message to the owner failed, posts paused with a reason, a status text, and a request the bot refused as
            // not the dashboard's
            var clear = await RequestSteps.AddAsync(db, factory, RequestKinds.CachesClear, now.AddMinutes(-3), TestUsers.Admin);
            await RequestSteps.ClaimAsync(db, clear, now.AddMinutes(-3).AddSeconds(1));
            await RequestSteps.FinishAsync(db, clear, RequestStates.Done, "Cleared the armor-name and latest-maps caches.", now.AddMinutes(-3).AddSeconds(2));
            var restart = await RequestSteps.AddAsync(db, factory, RequestKinds.BotRestart, now.AddMinutes(-2));
            await RequestSteps.ClaimAsync(db, restart, now.AddMinutes(-2).AddSeconds(1));
            await RequestSteps.FinishAsync(db, restart, RequestStates.Failed, "The restart failed.", now.AddMinutes(-2).AddSeconds(2));
            await RequestSteps.DmAsync(db, restart, RequestDmStates.Failed);
            await db.Requests().SavePause(true, "Testing the crawl", now.AddMinutes(-1), TestUsers.Owner.Id, restart, now.AddMinutes(-1));
            await db.Requests().SaveStatus("Mining at night", now.AddMinutes(-1), TestUsers.Owner.Id, restart, now.AddMinutes(-1));
            await db.Status().AddEvents([StatusSteps.Event(7, now.AddMinutes(-1), "request.rejected", DataAccessLibrary.Status.EventLevel.Error,
                "request 99 refused: its signature didn't match the bot's key")]);

            // A test message the bot sent, to the server's own chosen channel, a lookup it answered, and one request in each
            // state besides: waiting, taken, refused, expired, and not sure it happened
            var test = await RequestSteps.AddAsync(db, factory, RequestKinds.GuildTestMessage, now.AddMinutes(-4), TestUsers.Admin);
            await RequestSteps.ClaimAsync(db, test, now.AddMinutes(-4).AddSeconds(1));
            await RequestSteps.FinishAsync(db, test, RequestStates.Done, "Sent a test message.", now.AddMinutes(-4).AddSeconds(2), number: 1234567890123456789);
            var lookup = await RequestSteps.AddAsync(db, factory, RequestKinds.SteamLookup, now.AddMinutes(-4), TestUsers.Admin);
            await RequestSteps.ClaimAsync(db, lookup, now.AddMinutes(-4).AddSeconds(1));
            await RequestSteps.FinishAsync(db, lookup, RequestStates.Done, "That link is account [U:1:22202].", now.AddMinutes(-4).AddSeconds(2), number: 22202);
            var waiting = await RequestSteps.AddAsync(db, factory, RequestKinds.JobNotifications, now.AddSeconds(-5));
            var running = await RequestSteps.TakenAsync(db, factory, RequestKinds.JobColors, now.AddSeconds(-5));
            var refused = await RequestSteps.AddAsync(db, factory, RequestKinds.PostsResume, now.AddMinutes(-6));
            await RequestSteps.DecideAsync(db, refused, RequestStates.Refused, "Posts aren't paused.", now.AddMinutes(-6).AddSeconds(1));
            var expired = await RequestSteps.AddAsync(db, factory, RequestKinds.SteamRenew, now.AddMinutes(-8));
            await RequestSteps.DecideAsync(db, expired, RequestStates.Expired, "Expired", now.AddMinutes(-5));
            var unknown = await RequestSteps.TakenAsync(db, factory, RequestKinds.SteamReconnect, now.AddMinutes(-9));
            await RequestSteps.FinishAsync(db, unknown, RequestStates.Unknown, "Steam disconnected, and MinerBot hasn't logged on again within 45 seconds.", now.AddMinutes(-8));
            ulong[] seeded = [clear, restart, test, lookup, waiting, running, refused, expired, unknown];
            Assert.Equal([3UL, 4UL], new[] { test, lookup });

            // The review page's, a blacklist entry's, a request's, a server's and the leave page's routes are the ones with a
            // parameter, so a new one fails here until it's crawled too. Requests 1 to 4 are the ones seeded above.
            Assert.Equal(["Admin/Armor/{id:long}", "Admin/Blacklist/{account:long}", "Admin/Requests/{id:long}", "Admin/Servers/{id:long}", "Admin/Servers/{id:long}/Leave"],
                factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
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

            // A session the Owner ends, whose audit row Admins read on the audit page, and whose address must never be there
            var ended = Visitor("203.0.113.53");
            await SignInSteps.SignedInAsync(factory, ended, TestUsers.SecondAdmin);
            var endedKey = ended.SessionKey!;
            var end = await SignInSteps.PostAsync(owner, "/admin/sessions?handler=End", SignInSteps.FormToken(await owner.Client.GetStringAsync("/admin/sessions", Token)),
                origin: DashboardFactory.PublicOrigin, fields: [new("session", Convert.ToHexStringLower(DataAccessLibrary.Dashboard.DashboardSessionData.HashOf(endedKey)))]);
            Assert.Equal("/admin/sessions?done=ended", SignInSteps.PathOf(end.Headers.Location));
            seen.Add(("owner ends a session", await end.Content.ReadAsStringAsync(Token), end));

            var admin = Visitor("203.0.113.52");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            var adminKey = admin.SessionKey!;
            await Crawl("admin", admin);

            // The Owner's Sessions page once the Admin is signed in, with the Admin's address
            var sessions = await owner.Client.GetAsync("/admin/sessions", Token);
            seen.Add(("owner /admin/sessions after the admin", await sessions.Content.ReadAsStringAsync(Token), sessions));

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
                factory.RequestSigningKey,
                site.Password,
                site.UserID,
                bot.Password,
                bot.UserID,
                ownerKey,
                adminKey,
                endedKey,
                owner.Cookie(DashboardAuth.AntiforgeryCookie)!,
                admin.Cookie(DashboardAuth.AntiforgeryCookie)!
            };

            // The password's hash MariaDB keeps for each connection string's login, which the statement that lists a login's
            // grants prints, in case anything ever showed that statement's answer
            foreach (var login in new[] { site.UserID, bot.UserID }.Distinct())
                secrets.Add(Assert.Single(await db.RootQueryAsync<string>("SELECT authentication_string FROM mysql.user WHERE User = @login", new { login })));

            // Every code, access and refresh token and PKCE verifier of the sign-ins, and every Access token
            secrets.AddRange(factory.Discord.Secrets);
            secrets.AddRange(access?.Minted ?? []);
            Assert.All(secrets, secret => Assert.False(string.IsNullOrEmpty(secret)));

            if (access != null)
            {
                Assert.Equal(4, access.Minted.Count);

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
            Assert.Contains(seen, page => page.What == "owner /admin/requests/1" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "admin /admin/requests/1?handler=result" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "admin /admin/requests/99999" && page.Response.StatusCode == HttpStatusCode.NotFound);
            Assert.Contains(seen, page => page.What == "owner /admin/audit" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "admin /admin/audit" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "admin /admin/bot?routine=1" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "admin /admin?handler=status" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "admin /admin/servers?show=all" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == $"admin /admin/servers/{StatusSteps.ServerId}" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == $"admin /admin/servers/{StatusSteps.SettingsOnlyId}" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "admin /admin/servers/99999" && page.Response.StatusCode == HttpStatusCode.NotFound);
            Assert.Contains(seen, page => page.What == "owner /admin/bot/controls" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "owner /admin/bot/controls?request=1" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "admin /admin/bot/controls" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "admin /admin/armor/2?done=approved&request=1" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == "owner /admin/sessions" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == $"owner /admin/servers/{StatusSteps.ServerId}/leave" && page.Response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(seen, page => page.What == $"admin /admin/servers/{StatusSteps.ServerId}?request=3" && page.Response.StatusCode == HttpStatusCode.OK);

            // The Owner's pages are the Owner's alone: an Admin's ends at /denied
            foreach (var ownerOnly in new[] { "/admin/sessions", $"/admin/servers/{StatusSteps.ServerId}/leave" })
            {
                var denied = seen.Single(page => page.What == "admin " + ownerOnly).Response;
                Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
                Assert.StartsWith("/denied", SignInSteps.PathOf(denied.Headers.Location));
            }

            // The pages showed the seeded requests, the pause, the status text and the bot's refusal, as banners and lines, and
            // the test message's link and the lookup's account
            Assert.Contains("Testing the crawl", seen.Single(page => page.What == "owner /admin/bot/controls").Text);
            Assert.Contains("request.rejected", seen.Single(page => page.What == "admin /admin").Text);
            Assert.Contains($"/admin/requests/{restart}", seen.Single(page => page.What == "owner /admin").Text);
            Assert.Contains("1234567890123456789", seen.Single(page => page.What == $"admin /admin/servers/{StatusSteps.ServerId}?request=3").Text);
            Assert.Contains("/admin/blacklist/22202", seen.Single(page => page.What == "admin /admin/requests/4").Text);

            // No GET wrote a request: the table holds the seeded ones and the three sign-ins' messages to the owner
            Assert.Equal(seeded, (await db.SentAsync()).Select(row => row.id));
            Assert.Equal(3, (await db.RequestsAsync()).Count(row => row.kind == RequestKinds.OwnerSigninAlert));

            // The visitors' addresses are the Owner's alone: on the Owner's audit page, Overview and server page, and nowhere an
            // Admin or a visitor sees
            Assert.Contains("192.0.2.50", seen.Single(page => page.What == "owner /admin/audit").Text);
            Assert.Contains("192.0.2.52", seen.Single(page => page.What == "owner /admin").Text);
            Assert.Contains("192.0.2.52", seen.Single(page => page.What == $"owner /admin/servers/{StatusSteps.ServerId}").Text);
            Assert.All(seen.Where(page => !page.What.StartsWith("owner ", StringComparison.Ordinal) && !page.What.StartsWith("local ", StringComparison.Ordinal)),
                page => Assert.False(page.Text.Contains("192.0.2.5", StringComparison.Ordinal), $"{page.What} shows an address"));

            // And so are the sessions': the Admin's on the Owner's Sessions page, and neither theirs, the Owner's nor the ended
            // session's anywhere an Admin or a visitor sees, such as the ending's row on the audit page
            Assert.Contains("203.0.113.52", seen.Single(page => page.What == "owner /admin/sessions after the admin").Text);
            Assert.Contains("203.0.113.51", seen.Single(page => page.What == "owner /admin/sessions").Text);
            Assert.Contains(seen, page => page.What == "admin /admin/audit" && page.Text.Contains("Ended a session", StringComparison.Ordinal));
            Assert.All(seen.Where(page => !page.What.StartsWith("owner ", StringComparison.Ordinal) && !page.What.StartsWith("local ", StringComparison.Ordinal)),
                page => Assert.False(Regex.IsMatch(page.Text, @"203\.0\.113\.5[1-3]\b"), $"{page.What} shows a session's address"));
        }
    }
}
