using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary;
using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Discord;
using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Pages.Admin;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Status;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Armor;

namespace MinerBot.Dashboard.Tests
{
    // The Overview and the Bot status page, read from rows the bot's own code writes, for the Owner and an Admin: the
    // bot's state in each case, the banners, the website's database failing, the status fragment and its marker, and
    // refreshes that never keep a session going
    [Collection("Database")]
    public partial class StatusPageTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static Task<HttpResponseMessage> RefreshAsync(TestBrowser browser, string path = StatusBoxes.Address)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add(BackgroundRefresh.Header, BackgroundRefresh.Value);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            return browser.Client.SendAsync(request, Token);
        }

        static async Task<string> PageAsync(TestBrowser browser, string path) => WebUtility.HtmlDecode(await browser.Client.GetStringAsync(path, Token));

        static bool HasMarker(HttpResponseMessage response) => response.Headers.Contains(BackgroundRefresh.FragmentHeader);

        // The note beside a status box, as both pages draw it: empty, with the words refresh.js writes into it
        internal const string StoppedNote = "<p class=\"muted\" role=\"status\" data-refresh-stopped=\"Not updating: reload the page.\"></p>";

        // The sections' text without its markup, so a sentence reads as one string
        static string Text(string html) => Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), "\\s+", " ");

        // Everything up: the status box says so, nothing needs anyone, the last notification run shows, and the latest
        // changes hold the visitors' addresses for the Owner only
        [Fact]
        public async Task OverviewWhileEverythingIsUp()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            await StatusSteps.StatusAsync(db, now);
            await db.Audit().AddAudit(new() { at = now.AddMinutes(-1), actor_id = TestUsers.Admin.Id, actor_name = "Test Admin", actor_role = "admin", action = "auth.signin", outcome = "ok", target_type = "session", ip = "192.0.2.91" });

            var owner = factory.Visitor("203.0.113.120");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var admin = factory.Visitor("203.0.113.121");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            var ownerPage = await owner.Client.GetStringAsync("/admin", Token);
            var adminPage = await admin.Client.GetStringAsync("/admin", Token);
            PageTests.AssertAccessible("the Overview", ownerPage);

            foreach (var page in new[] { ownerPage, adminPage })
            {
                var text = Text(WebUtility.HtmlDecode(page));

                Assert.Contains("Nothing needs you right now.", text);
                Assert.Contains("Online Last checked in 10 seconds ago", text);
                Assert.Contains("Connected since", text);
                Assert.Contains("ago. Latency 18 ms. In 10 servers.", text);
                Assert.Contains("Logged on since", text);
                Assert.Contains("The sign-in token ends on", text);
                Assert.Contains("Website database Answering", text);
                Assert.Contains("Result OK", text);
                Assert.Contains("No armor is waiting for a review.", text);
                Assert.Contains($"data-refresh=\"{StatusBoxes.Address}\" data-fragment=\"{StatusBoxes.Fragment}\"", page);

                // The note is a live region that's there and empty from the start, which refresh.js fills when it stops: a
                // hidden one isn't in the accessibility tree, so showing it is no change a screen reader announces
                Assert.Contains(StoppedNote, page);
                Assert.DoesNotContain("hidden role=\"status\"", page);
            }

            Assert.Contains("192.0.2.91", ownerPage);
            Assert.DoesNotContain("192.0.2.91", adminPage);
            Assert.Contains("Each row's visitor address is shown to the Owner alone.", WebUtility.HtmlDecode(adminPage));

            // A stop writes the note's words into it as text, and never shows a hidden note
            var js = await owner.Client.GetStringAsync("/js/refresh.js", Token);
            Assert.Contains("note.textContent = note.getAttribute('data-refresh-stopped');", js);
            Assert.DoesNotContain(".hidden", js);
        }

        // A sign-in refused for its scope is written before Discord says who it was, so its row holds no link. When the
        // latest changes are all such rows, their table takes the keyboard's focus itself, as a group named by its heading,
        // so a keyboard can scroll it on a phone, as the Bot page's tables do. A row with a link leaves it as it was.
        [Fact]
        public async Task LatestChangesWithNoLinkTakeTheKeyboard()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            await StatusSteps.StatusAsync(db, factory.Clock.GetUtcNow().UtcDateTime);
            var owner = factory.Visitor("203.0.113.125");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            factory.Discord.GrantedScope = "identify email";

            for (var i = 0; i < OverviewModel.ChangesShown; i++)
            {
                factory.Clock.Advance(TimeSpan.FromSeconds(1));
                await SignInSteps.SignInAsync(factory, factory.Visitor("198.51.100." + (20 + i).ToString(CultureInfo.InvariantCulture)), TestUsers.Owner);
            }

            var html = await owner.Client.GetStringAsync("/admin", Token);
            PageTests.AssertAccessible("the Overview", html);
            Assert.Equal(OverviewModel.ChangesShown, Regex.Count(WebUtility.HtmlDecode(html), "Someone Discord didn't name"));
            PageTests.AssertTablesTakeTheKeyboard("the Overview with only refused sign-ins in its latest changes", html);
            Assert.Contains("<div class=\"table-wrap\" tabindex=\"0\" role=\"group\" aria-labelledby=\"changes\">", html);

            // A sign-in after them, whose row links its person
            factory.Discord.GrantedScope = "identify";
            factory.Clock.Advance(TimeSpan.FromSeconds(1));
            await SignInSteps.SignedInAsync(factory, factory.Visitor("203.0.113.126"), TestUsers.Admin);

            var mixed = await owner.Client.GetStringAsync("/admin", Token);
            Assert.Contains("<div class=\"table-wrap\">", mixed);
            Assert.DoesNotContain("tabindex", mixed);
        }

        // Each state the status row can be in, with the banner it brings, if any
        [Fact]
        public async Task OverviewForEachStateOfTheBot()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.122");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            async Task<string> WithStatusAsync(BotStatus? status)
            {
                await db.ClearAsync(MariaDbFixture.StatusTable);

                if (status != null)
                    await db.Status().SaveStatus(status);

                var html = await owner.Client.GetStringAsync("/admin", Token);
                PageTests.AssertAccessible("the Overview", html);
                return Text(WebUtility.HtmlDecode(html));
            }

            var missing = await WithStatusAsync(null);
            Assert.Contains("Offline Never checked in.", missing);
            Assert.Contains("MinerBot hasn't checked in: there's no status row.", missing);

            var late = await WithStatusAsync(StatusSteps.Status(now.AddMinutes(-4)));
            Assert.Contains("Late Last checked in 4 minutes ago", late);
            Assert.Contains("Nothing needs you right now.", late);
            Assert.Contains("(as MinerBot last recorded it)", late);

            var offline = await WithStatusAsync(StatusSteps.Status(now.AddMinutes(-11)));
            Assert.Contains("Offline Last checked in 11 minutes ago", offline);
            Assert.Contains("MinerBot is offline: it last checked in 11 minutes ago", offline);
            Assert.Contains("Check it on the server with ./status.sh. The deploy notes", offline);

            var stopped = StatusSteps.Status(now.AddMinutes(-2));
            stopped.stopped_at = stopped.heartbeat_at;
            var briefly = await WithStatusAsync(stopped);
            Assert.Contains("Stopped Stopped 2 minutes ago", briefly);
            Assert.Contains("Nothing needs you right now.", briefly);

            var longer = StatusSteps.Status(now.AddMinutes(-6));
            longer.stopped_at = longer.heartbeat_at;
            Assert.Contains("MinerBot stopped 6 minutes ago", await WithStatusAsync(longer));

            var dropped = StatusSteps.Status(now.AddSeconds(-5));
            dropped.discord_state = DiscordState.Disconnected;
            dropped.discord_since = now.AddMinutes(-7);
            dropped.discord_latency_ms = null;
            dropped.discord_guilds = null;
            var discordDown = await WithStatusAsync(dropped);
            Assert.Contains("Disconnected since", discordDown);
            Assert.Contains("MinerBot has been disconnected from Discord since", discordDown);

            // Steam drops the bot now and then and the bot logs on again by itself, so offline is in the box and no banner
            var steamDropped = StatusSteps.Status(now.AddSeconds(-10));
            steamDropped.steam_state = SteamState.Offline;
            steamDropped.steam_since = now.AddMinutes(-3);
            var steamOffline = await WithStatusAsync(steamDropped);
            Assert.Contains("Online Last checked in 10 seconds ago", steamOffline);
            Assert.Contains("Steam Offline since", steamOffline);
            Assert.Contains("UTC, 3 minutes ago. The sign-in token ends on", steamOffline);
            Assert.Contains("Nothing needs you right now.", steamOffline);

            // A token past its end: the box says when it ended, in the warning's colour, and the banner asks for a new
            // sign-in by the Steam Sign-In steps, with no "before then", since it can't be renewed any more
            var tokenEnded = StatusSteps.Status(now.AddSeconds(-10));
            tokenEnded.steam_token_expires = DateOnly.FromDateTime(now).AddDays(-1);
            await WithStatusAsync(tokenEnded);
            var ended = WebUtility.HtmlDecode(await owner.Client.GetStringAsync("/admin", Token));
            var endedOn = DateOnly.FromDateTime(now).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Assert.Contains($"<span class=\"warn-text\">The sign-in token ended on {endedOn}.</span>", ended);
            Assert.Contains($"<p>The Steam sign-in token ended on {endedOn}, so the bot can't log on to Steam with it again: sign in again, as the Steam Sign-In steps say. <a href=\"{Banners.SteamSignIn}\">Steam Sign-In</a></p>", ended);
            Assert.DoesNotContain("before then", ended);
            Assert.DoesNotContain("Nothing needs you right now.", ended);
        }

        // A bot that needs things: a Steam sign-in, a token near its end, a failed and a cut-off job, armor waiting 3 days,
        // and a server with a setting it can't post in, each with what to do and where
        [Fact]
        public async Task OverviewBannersSayWhatToDo()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var status = StatusSteps.Status(now.AddSeconds(-10));
            status.steam_state = SteamState.NeedsSignIn;
            status.steam_last_result = "Revoked";
            status.steam_token_expires = DateOnly.FromDateTime(now).AddDays(10);
            await db.Status().SaveStatus(status);
            await db.Status().SaveJobStatus(
            [
                StatusSteps.Job("GuildMembersJob.CountMembers", "21 4 * * *", now, JobResult.Failed, "TimeoutException"),
                StatusSteps.Job("HousekeepingJob.ApplyRetention", "30 4 * * *", now, JobResult.CutOff, "still running when the time limit of 270 s cut it off"),
                StatusSteps.Job("GuildNotificationJob.NotifyAllGuilds", "*/5 * * * *", now, JobResult.Ok)
            ]);

            var oldest = WebsiteClock.FromUtc(now.AddDays(-3).AddHours(-1))!.Value;
            var armor = ArmorSteps.Submission(1);
            armor.CreatedAt = new DateTime(oldest.Ticks - oldest.Ticks % TimeSpan.TicksPerSecond);
            var newer = ArmorSteps.Submission(2);
            newer.CreatedAt = armor.CreatedAt.AddDays(2);
            await db.AddArmorAsync(armor);
            await db.AddArmorAsync(newer);
            await StatusSteps.ServersAsync(db);

            // A server in an outage whose setting works: an outage is Discord's, with nothing to fix, so it adds no banner,
            // though the banner's link lists it
            const ulong outage = 1000000000000000703;
            await db.BotGuilds().SaveGuild(StatusSteps.Server(outage, "Outage Server"), [StatusSteps.Channel(3101, "general", 0)], now, StatusSteps.RunId, 2);
            await db.Guilds().SaveGuild(new Guild { guild_id = outage, armor_channel_id = 3101 });
            await db.BotGuilds().MarkUnavailable([outage], now);

            var owner = factory.Visitor("203.0.113.123");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var html = await owner.Client.GetStringAsync("/admin", Token);
            var text = Text(WebUtility.HtmlDecode(html));
            PageTests.AssertAccessible("the Overview with banners", html);

            Assert.DoesNotContain("Nothing needs you right now.", text);
            Assert.Contains("ago. Steam's last answer: Revoked. The sign-in token ends on", text);
            Assert.Contains("Steam needs a sign-in, since", text);
            Assert.Contains("Steam's last answer was Revoked. Sign in again as the Steam Sign-In steps say. Steam Sign-In", text);
            Assert.Contains("The Steam sign-in token ends on", text);
            Assert.Contains("in 10 days.", text);
            Assert.Contains("The last run of Member counts failed at", text);
            Assert.Contains("TimeoutException. Its runs on the Bot status page", text);
            Assert.Contains("The last run of Clean-up was cut off by the time limit at", text);
            Assert.Contains("1 server has a setting MinerBot can't post in. See them", text);
            Assert.Contains($"2 armor are waiting for a review, the oldest since {armor.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} by the website's clock, 3 days ago. Review them", text);
            Assert.Contains("2 armor waiting for a review.", text);

            // The last notification run is the notification job's own, which went well, not a job listed before it
            var from = text.IndexOf("The last notification run", StringComparison.Ordinal);
            var notifications = text[from..text.IndexOf("Pending armor", from, StringComparison.Ordinal)];
            Assert.Contains("Result OK", notifications);
            Assert.DoesNotContain("TimeoutException", notifications);

            Assert.Contains($"href=\"{Banners.SteamSignIn}\"", html);
            Assert.Contains("href=\"/admin/bot#jobs\"", html);
            Assert.Contains("href=\"/admin/servers?show=problems\"", html);
            Assert.Contains("href=\"/admin/armor?status=pending\"", html);
        }

        // The website's database down doesn't take the Overview down: its parts say so and the banner shows, while the
        // rest, from MinerBot's own database, is all there. Nothing of the dead connection reaches the logs.
        [Fact]
        public async Task OverviewStaysUpWhileTheWebsiteDatabaseFails()
        {
            var db = await fixture.ReadyBotAsync();
            var dead = new DeadDatabase();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions { SiteConnection = dead.SiteConnection });
            await StatusSteps.StatusAsync(db, factory.Clock.GetUtcNow().UtcDateTime);

            var admin = factory.Visitor("203.0.113.124");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            var response = await admin.Client.GetAsync("/admin", Token);
            var html = await response.Content.ReadAsStringAsync(Token);
            var text = Text(WebUtility.HtmlDecode(html));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            PageTests.AssertAccessible("the Overview with the website down", html);
            Assert.Contains("The website's database isn't answering the dashboard", text);
            Assert.Contains(WebsiteNotAnswering, text);
            Assert.Contains("The website's database isn't answering, so pending armor can't be shown.", text);
            Assert.Contains("Online Last checked in", text);
            Assert.Contains("Result OK", text);

            var fragment = await RefreshAsync(admin);
            Assert.Equal(HttpStatusCode.OK, fragment.StatusCode);
            Assert.Contains("Not answering", await fragment.Content.ReadAsStringAsync(Token));

            // Every page the banner names as one that won't open answers the usual 503, and the Bot status page, which
            // needs only MinerBot's database, still opens
            Assert.Contains("the armor, blacklist and servers pages won't open", text);
            foreach (var page in new[] { "/admin/armor", "/admin/blacklist", "/admin/servers", "/admin/servers/1000000000000000801" })
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.Client.GetAsync(page, Token)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync("/admin/bot", Token)).StatusCode);

            foreach (var secret in new[] { dead.Login, dead.Password, dead.SiteDatabase })
                Assert.DoesNotContain(secret, factory.Logs.Text);
        }

        // A website read that fails after the check answered, as one of a table whose right the login lost, leaves the page
        // up with the other read's part. The status box keeps the check's answer, so the box its refresh brings says the
        // same, and the banner names the read that failed rather than saying the database doesn't answer. Guard has
        // already logged it.
        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task OverviewStaysUpWhenAWebsiteReadFails(bool armorFails, bool settingsFail)
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services =>
                {
                    if (armorFails)
                        services.Replace(ServiceDescriptor.Transient<IArmorSubmissionData, FailingArmor>());

                    if (settingsFail)
                        services.Replace(ServiceDescriptor.Transient<IGuildSettingsReads, FailingSettings>());
                }
            });
            await StatusSteps.StatusAsync(db, factory.Clock.GetUtcNow().UtcDateTime);
            await db.AddArmorAsync(ArmorSteps.Submission(1));
            var admin = factory.Visitor("203.0.113.135");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            var response = await admin.Client.GetAsync("/admin", Token);
            var text = Text(WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token)));
            var refreshed = Text(WebUtility.HtmlDecode(await (await RefreshAsync(admin)).Content.ReadAsStringAsync(Token)));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Website database Answering", text);
            Assert.Contains("Website database Answering", refreshed);
            Assert.DoesNotContain("isn't answering", text);

            var what = armorFails && settingsFail ? "pending armor and the servers' settings" : armorFails ? "pending armor" : "the servers' settings";
            Assert.Contains($"The website's database answers the dashboard, but reading {what} from it failed", text);
            Assert.Contains(armorFails ? "Reading pending armor from the website's database failed, so it can't be shown." : "1 armor waiting for a review.", text);
            Assert.Contains("A database call failed with TimeoutException", factory.Logs.Text);
        }

        // The status box's words for a website database whose check failed: refused, as by a closed port or a refused
        // login, or with no answer. Never only "no answer", which a refusal isn't. A read that fails after a check that
        // answered leaves the box at Answering, as its refresh's own check says.
        const string WebsiteNotAnswering = "Website database Not answering The dashboard couldn't use it just now: its own check of the connection failed or got no answer within 3 seconds.";

        sealed class FailingSettings : IGuildSettingsReads
        {
            public Task<Guild?> GetSettings(ulong guildId) => throw new TimeoutException("The database didn't answer within 15 seconds");
            public Task<IReadOnlyList<Guild>> ListSettings() => throw new TimeoutException("The database didn't answer within 15 seconds");
        }

        sealed class FailingArmor : IArmorSubmissionData
        {
            static TimeoutException Late() => new("The database didn't answer within 15 seconds");

            public Task<bool> ChangeStatus(long id, string? expectedStatus, string newStatus, DataTransaction? transaction = null) => throw Late();
            public Task<ArmorSubmission?> GetSubmission(long id, DataTransaction? transaction = null) => throw Late();
            public Task<bool> IsApprovedNameTaken(string name, long exceptId, DataTransaction? transaction = null) => throw Late();
            public Task<Page<ArmorSubmission>> ListByStatus(string? status, int page, int pageSize) => throw Late();
            public Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work) => throw Late();
            public Task<Page<ArmorSubmission>> Search(string text, string? status, int page, int pageSize) => throw Late();
        }

        // An Overview left open: its banners are drawn with the page and say when, while the status box refreshes, so a bot
        // that goes offline meanwhile turns the box to Offline under a section that says how old it is
        [Fact]
        public async Task OverviewSaysWhenItsBannersWereDrawn()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var drawn = factory.Clock.GetUtcNow().UtcDateTime;
            await StatusSteps.StatusAsync(db, drawn);
            var owner = factory.Visitor("203.0.113.136");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            var text = Text(WebUtility.HtmlDecode(await owner.Client.GetStringAsync("/admin", Token)));
            factory.Clock.Advance(TimeSpan.FromMinutes(11));
            var box = Text(WebUtility.HtmlDecode(await (await RefreshAsync(owner)).Content.ReadAsStringAsync(Token)));

            Assert.Contains($"What needs you Nothing needs you right now. As of {TimeWords.Utc(drawn)}. Unlike the status box below, this part doesn't refresh itself: reload the page for the latest. Status", text);
            Assert.Contains("MinerBot Offline Last checked in 11 minutes ago", box);
            Assert.Contains($"As of {TimeWords.Utc(drawn.AddMinutes(11))}.", box);
        }

        // The fragment is the status box alone, with its marker. Anything else, such as a page, a refusal, a sign-in or an
        // error page, has none, so refresh.js never puts one in the box.
        [Fact]
        public async Task FragmentIsTheStatusBoxWithItsMarker()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            await StatusSteps.StatusAsync(db, factory.Clock.GetUtcNow().UtcDateTime);
            var admin = factory.Visitor("203.0.113.125");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            var fragment = await RefreshAsync(admin);
            var box = await fragment.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.OK, fragment.StatusCode);
            Assert.Equal("text/html", fragment.Content.Headers.ContentType?.MediaType);
            Assert.Equal([StatusBoxes.Fragment], fragment.Headers.GetValues(BackgroundRefresh.FragmentHeader));
            Assert.StartsWith("<dl class=\"details status-box\">", box.Trim());
            Assert.Contains("Last checked in 10 seconds ago", box);
            Assert.DoesNotContain("<html", box);
            Assert.DoesNotContain("<h1", box);
            Assert.DoesNotContain("<script", box);

            // The same without the marker, as a browser would open the address
            Assert.True(HasMarker(await admin.Client.GetAsync(StatusBoxes.Address, Token)));

            // A page, a handler that isn't there, which Razor Pages answers with the page itself, and someone signed out: no
            // marker on any, so the script never puts one in the box
            Assert.False(HasMarker(await admin.Client.GetAsync("/admin", Token)));
            var unknown = await RefreshAsync(admin, "/admin?handler=other");
            Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
            Assert.Contains("<h1>Overview</h1>", await unknown.Content.ReadAsStringAsync(Token));
            Assert.False(HasMarker(unknown));

            var anonymous = factory.Visitor("203.0.113.126");
            var refused = await RefreshAsync(anonymous);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.False(HasMarker(refused));
            var redirected = await anonymous.Client.GetAsync(StatusBoxes.Address, Token);
            Assert.Equal(HttpStatusCode.Redirect, redirected.StatusCode);
            Assert.Equal("/signin?returnUrl=%2Fadmin%3Fhandler%3Dstatus", SignInSteps.PathOf(redirected.Headers.Location));
            Assert.False(HasMarker(redirected));

            // MinerBot's database down, behind a cookie: the 503 page, written on a cleared response, has no marker either
            var deadDatabase = new DeadDatabase();
            await using var deadFactory = new DashboardFactory(db, new DashboardFactoryOptions { BotConnection = deadDatabase.BotConnection });
            var signedOut = deadFactory.Visitor("203.0.113.127");
            signedOut.Jar.Add(DashboardFactory.PublicAddress, new Cookie(DashboardAuth.SessionCookie, SessionKeys.New()) { Secure = true });
            var unavailable = await RefreshAsync(signedOut);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.False(HasMarker(unavailable));
        }

        // A fragment whose own read fails gets the usual 503 page, which the exception handler writes on a cleared response,
        // so it never carries the marker, however the marker was set
        [Fact]
        public async Task FragmentWhoseReadFailsHasNoMarker()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IBotStatusReads, FailingStatusReads>())
            });
            var admin = factory.Visitor("203.0.113.134");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            var failed = await RefreshAsync(admin);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.False(HasMarker(failed));
            Assert.Contains(ErrorResponses.Unavailable, WebUtility.HtmlDecode(await failed.Content.ReadAsStringAsync(Token)));
        }

        // Every read fails as a database that stopped answering would
        sealed class FailingStatusReads : IBotStatusReads
        {
            static TimeoutException Dropped() => new("The database didn't answer within 15 seconds");

            public Task<IReadOnlyList<EventCountRow>> CountEvents(IReadOnlyCollection<string> kinds, DateTime from, DateTime recentFrom) => throw Dropped();
            public Task<UptimeTotalRow> CountSamples(DateTime from, DateTime to) => throw Dropped();
            public Task<IReadOnlyList<UptimeDayRow>> CountSampleDays(DateTime from, DateTime to) => throw Dropped();
            public Task<BotEventRow?> FirstEventAfter(string kind, BotEventRow? after) => throw Dropped();
            public Task<UptimeStartRow> FirstRecord() => throw Dropped();
            public Task<BotStatusRow?> GetStatus() => throw Dropped();
            public Task<BotEventRow?> LatestEvent(string kind) => throw Dropped();
            public Task<BotEventRow?> LatestEventWith(string kind, IReadOnlyCollection<string> phrases, DateTime since) => throw Dropped();
            public Task<DataAccessLibrary.Page<BotEventRow>> ListEvents(EventQuery query) => throw Dropped();
            public Task<IReadOnlyList<JobRunRow>> ListJobRuns(IReadOnlyCollection<string> jobs, int perJob) => throw Dropped();
            public Task<IReadOnlyList<JobStatusRow>> ListJobs() => throw Dropped();
            public Task<IReadOnlyList<UptimeDayRow>> ListUptimeDays(DateTime from, DateTime to) => throw Dropped();
        }

        // A page left open refreshes its box every 30 seconds, and none of those refreshes counts as use: the session still
        // ends 30 minutes after it was last touched. A page view touches it as before.
        [Fact]
        public async Task RefreshesNeverKeepASessionGoing()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.128");
            var key = await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var signedIn = (await db.SessionAsync(key))!.last_seen_at;

            // Every 5 minutes for 25, well past the minute a touch waits for, and below the 60 a minute one address may make
            for (var minutes = 5; minutes < 30; minutes += 5)
            {
                factory.Clock.Advance(TimeSpan.FromMinutes(5));
                Assert.True((await RefreshAsync(owner)).StatusCode == HttpStatusCode.OK, $"The refresh after {minutes} minutes failed");
                Assert.Equal(signedIn, (await db.SessionAsync(key))!.last_seen_at);
            }

            // A marked request for a page is no use either
            Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(owner, "/admin/bot")).StatusCode);
            Assert.Equal(signedIn, (await db.SessionAsync(key))!.last_seen_at);

            factory.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
            Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(owner)).StatusCode);

            factory.Clock.Advance(TimeSpan.FromSeconds(1));
            var ended = await RefreshAsync(owner);
            Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
            Assert.False(HasMarker(ended));
            Assert.Null(await db.SessionAsync(key));
        }

        [Fact]
        public async Task PageViewStillTouchesTheSession()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.129");
            var key = await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var signedIn = (await db.SessionAsync(key))!.last_seen_at;

            factory.Clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(owner)).StatusCode);
            Assert.Equal(signedIn, (await db.SessionAsync(key))!.last_seen_at);

            Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync("/admin", Token)).StatusCode);
            Assert.Equal(signedIn.AddMinutes(2), (await db.SessionAsync(key))!.last_seen_at);

            // The fragment opened without the marker is use too, like any request
            factory.Clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync(StatusBoxes.Address, Token)).StatusCode);
            Assert.Equal(signedIn.AddMinutes(4), (await db.SessionAsync(key))!.last_seen_at);

            // And the marker means nothing on a request that isn't a GET
            factory.Clock.Advance(TimeSpan.FromMinutes(2));
            var head = new HttpRequestMessage(HttpMethod.Head, "/admin");
            head.Headers.Add(BackgroundRefresh.Header, BackgroundRefresh.Value);
            await owner.Client.SendAsync(head, Token);
            Assert.Equal(signedIn.AddMinutes(6), (await db.SessionAsync(key))!.last_seen_at);
        }

        // The version and starts, uptime from the days the clean-up totalled and the samples after them, the jobs with
        // their schedules in words and their last 50 runs, and the events, 50 to a page, without routine reconnects unless
        // asked, and under the status the way to the bot's controls
        [Fact]
        public async Task BotPageShowsVersionUptimeJobsAndEvents()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var minute = UptimeDays.Minute(now);
            var today = minute.Date;
            var status = db.Status();

            await status.SaveStatus(StatusSteps.Status(now.AddSeconds(-10)));

            // Starts 1 hour, 3 days, 20 days and 40 days ago, an unclean end 10 days ago, a routine reconnect, another
            // drop, and enough Steam logons for two pages
            var events = new List<BotEvent>
            {
                StatusSteps.Event(1, now.AddDays(-40), "bot.started", EventLevel.Info, "version " + StatusSteps.Commit),
                StatusSteps.Event(2, now.AddDays(-20), "bot.started", EventLevel.Info, "version " + StatusSteps.Commit),
                StatusSteps.Event(3, now.AddDays(-10), "bot.unclean_end", EventLevel.Warning, "the run that started at 2026-11-01 00:00:00 UTC never stopped cleanly"),
                StatusSteps.Event(4, now.AddDays(-3), "bot.started", EventLevel.Info, "version " + StatusSteps.Commit),
                StatusSteps.Event(5, now.AddHours(-1), "bot.started", EventLevel.Info, "version " + StatusSteps.Commit),
                StatusSteps.Event(6, now.AddMinutes(-50), RoutineReconnect.Kind, EventLevel.Warning, RoutineReconnect.Detail),
                StatusSteps.Event(7, now.AddMinutes(-40), RoutineReconnect.Kind, EventLevel.Warning, "WebSocketException")
            };
            events.AddRange(Enumerable.Range(0, 50).Select(i => StatusSteps.Event((uint)(8 + i), now.AddMinutes(-30).AddSeconds(i), "steam.logged_on", EventLevel.Info, "after 37 s offline")));
            await status.AddEvents(events);

            // Recording began 2 days ago at 12:00, with Discord down for 10 of its 720 minutes, and that day totalled by the
            // bot's own clean-up. Yesterday has no samples, so it was down all day, and today has its last 30 minutes.
            var first = today.AddDays(-2).AddHours(12);
            var samples = Enumerable.Range(0, 720).Select(i => new UptimeSample { minute = first.AddMinutes(i), discord_up = i >= 10, steam_up = true }).ToList();
            var todayFrom = minute.AddMinutes(-30) > today ? minute.AddMinutes(-30) : today;
            samples.AddRange(Enumerable.Range(0, (int)(minute - todayFrom).TotalMinutes).Select(i => new UptimeSample { minute = todayFrom.AddMinutes(i), discord_up = true, steam_up = true }));

            foreach (var batch in samples.Chunk(100))
                await status.SaveSamples(batch);

            await db.Retention().TotalUptimeDays(today.AddDays(-2), today.AddDays(-1));

            var todayUp = (int)(minute - todayFrom).TotalMinutes;
            var todayMinutes = (int)(minute - today).TotalMinutes;

            await status.SaveJobStatus(
            [
                StatusSteps.Job("ArmorJob.AddColorArrays", "0 * * * *", now, JobResult.Ok),
                StatusSteps.Job("GuildMembersJob.CountMembers", "21 4 * * *", now, JobResult.Ok, "counted the members of 10 of 10 servers"),
                StatusSteps.Job("GuildNotificationJob.NotifyAllGuilds", "*/5 * * * *", now, JobResult.Ok)
            ]);
            await status.SaveJobRuns(Enumerable.Range(0, 55).Select(i => StatusSteps.Run((uint)(100 + i), "GuildNotificationJob.NotifyAllGuilds", now.AddMinutes(-5 * (i + 2)), JobResult.Ok)).ToList());
            await status.SaveJobRuns([StatusSteps.Run(200, "GuildMembersJob.CountMembers", now.AddHours(-10), JobResult.Failed, "TimeoutException")]);

            var owner = factory.Visitor("203.0.113.130");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var html = await owner.Client.GetStringAsync("/admin/bot", Token);
            var text = Text(WebUtility.HtmlDecode(html));
            PageTests.AssertAccessible("the Bot status page", html);
            PageTests.AssertTablesTakeTheKeyboard("the Bot status page", html);
            PageTests.AssertTimesStayWhole("the Bot status page", html);

            Assert.Contains($"<a href=\"https://github.com/24kConnectLLC/MinerBot/commit/{StatusSteps.Commit}\" class=\"address\">5a62c9f</a>", html);
            Assert.Contains("<a href=\"/admin/bot/controls\">Bot controls</a>", html);
            Assert.Contains("Starts 2 starts in the last 7 days, 3 starts in the last 30", text);
            Assert.Contains("Ends without a clean stop 0 in the last 7 days, 1 in the last 30", text);

            Assert.Contains($"Last 7 days {UptimeDays.Percent(710 + todayUp, 720 + 1440 + todayMinutes)}", text);
            Assert.Contains($"Last 24 hours {UptimeDays.Percent(todayUp, 1440)}", text);
            Assert.Equal(UptimeDays.StripDays, Regex.Count(html, "<li class=\"strip-"));
            Assert.Equal(UptimeDays.StripDays - 2 - (todayMinutes > 0 ? 1 : 0), Regex.Count(html, "<li class=\"strip-none\""));
            Assert.Contains($"<li class=\"strip-fair\" title=\"{TimeWords.Day(today.AddDays(-2))}: Discord 98.61% (710 of 720 minutes), Steam 100.00% (720 of 720 minutes)\"></li>", html);
            Assert.Contains($"<li class=\"strip-poor\" title=\"{TimeWords.Day(today.AddDays(-1))}: Discord 0.00% (0 of 1,440 minutes), Steam 0.00% (0 of 1,440 minutes)\"></li>", html);

            Assert.Contains("every 5 minutes", text);
            Assert.Contains("every day at 04:21 Eastern", text);
            Assert.Contains("every hour, on the hour", text);
            Assert.Contains("The last runs of Notifications (50)", text);
            Assert.Contains("The last runs of Member counts (1)", text);
            Assert.Contains("The last runs of Armor save strings (0)", text);
            Assert.Contains("counted the members of 10 of 10 servers", text);
            Assert.Contains("in 4 minutes", text);

            // 50 of the 56 events that aren't routine on the first page, newest first, and the rest on the second
            Assert.Contains("56 events, newest first.", text);
            Assert.DoesNotContain(EventWords.RoutineLabel, text);
            Assert.Contains("Info Logged on to Steam after 37 s offline", text);
            Assert.Contains("Page 1 of 2", text);
            Assert.Contains("href=\"/admin/bot?page=2#events\"", html);

            var routine = Text(await PageAsync(owner, "/admin/bot?routine=1&page=2"));
            Assert.Contains("57 events, newest first.", routine);
            Assert.Contains($"Routine {EventWords.RoutineLabel} {RoutineReconnect.Detail}", routine);
            Assert.Contains("href=\"/admin/bot?routine=1#events\"", await owner.Client.GetStringAsync("/admin/bot?routine=1&page=2", Token));

            var second = Text(await PageAsync(owner, "/admin/bot?page=2"));
            Assert.Contains("Page 2 of 2", second);
            Assert.Contains("Warning Disconnected from Discord WebSocketException", second);
            Assert.Contains("Started version", second);

            var filtered = Text(await PageAsync(owner, "/admin/bot?level=warning&kind=bot.unclean_end"));
            Assert.Contains("1 event that matches the filters, newest first.", filtered);
            Assert.Contains("Ended without a clean stop", filtered);
            Assert.Contains("There's nothing on page 9.", Text(await PageAsync(owner, "/admin/bot?page=9")));

            // An Admin sees the same page
            var admin = factory.Visitor("203.0.113.131");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            Assert.Contains("56 events, newest first.", Text(await PageAsync(admin, "/admin/bot")));

            // The status box's note, as on the Overview
            Assert.Contains(StoppedNote, html);
        }

        // Each uptime row covers its own stretch: the last 24 hours from the samples, then the strip's last 7, 30 and 90 days.
        // The record began 80 days ago with a day the clean-up totalled and then deleted the samples of, and the only
        // samples since are an hour from 3 hours ago and the last half hour, so no two rows hold the same minutes.
        [Fact]
        public async Task EachUptimeRowCoversItsOwnPeriod()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var minute = UptimeDays.Minute(now);
            var today = minute.Date;
            var status = db.Status();
            await status.SaveStatus(StatusSteps.Status(now.AddSeconds(-10)));

            // The first day: Discord up in its first 100 minutes, and Steam in the last 50 of those
            var first = today.AddDays(-80);
            await status.SaveSamples(Enumerable.Range(0, 100).Select(i => new UptimeSample { minute = first.AddMinutes(i), discord_up = true, steam_up = i >= 50 }).ToList());
            await db.Retention().TotalUptimeDays(first, first.AddDays(1));
            await db.Retention().DeleteSamples(first.AddDays(1), RetentionData.ChunkLimit);

            // In the last 24 hours, with only the last half hour in the last hour: Discord up throughout, Steam down for the
            // first 20 minutes of the hour
            var recent = Enumerable.Range(0, 60).Select(i => new UptimeSample { minute = minute.AddMinutes(i - 180), discord_up = true, steam_up = i >= 20 })
                .Concat(Enumerable.Range(0, 30).Select(i => new UptimeSample { minute = minute.AddMinutes(i - 30), discord_up = true, steam_up = true }));

            foreach (var batch in recent.Chunk(100))
                await status.SaveSamples(batch);

            var owner = factory.Visitor("203.0.113.136");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var text = Text(await PageAsync(owner, "/admin/bot"));

            static string Row(string label, int minutes, int discord, int steam) =>
                $"{label} {UptimeDays.Percent(discord, minutes)} ({TimeWords.Count(minutes - discord, "minute")} down) " +
                $"{UptimeDays.Percent(steam, minutes)} ({TimeWords.Count(minutes - steam, "minute")} down)";

            var todayMinutes = (int)(minute - today).TotalMinutes;
            Assert.Contains(Row("Last 24 hours", 1440, 90, 70), text);
            Assert.Contains(Row("Last 7 days", 6 * 1440 + todayMinutes, 90, 70), text);
            Assert.Contains(Row("Last 30 days", 29 * 1440 + todayMinutes, 90, 70), text);
            Assert.Contains(Row("Last 90 days", 80 * 1440 + todayMinutes, 190, 120), text);
        }

        // The bot records Discord's reconnect as a drop and then the connection after it. Only the drop is routine and left
        // out, so the connection still shows, and the box's hint says so rather than that the reconnect is left out.
        [Fact]
        public async Task RoutineReconnectLeavesOutOnlyItsDrop()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var status = db.Status();
            var drop = now.AddMinutes(-20);

            await status.SaveStatus(StatusSteps.Status(now.AddSeconds(-10)));
            await status.AddEvents(
            [
                StatusSteps.Event(1, drop, RoutineReconnect.Kind, EventLevel.Warning, RoutineReconnect.Detail),
                StatusSteps.Event(2, drop.AddSeconds(1), "discord.connected", EventLevel.Info,
                    string.Create(CultureInfo.InvariantCulture, $"after 1 s down, since {drop:yyyy-MM-dd HH:mm:ss} UTC"))
            ]);

            var owner = factory.Visitor("203.0.113.133");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            var shown = Text(await PageAsync(owner, "/admin/bot"));
            Assert.Contains("1 event, newest first.", shown);
            Assert.Contains("Info Connected to Discord after 1 s down", shown);
            Assert.DoesNotContain(EventWords.RoutineLabel, shown);
            Assert.Contains("Show routine reconnects Discord asks the bot to reconnect now and then, and it does so at once. The disconnects Discord asks for are left out unless this is ticked, though the Connected to Discord row after each one still shows.", shown);

            var all = Text(await PageAsync(owner, "/admin/bot?routine=1"));
            Assert.Contains("2 events, newest first.", all);
            Assert.Contains($"Routine {EventWords.RoutineLabel} {RoutineReconnect.Detail}", all);
        }

        // The 90-day strip's bands differ in height as well as in colour, from the full cell for 99.9% down to the
        // shortest below 95%, so a day can be told from another without telling red from green. No record stays a full
        // outline.
        [Fact]
        public async Task StripBandsDifferInHeightNotOnlyInColour()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyBotAsync());

            var css = await factory.Visitor().Client.GetStringAsync("/css/site.css", Token);

            double Height(string selector)
            {
                var rule = Regex.Match(css, @"\n" + Regex.Escape(selector) + @" \{[^}]*\}").Value;
                var height = Regex.Match(rule, @"\n\s*height: ([0-9.]+)rem;");
                Assert.True(height.Success, $"{selector} sets no height in rem");
                return double.Parse(height.Groups[1].Value, CultureInfo.InvariantCulture);
            }

            var full = Height(".strip li");
            var good = Height(".strip .strip-good");
            var fair = Height(".strip .strip-fair");
            var poor = Height(".strip .strip-poor");

            Assert.True(full > good && good > fair && fair > poor && poor > 0, $"heights {full}, {good}, {fair}, {poor}");
            Assert.Contains("align-items: end;", Regex.Match(css, @"\n\.strip \{[^}]*\}").Value);
        }

        // The filters' selects and box send nothing else, so anything else is a made-up address
        [Theory]
        [InlineData("?level=loud")]
        [InlineData("?level=Warning")]
        [InlineData("?kind=bot.renamed")]
        [InlineData("?kind=Bot.Started")]
        [InlineData("?routine=yes")]
        public async Task MadeUpFiltersAreRefused(string query)
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var admin = factory.Visitor("203.0.113.132");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.GetAsync("/admin/bot" + query, Token)).StatusCode);
        }

        // With nothing recorded at all, every part says so, and nothing fails
        [Fact]
        public async Task BotPageWithNothingRecorded()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var admin = factory.Visitor("203.0.113.133");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            var html = await admin.Client.GetStringAsync("/admin/bot", Token);
            var text = Text(WebUtility.HtmlDecode(html));
            PageTests.AssertAccessible("the Bot status page with nothing recorded", html);

            Assert.Contains("Offline Never checked in.", text);
            Assert.Contains("Version Not recorded", text);
            Assert.Contains("Last 90 days no record", text);
            Assert.Equal(UptimeDays.StripDays, Regex.Count(html, "<li class=\"strip-none\""));
            Assert.Contains("No jobs are recorded yet", text);
            Assert.Contains("No events are recorded.", text);
        }
    }
}
