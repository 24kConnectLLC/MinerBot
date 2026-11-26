using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary;
using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MinerBot.Dashboard.Armor;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Status;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Armor;
using MinerBot.Shared.Requests;
using static MinerBot.Dashboard.Tests.Support.ArmorSteps;

namespace MinerBot.Dashboard.Tests
{
    // What follows an armor review on the bot's side: the cache clear after an approval, a take-down or a move back from
    // Approved, sent once the review has committed and never able to fail it, and Announce now: with a bot that keeps the
    // record of its posts, offered for approved armor until the bot has found it, restarts included; with an older one,
    // offered for approved armor the bot may post until a notification run has started since the approval, unless the bot
    // has stopped cleanly or started again since its last run, which most tests here pin with an older bot's status row
    [Collection("Database")]
    public partial class ArmorFollowUpTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        const string NotificationJob = "GuildNotificationJob.NotifyAllGuilds";

        // A page's text without its markup, so a sentence reads as one string
        static string Text(string html) => Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(html), "<[^>]+>", " "), "\\s+", " ");

        static async Task<List<DataAccessLibrary.Requests.Models.BotRequestRow>> ClearsAsync(MariaDbFixture db) =>
            (await db.SentAsync()).Where(row => row.kind == RequestKinds.CachesClear).ToList();

        // The bot's status row as a bot from before PR 11 writes it, which keeps what it finds only in memory: its write names
        // no posting_run_id, so the column keeps what a newer bot left there, here none. The rules for such a bot are today's
        // Restarted and Stopped states, which these tests pin; a bot that keeps what it finds is NewBot's below.
        static async Task OlderBotAsync(MariaDbFixture db, BotStatus status)
        {
            await db.Status().SaveStatus(status);
            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.StatusTable} SET posting_run_id = NULL");
        }

        // The notification job's row as the bot writes it, with its last run started and ended at the times given
        static Task RunAsync(MariaDbFixture db, DateTime started, DateTime? ended) => db.Status().SaveJobStatus(
        [
            new JobStatus
            {
                job = NotificationJob,
                schedule = "*/5 * * * *",
                next_run_at = started.AddMinutes(5),
                last_started_at = started,
                last_ended_at = ended,
                last_result = ended == null ? null : JobResult.Ok,
                updated_at = ended ?? started
            }
        ]);

        // Announce now's form on a review page: its key, which the page drew it with
        static string AnnounceKey(string html)
        {
            var match = AnnounceForm().Match(html);
            Assert.True(match.Success, "The page has no Announce now form");
            return match.Groups[1].Value;
        }

        static Task<HttpResponseMessage> AnnounceAsync(TestBrowser browser, long id, ReviewForm form, string key) =>
            SignInSteps.PostAsync(browser, $"/admin/armor/{id}?handler=Announce", form.Token, origin: DashboardFactory.PublicOrigin, fields: [new("formkey", key)]);

        // An approval asks the bot to clear its caches as the reviewer, Owner or Admin, after the review's own rows, and leads
        // to the clear's box beside the notice. Fails if the clear is sent before the commit, or not at all.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task AnApprovalAsksTheBotToClearItsCachesAfterTheCommit(bool asOwner)
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(61));
            await using var factory = new DashboardFactory(db);
            var user = asOwner ? TestUsers.Owner : TestUsers.Admin;
            var reviewer = await ReviewerAsync(factory, "198.51.100.130", user);

            var response = await PostAsync(reviewer, 61, "Approve", await OpenAsync(reviewer, 61));

            var clear = Assert.Single(await db.SentAsync());
            var location = SignInSteps.PathOf(response.Headers.Location)!;
            Assert.Equal($"/admin/armor/61?done=approved&request={clear.id}#result", location);
            Assert.Equal((RequestKinds.CachesClear, user.Id, asOwner ? RequestRoles.Owner : RequestRoles.Admin), (clear.kind, clear.actor_id, clear.actor_role));
            Assert.Equal(ArmorStatus.Approved, await db.StatusAsync(61));

            // Its audit row comes after the approval's, which committed first
            var audit = await db.AuditAsync();
            var approval = Assert.Single(audit, entry => entry.action == "armor.approve");
            var asked = Assert.Single(audit, entry => entry.action == RequestKinds.CachesClear);
            Assert.True(asked.id > approval.id, "The clear was recorded before the approval");
            Assert.Equal((AuditOutcome.Ok, clear.id.ToString(CultureInfo.InvariantCulture), "198.51.100.130"), (asked.outcome, asked.target_id!, asked.ip!));

            // The page it leads to says the bot was asked, and shows the request's box until the bot answers
            var html = await reviewer.Client.GetStringAsync(location, Token);
            var text = Text(html);
            Assert.Contains(ArmorWords.Approved, text);
            Assert.Contains(ArmorWords.CacheClearing, text);
            Assert.Contains($"Clear caches, request {clear.id}, asked for by {user.DisplayName}", text);
            Assert.True(PageTests.LoadsRefreshScript(html), "The page with the clear's box doesn't load refresh.js");
            PageTests.AssertAccessible("an approval's page with the clear's box", html);
        }

        // A take-down, and a move back to pending from Approved, change what the bot shows as an approval does, so each
        // clears too. A rejection, and a move back from Not Approved, change nothing the bot shows, and send none.
        [Fact]
        public async Task TakeDownAndBackToPendingFromApprovedClearTooAndOthersDont()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(62, ArmorStatus.Approved, "Test Armor 62"), Submission(63, ArmorStatus.Approved, "Test Armor 63"),
                Submission(64, ArmorStatus.Pending, "Test Armor 64"), Submission(65, ArmorStatus.NotApproved, "Test Armor 65"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "198.51.100.131");

            var takenDown = await PostAsync(admin, 62, "TakeDown", await OpenAsync(admin, 62));
            var backFromApproved = await PostAsync(admin, 63, "BackToPending", await OpenAsync(admin, 63));
            var clears = await ClearsAsync(db);

            Assert.Equal(2, clears.Count);
            Assert.Equal($"/admin/armor/62?done=taken-down&request={clears[0].id}#result", SignInSteps.PathOf(takenDown.Headers.Location));
            Assert.Equal($"/admin/armor/63?done=pending&request={clears[1].id}#result", SignInSteps.PathOf(backFromApproved.Headers.Location));

            var taken = Text(await admin.Client.GetStringAsync(SignInSteps.PathOf(takenDown.Headers.Location), Token));
            Assert.Contains(ArmorWords.TakenDown, taken);
            Assert.Contains(ArmorWords.CacheClearing, taken);
            Assert.Contains(ArmorWords.MovedBackFromApproved, Text(await admin.Client.GetStringAsync(SignInSteps.PathOf(backFromApproved.Headers.Location), Token)));

            var rejected = await PostAsync(admin, 64, "Reject", await OpenAsync(admin, 64));
            var backFromNotApproved = await PostAsync(admin, 65, "BackToPending", await OpenAsync(admin, 65));

            Assert.Equal("/admin/armor/64?done=rejected", SignInSteps.PathOf(rejected.Headers.Location));
            Assert.Equal("/admin/armor/65?done=pending", SignInSteps.PathOf(backFromNotApproved.Headers.Location));
            Assert.Equal(2, (await db.SentAsync()).Count);
            Assert.DoesNotContain(ArmorWords.CacheClearing, Text(await admin.Client.GetStringAsync("/admin/armor/65?done=pending", Token)));
        }

        // A clear that can't be sent never fails the review: with requests off, past the limits, with MinerBot's database not
        // answering, or with its commit unconfirmed, the armor is approved, the page is the approval's, and its line says
        // why the cache wasn't cleared. Nobody pressed anything for the clear, so no refusal is recorded for it.
        [Theory]
        [InlineData(ArmorWords.UnsentOff)]
        [InlineData(ArmorWords.UnsentLimit)]
        [InlineData(ArmorWords.UnsentUnavailable)]
        [InlineData(ArmorWords.UnsentUnsure)]
        public async Task AClearThatCantBeSentLeavesTheReviewDoneAndSaysWhy(string unsent)
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(66));
            await using var factory = new DashboardFactory(db, unsent switch
            {
                ArmorWords.UnsentOff => new DashboardFactoryOptions { Settings = new Dictionary<string, string?> { ["Dashboard:RequestSigningKey"] = "" } },
                ArmorWords.UnsentUnavailable => new DashboardFactoryOptions
                {
                    Services = services => services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider => new UnavailableQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider))))
                },
                ArmorWords.UnsentUnsure => new DashboardFactoryOptions
                {
                    Services = services => services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider => new UnconfirmedQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider))))
                },
                _ => null
            });
            var admin = await ReviewerAsync(factory, "198.51.100.132");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            if (unsent == ArmorWords.UnsentLimit)
                await RequestSteps.AddRefusedAsync(db, RequestKinds.CachesClear, TestUsers.Admin, Enumerable.Range(1, 20).Select(minute => now.AddMinutes(-minute)));

            var response = await PostAsync(admin, 66, "Approve", await OpenAsync(admin, 66));
            var location = SignInSteps.PathOf(response.Headers.Location)!;

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal($"/admin/armor/66?done=approved&unsent={unsent}", location);
            Assert.Equal(ArmorStatus.Approved, await db.StatusAsync(66));

            var page = await admin.Client.GetAsync(location, Token);
            var text = Text(await page.Content.ReadAsStringAsync(Token));
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains(ArmorWords.Approved, text);
            Assert.Contains(ArmorWords.CacheNotCleared(unsent)!, text);
            Assert.DoesNotContain(await db.AuditAsync(), entry => entry.action == RequestKinds.CachesClear && entry.outcome == AuditOutcome.Refused);
        }

        // A review that didn't change the armor sends nothing: blocked by the rules, changed since the page showed it, a
        // warning left unticked, the same approval sent twice, and one whose commit wasn't confirmed. Fails if the clear goes
        // out whatever the review's outcome.
        [Fact]
        public async Task ARefusedOrUnsureReviewSendsNothing()
        {
            var db = await fixture.ReadyBotAsync();
            var blocked = Submission(71, ArmorStatus.Pending, "Night`Raid");
            var warned = Submission(72, ArmorStatus.Pending, "Test Armor 72");
            warned.ArmorDescription = "Visit https://example.com";
            await db.AddAsync(blocked, warned, Submission(73, ArmorStatus.Pending, "Test Armor 73"), Submission(74, ArmorStatus.Pending, "Test Armor 74"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "198.51.100.133");

            Assert.Equal(HttpStatusCode.Conflict, (await RequestSteps.AnsweredAsync(admin, await PostAsync(admin, 71, "Approve", await OpenAsync(admin, 71)))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict,
                (await RequestSteps.AnsweredAsync(admin, await PostAsync(admin, 72, "Approve", await OpenAsync(admin, 72), tickAll: false))).StatusCode);

            var stale = await OpenAsync(admin, 73);
            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.ArmorTable} SET ArmorDescription = 'Changed after the page opened' WHERE ID = 73");
            Assert.Equal(HttpStatusCode.Conflict, (await RequestSteps.AnsweredAsync(admin, await PostAsync(admin, 73, "Approve", stale))).StatusCode);

            Assert.Empty(await db.SentAsync());

            // Sent twice: the second finds the armor approved already, and sends no second clear
            var twice = await OpenAsync(admin, 74);
            Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(admin, 74, "Approve", twice)).StatusCode);
            var already = await RequestSteps.AnsweredAsync(admin, await PostAsync(admin, 74, "Approve", twice));
            Assert.Equal(HttpStatusCode.Conflict, already.StatusCode);
            Assert.Contains(ArmorWords.Already(ArmorStatus.Approved), Text(await already.Content.ReadAsStringAsync(Token)));
            Assert.Single(await db.SentAsync());

            // A commit that may or may not have landed
            await db.AddAsync(Submission(75));
            await using var late = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IArmorSubmissionData>(provider => new LateSubmissions(ActivatorUtilities.CreateInstance<ArmorSubmissionData>(provider))))
            });
            var lateAdmin = await ReviewerAsync(late, "198.51.100.134");
            var unsure = await RequestSteps.AnsweredAsync(lateAdmin, await PostAsync(lateAdmin, 75, "Approve", await OpenAsync(lateAdmin, 75)));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, unsure.StatusCode);
            Assert.Contains(ArmorWords.Unsure, Text(await unsure.Content.ReadAsStringAsync(Token)));
            Assert.Single(await db.SentAsync());
        }

        // With a bot that keeps the record of its posts, Announce now is offered for approved armor until the bot has found it,
        // whatever its runs and starts since the approval: a run since then, or a start with no run since, which an older bot's
        // rules take as seen or lost, leave it offered, and the record's row of the armor takes it away, the page then saying
        // where it was posted. While posts are paused, a run is under way or the bot is stopped, its place says the armor waits,
        // restarts included. Fails if the new bot gets an older bot's Restarted line, or Announce now goes with the first run.
        [Fact]
        public async Task NewBotOffersAnnounceNowUntilTheArmorIsFound()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(110));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "198.51.100.235");
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var bot = StatusSteps.Status(now);
            await db.Status().SaveStatus(bot);
            await RunAsync(db, now.AddMinutes(-1), now.AddMinutes(-1).AddSeconds(3));

            var approval = await PostAsync(admin, 110, "Approve", await OpenAsync(admin, 110));
            var done = Text(await admin.Client.GetStringAsync(SignInSteps.PathOf(approval.Headers.Location), Token));
            Assert.Contains(ArmorWords.ApprovedKept, done);
            Assert.Contains("?handler=Announce\"", (await OpenAsync(admin, 110)).Html);

            // A run since the approval, then a start with no run since: still offered
            factory.Clock.Advance(TimeSpan.FromSeconds(30));
            await RunAsync(db, now.AddSeconds(10), now.AddSeconds(12));
            Assert.Contains("?handler=Announce\"", (await OpenAsync(admin, 110)).Html);

            bot.run_id = 7;
            bot.started_at = now.AddSeconds(20);
            bot.heartbeat_at = now.AddSeconds(25);
            await db.Status().SaveStatus(bot);
            var started = await OpenAsync(admin, 110);
            Assert.Contains("?handler=Announce\"", started.Html);
            Assert.DoesNotContain("no notification run since is recorded yet", started.Text);
            Assert.Contains(ArmorWords.PostNoteKept, started.Text);

            // Its place while paused, while a run is under way, and while the bot is stopped
            await db.Requests().SavePause(true, "Testing the pause", now, TestUsers.Owner.Id, 1, now);
            Assert.Contains(ArmorWords.AnnouncePausedKept, (await OpenAsync(admin, 110)).Text);
            await db.ClearAsync(MariaDbFixture.SettingsTable);

            await RunAsync(db, now.AddSeconds(28), null);
            Assert.Contains(ArmorWords.AnnounceRunningKept(RequestWords.Clock(now.AddSeconds(28))), (await OpenAsync(admin, 110)).Text);
            await RunAsync(db, now.AddSeconds(28), now.AddSeconds(29));

            bot.stopped_at = bot.heartbeat_at;
            await db.Status().SaveStatus(bot);
            var stopped = await OpenAsync(admin, 110);
            Assert.Contains(ArmorWords.AnnounceStoppedKept, stopped.Text);
            Assert.DoesNotContain("handler=Announce", stopped.Html);

            bot.run_id = 8;
            bot.stopped_at = null;
            await db.Status().SaveStatus(bot);
            Assert.Contains("?handler=Announce\"", (await OpenAsync(admin, 110)).Html);

            // Found by the bot: Announce now goes, and where it was posted shows
            await PostSteps.QueueAsync(db, PostSource.Armor, 110, now.AddMinutes(1), RequestSteps.ServerId);
            var found = await OpenAsync(admin, 110);
            Assert.DoesNotContain("id=\"announce\"", found.Html);
            Assert.Contains("<h2 id=\"posted\">Where it was posted</h2>", found.Html);
            Assert.Contains(ArmorWords.PostMayNotComeNoteKept, found.Text);
            PageTests.AssertAccessible("a review page of armor the bot has found", found.Html);
        }

        // With an older bot, which keeps what it finds only in memory, today's rules and words stand: Announce now goes once
        // a run has started since the approval, a start with no run since replaces it with the Restarted line, a stop with
        // the Stopped one, and the approval's words say the bot may not post it. No Where it was posted, which only a bot that
        // keeps the record has.
        [Fact]
        public async Task OlderBotKeepsTheRestartedAndStoppedStates()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(111));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "198.51.100.236");
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var bot = StatusSteps.Status(now);
            await OlderBotAsync(db, bot);
            await RunAsync(db, now.AddMinutes(-1), now.AddMinutes(-1).AddSeconds(3));

            var approval = await PostAsync(admin, 111, "Approve", await OpenAsync(admin, 111));
            Assert.Contains(ArmorWords.Approved, Text(await admin.Client.GetStringAsync(SignInSteps.PathOf(approval.Headers.Location), Token)));
            var offered = await OpenAsync(admin, 111);
            Assert.Contains("?handler=Announce\"", offered.Html);
            Assert.Contains(ArmorWords.PostNote, offered.Text);
            Assert.DoesNotContain("id=\"posted\"", offered.Html);

            bot.run_id = 7;
            bot.started_at = now.AddSeconds(20);
            bot.heartbeat_at = now.AddSeconds(25);
            await OlderBotAsync(db, bot);
            factory.Clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Contains(ArmorWords.AnnounceRestarted(TimeWords.Utc(bot.started_at)), (await OpenAsync(admin, 111)).Text);

            bot.stopped_at = bot.heartbeat_at;
            await OlderBotAsync(db, bot);
            Assert.Contains(ArmorWords.AnnounceStopped, (await OpenAsync(admin, 111)).Text);

            // A run since the approval takes Announce now away
            bot.run_id = 8;
            bot.stopped_at = null;
            await OlderBotAsync(db, bot);
            await RunAsync(db, now.AddSeconds(40), now.AddSeconds(42));
            factory.Clock.Advance(TimeSpan.FromSeconds(30));
            Assert.DoesNotContain("id=\"announce\"", (await OpenAsync(admin, 111)).Html);
        }

        // Announce now shows for approved armor until a notification run has started since the approval, which has seen it:
        // a run that started before the approval leaves it, one that started after takes it away
        [Fact]
        public async Task AnnounceNowIsOfferedOnlyUntilANotificationRunStarts()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(76));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "198.51.100.135");
            Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(admin, 76, "Approve", await OpenAsync(admin, 76))).StatusCode);
            var approved = factory.Clock.GetUtcNow().UtcDateTime;

            await RunAsync(db, approved.AddMinutes(-1), approved.AddMinutes(-1).AddSeconds(2));
            var before = await OpenAsync(admin, 76);
            Assert.Contains("?handler=Announce\"", before.Html);
            Assert.Contains(ArmorWords.AnnounceHelp, before.Text);
            Assert.Contains("Available now.", before.Text);
            PageTests.AssertAccessible("a review page with Announce now", before.Html);

            factory.Clock.Advance(TimeSpan.FromMinutes(1));
            await RunAsync(db, approved.AddSeconds(30), approved.AddSeconds(32));
            var after = await OpenAsync(admin, 76);
            Assert.DoesNotContain("handler=Announce", after.Html);
            Assert.DoesNotContain("id=\"announce\"", after.Html);
        }

        // Armor the bot may not post, as one approved from Not Approved, is never offered Announce now: the approval's notice
        // already says the bot may not post it
        [Fact]
        public async Task AnnounceNowIsNotOfferedForAnArmorTheBotMayNotPost()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(77, ArmorStatus.NotApproved, "Test Armor 77"), Submission(78, ArmorStatus.Pending, "Test Armor 78"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "198.51.100.136");

            var again = await PostAsync(admin, 77, "Approve", await OpenAsync(admin, 77));
            var first = await PostAsync(admin, 78, "Approve", await OpenAsync(admin, 78));

            var mayNotPost = await admin.Client.GetStringAsync(SignInSteps.PathOf(again.Headers.Location), Token);
            Assert.Contains(ArmorWords.ApprovedAgain, Text(mayNotPost));
            Assert.DoesNotContain("id=\"announce\"", mayNotPost);
            Assert.DoesNotContain("handler=Announce", mayNotPost);

            // The first approval of armor from the queue is offered it
            Assert.Contains("?handler=Announce\"", await admin.Client.GetStringAsync(SignInSteps.PathOf(first.Headers.Location), Token));
        }

        // Announce now sends the notification job, as the reviewer, and leads to its box; the same form sent again is that
        // first request. Armor that isn't approved now sends nothing.
        [Fact]
        public async Task AnnounceNowSendsTheNotificationJobOnce()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(79), Submission(80, ArmorStatus.Pending, "Test Armor 80"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "198.51.100.137");
            Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(admin, 79, "Approve", await OpenAsync(admin, 79))).StatusCode);

            var page = await OpenAsync(admin, 79);
            var key = AnnounceKey(page.Html);
            var first = await AnnounceAsync(admin, 79, page, key);
            var second = await AnnounceAsync(admin, 79, page, key);

            var job = Assert.Single(await db.SentAsync(), row => row.kind == RequestKinds.JobNotifications);
            Assert.Equal((TestUsers.Admin.Id, RequestRoles.Admin), (job.actor_id, job.actor_role));

            foreach (var response in new[] { first, second })
            {
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                Assert.Equal($"/admin/armor/79?request={job.id}#result", SignInSteps.PathOf(response.Headers.Location));
            }

            var html = await admin.Client.GetStringAsync($"/admin/armor/79?request={job.id}", Token);
            Assert.Contains($"Run the notification job, request {job.id}, asked for by Test Admin (Admin)", Text(html));
            Assert.True(PageTests.LoadsRefreshScript(html), "The page with Announce now's box doesn't load refresh.js");

            // Pending armor: refused before the sender, whatever the form held
            var pending = await OpenAsync(admin, 80);
            var refused = await RequestSteps.AnsweredAsync(admin, await AnnounceAsync(admin, 80, pending, RequestFields.NewKey()));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains(ArmorWords.NotApprovedNow, Text(await refused.Content.ReadAsStringAsync(Token)));
            Assert.Single(await db.SentAsync(), row => row.kind == RequestKinds.JobNotifications);
        }

        // Announce now whose save the database didn't confirm is a notice whose GET says it can't tell, at the top and by the
        // button, at 503, and draws the form with the key it was sent with, so pressing it again finds the first request
        // rather than sending a second. Fails if the key isn't carried to the notice's GET.
        [Fact]
        public async Task AnUnconfirmedAnnounceKeepsItsKey()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(95));
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider => new UnconfirmedQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider))))
            });
            var admin = await ReviewerAsync(factory, "198.51.100.146");
            Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(admin, 95, "Approve", await OpenAsync(admin, 95))).StatusCode);

            var page = await OpenAsync(admin, 95);
            var key = AnnounceKey(page.Html);
            var unsure = await RequestSteps.AnsweredAsync(admin, await AnnounceAsync(admin, 95, page, key));
            var html = await unsure.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, unsure.StatusCode);
            Assert.Equal(2, Regex.Count(Text(html), Regex.Escape(RequestWords.Unsure)));
            Assert.Equal(key, AnnounceKey(html));
            PageTests.AssertAccessible("an Announce now the database didn't confirm", html);
            var job = Assert.Single(await db.SentAsync(), row => row.kind == RequestKinds.JobNotifications);

            var again = await AnnounceAsync(admin, 95, Read(html), key);
            Assert.Equal($"/admin/armor/95?request={job.id}#result", SignInSteps.PathOf(again.Headers.Location));
            Assert.Single(await db.SentAsync(), row => row.kind == RequestKinds.JobNotifications);
        }

        // Announce now whose save the database didn't confirm, which the bot then took: once a notification run has started
        // since the approval, Announce now has no place on the page, so a reload of the notice's address, as the error page
        // advises while the database is down, draws no form, and the words don't offer one, but say where to look. Fails if
        // the words offer a form the page doesn't draw.
        [Fact]
        public async Task AnUnconfirmedAnnounceOnceARunHasStartedOffersNoForm()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(97));
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider => new UnconfirmedQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider))))
            });
            var admin = await ReviewerAsync(factory, "198.51.100.216");
            Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(admin, 97, "Approve", await OpenAsync(admin, 97))).StatusCode);
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var page = await OpenAsync(admin, 97);
            var address = await RequestSteps.NoticeOfAsync(await AnnounceAsync(admin, 97, page, AnnounceKey(page.Html)));
            var job = Assert.Single(await db.SentAsync(), row => row.kind == RequestKinds.JobNotifications);
            Assert.Equal(2, Regex.Count(Text(await (await admin.Client.GetAsync(address, Token)).Content.ReadAsStringAsync(Token)), Regex.Escape(RequestWords.Unsure)));

            await RequestSteps.ClaimAsync(db, job.id, now.AddSeconds(1));
            await RunAsync(db, now.AddSeconds(2), now.AddSeconds(4));
            await RequestSteps.FinishAsync(db, job.id, RequestStates.Done, "The notification job ran in 2 s.", now.AddSeconds(4));
            factory.Clock.Advance(TimeSpan.FromMinutes(3));

            var later = await admin.Client.GetAsync(address, Token);
            var html = await later.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, later.StatusCode);
            Assert.Contains(RequestWords.UnsureNotOffered, Text(html));
            Assert.DoesNotContain(RequestWords.Unsure, Text(html));
            Assert.DoesNotContain("handler=Announce", html);
            PageTests.AssertAccessible("an Announce now the database didn't confirm, once a notification run has started since", html);
        }

        // Announce now whose save the database didn't confirm, reloaded in the other ways its section loses the form: while
        // posts are paused, once the bot has started again with no notification run since, and once it has stopped cleanly.
        // Each draws its own words in the form's place, so the notice's words don't offer the form, but say where to look;
        // once posts are resumed, the form is back with the key it was sent with, and they offer it again. Fails if the words
        // offer a form the page doesn't draw in any of them, or don't once it's drawn again.
        [Fact]
        public async Task AnUnconfirmedAnnounceWhilePausedRestartedOrStoppedOffersNoForm()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(99));
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider => new UnconfirmedQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider))))
            });
            var admin = await ReviewerAsync(factory, "198.51.100.218");
            Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(admin, 99, "Approve", await OpenAsync(admin, 99))).StatusCode);
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var page = await OpenAsync(admin, 99);
            var key = AnnounceKey(page.Html);
            var address = await RequestSteps.NoticeOfAsync(await AnnounceAsync(admin, 99, page, key));
            Assert.Single(await db.SentAsync(), row => row.kind == RequestKinds.JobNotifications);

            // The notice's address reloaded a minute on, as the section's words stand in the form's place
            async Task AssertNotOfferedAsync(string section, string what)
            {
                factory.Clock.Advance(TimeSpan.FromMinutes(1));
                var answer = await admin.Client.GetAsync(address, Token);
                var html = await answer.Content.ReadAsStringAsync(Token);

                Assert.Equal(HttpStatusCode.ServiceUnavailable, answer.StatusCode);
                Assert.Contains(section, Text(html));
                Assert.Contains(RequestWords.UnsureNotOffered, Text(html));
                Assert.DoesNotContain(RequestWords.Unsure, Text(html));
                Assert.DoesNotContain("handler=Announce", html);
                PageTests.AssertAccessible(what, html);
            }

            await db.Requests().SavePause(true, "Testing the pause", now, TestUsers.Owner.Id, 1, now);
            await AssertNotOfferedAsync(ArmorWords.AnnouncePaused, "an Announce now the database didn't confirm, while posts are paused");

            await db.Requests().SavePause(false, null, now.AddMinutes(1), TestUsers.Owner.Id, 2, now.AddMinutes(1));
            var resumed = await (await admin.Client.GetAsync(address, Token)).Content.ReadAsStringAsync(Token);
            Assert.Equal(2, Regex.Count(Text(resumed), Regex.Escape(RequestWords.Unsure)));
            Assert.Equal(key, AnnounceKey(resumed));

            var bot = StatusSteps.Status(now.AddMinutes(2));
            bot.started_at = now.AddMinutes(1).AddSeconds(30);
            await OlderBotAsync(db, bot);
            await AssertNotOfferedAsync(ArmorWords.AnnounceRestarted(TimeWords.Utc(bot.started_at)), "an Announce now the database didn't confirm, once the bot has started again");

            bot.stopped_at = now.AddMinutes(2).AddSeconds(30);
            await OlderBotAsync(db, bot);
            await AssertNotOfferedAsync(ArmorWords.AnnounceStopped, "an Announce now the database didn't confirm, once the bot has stopped");
        }

        // Announce now refused for the armor itself, which wasn't approved when it was pressed, says why at the top of the page
        // alone: once the armor is approved, the notice's GET draws Announce now's form with no refusal beside it, since the
        // reason was the armor's, not the request's. Fails if such a refusal is drawn by the button too.
        [Fact]
        public async Task AnAnnounceRefusedForTheArmorSaysWhyAtTheTopAlone()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(96));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "198.51.100.147");

            var address = await RequestSteps.NoticeOfAsync(await AnnounceAsync(admin, 96, await OpenAsync(admin, 96), RequestFields.NewKey()));
            Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(admin, 96, "Approve", await OpenAsync(admin, 96))).StatusCode);

            var answer = await admin.Client.GetAsync(address, Token);
            var html = await answer.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
            Assert.Equal(1, Regex.Count(Text(html), Regex.Escape(ArmorWords.NotApprovedNow)));
            Assert.Contains("?handler=Announce\"", html);
            Assert.DoesNotContain("<div class=\"notice notice-problem\">", html);
            Assert.DoesNotContain(await db.SentAsync(), row => row.kind == RequestKinds.JobNotifications);
        }

        // In Announce now's place: while posts are paused, the bot posts the armor once they're resumed; while a run that
        // started before the approval is under way, it may post it, until the bot starts again or stops cleanly, either of
        // which ends that run, and then the bot's next run is its first since a start, which posts none of the approved armor
        // it finds, so the line says the bot won't post this armor if it hasn't already; with requests off, the next check does.
        // Fails if Announce now is offered while the bot would refuse it or couldn't post the armor, if a run the bot's restart
        // or clean stop ended reads as under way, or if the line for a run under way promises Announce now after such an end.
        [Fact]
        public async Task AnnounceNowIsReplacedWhilePausedRunningOrOff()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(81));
            await using (var factory = new DashboardFactory(db))
            {
                var admin = await ReviewerAsync(factory, "198.51.100.138");
                Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(admin, 81, "Approve", await OpenAsync(admin, 81))).StatusCode);
                var now = factory.Clock.GetUtcNow().UtcDateTime;

                await db.Requests().SavePause(true, "Testing the pause", now, TestUsers.Owner.Id, 1, now);
                var paused = await OpenAsync(admin, 81);
                Assert.Contains(ArmorWords.AnnouncePaused, paused.Text);
                Assert.DoesNotContain("handler=Announce", paused.Html);

                await db.ClearAsync(MariaDbFixture.SettingsTable);
                await RunAsync(db, now.AddSeconds(-20), now.AddMinutes(-5));
                var running = await OpenAsync(admin, 81);
                Assert.Contains(ArmorWords.AnnounceRunning(RequestWords.Clock(now.AddSeconds(-20))), running.Text);
                Assert.Contains("may post this armor, unless the bot restarts or stops first, which loses it.", running.Text);
                Assert.DoesNotContain("comes back", running.Text);
                Assert.DoesNotContain("handler=Announce", running.Html);
                PageTests.AssertAccessible("a review page while a notification run is under way", running.Html);

                // The same run once the bot has started again since it began, as the restart that cures a run the time limit
                // cut off does: it ended with the bot, whose first run since, Announce now's or the schedule's, takes this
                // armor as seen
                var restarted = StatusSteps.Status(now);
                restarted.started_at = now.AddSeconds(-10);
                await OlderBotAsync(db, restarted);
                var after = await OpenAsync(admin, 81);
                Assert.Contains(ArmorWords.AnnounceRestarted(TimeWords.Utc(now.AddSeconds(-10))), after.Text);
                Assert.DoesNotContain("handler=Announce", after.Html);
                Assert.DoesNotContain(ArmorWords.AnnounceRunning(RequestWords.Clock(now.AddSeconds(-20))), after.Text);

                // The same run once the bot has stopped cleanly since it began, and not started again, as docker stop or a deploy
                // whose new container doesn't come up leaves it: no job runs while the bot is stopped, and its first run once it
                // starts takes this armor as seen
                await db.ClearAsync(MariaDbFixture.StatusTable);
                var stopped = StatusSteps.Status(now.AddSeconds(-5));
                stopped.stopped_at = now.AddSeconds(-5);
                await OlderBotAsync(db, stopped);
                var whileStopped = await OpenAsync(admin, 81);
                Assert.Contains(ArmorWords.AnnounceStopped, whileStopped.Text);
                Assert.DoesNotContain("handler=Announce", whileStopped.Html);
                Assert.DoesNotContain(ArmorWords.AnnounceRunning(RequestWords.Clock(now.AddSeconds(-20))), whileStopped.Text);
                PageTests.AssertAccessible("a review page while the bot is stopped", whileStopped.Html);
            }

            await db.ClearAsync(MariaDbFixture.JobStatusTable, MariaDbFixture.StatusTable);
            await using var off = new DashboardFactory(db, new DashboardFactoryOptions { Settings = new Dictionary<string, string?> { ["Dashboard:RequestSigningKey"] = "" } });
            var offAdmin = await ReviewerAsync(off, "198.51.100.139");
            var page = await OpenAsync(offAdmin, 81);

            Assert.Contains(ArmorWords.AnnounceOff, page.Text);
            Assert.DoesNotContain("handler=Announce", page.Html);
        }

        // A run the bot skipped never ran: the bot records one when a run comes due while an earlier one still runs, as a run
        // the time limit cut off does 30 seconds on, and from then on the skip is the job's last run in the status row. One
        // in the bot's current run withholds Announce now, as the cut-off run would; one from before the restart that cures
        // that run, or from before a clean stop the bot hasn't started again since, tells only of a run that ended with the
        // bot, so it's no run since the approval, and the bot's next run is its first since a start, which takes this armor
        // as seen, as the page then says. Fails if a skip from before the bot's current run, or from before its clean stop,
        // counts as a run that has seen the approval.
        [Fact]
        public async Task ASkippedRunFromBeforeARestartIsNoRunSinceTheApproval()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(87));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "198.51.100.127");
            Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(admin, 87, "Approve", await OpenAsync(admin, 87))).StatusCode);
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            await db.Status().SaveJobStatus(
            [
                new JobStatus
                {
                    job = NotificationJob,
                    schedule = "*/5 * * * *",
                    next_run_at = now.AddMinutes(5),
                    last_started_at = now.AddSeconds(30),
                    last_ended_at = now.AddSeconds(30),
                    last_result = JobResult.Skipped,
                    last_detail = "the run that was cut off still runs",
                    updated_at = now.AddSeconds(30)
                }
            ]);
            var bot = StatusSteps.Status(now.AddSeconds(40));
            await OlderBotAsync(db, bot);
            Assert.DoesNotContain("id=\"announce\"", (await OpenAsync(admin, 87)).Html);

            bot.started_at = now.AddSeconds(60);
            await OlderBotAsync(db, bot);
            var restarted = await OpenAsync(admin, 87);
            Assert.Contains(ArmorWords.AnnounceRestarted(TimeWords.Utc(now.AddSeconds(60))), restarted.Text);
            Assert.DoesNotContain("handler=Announce", restarted.Html);

            // The skip's own run once it has stopped cleanly after it, and not started again, as docker stop leaves it: the
            // skip ended with the bot too, so the page says what the bot's first run once it starts will do with this armor
            await db.ClearAsync(MariaDbFixture.StatusTable);
            var stopped = StatusSteps.Status(now.AddSeconds(50));
            stopped.stopped_at = now.AddSeconds(50);
            await OlderBotAsync(db, stopped);
            var whileStopped = await OpenAsync(admin, 87);
            Assert.Contains(ArmorWords.AnnounceStopped, whileStopped.Text);
            Assert.DoesNotContain("handler=Announce", whileStopped.Html);
        }

        // The bot keeps what it has seen only in memory, so its first notification run after a start takes every approved
        // armor it finds as seen and posts none of it, whether it's the schedule's or Announce now's. Armor approved after the
        // bot's last run is offered Announce now while the bot runs on; once the bot has started again with no run since,
        // posts paused or not, the page says the bot won't post it instead, as it does when no run is recorded at all; and the
        // first run since the start takes it away. Fails if Announce now is offered for armor that run would take as seen.
        [Fact]
        public async Task AnnounceNowIsNotOfferedOnceTheBotHasStartedSinceItsLastRun()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(89));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "198.51.100.128");
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var bot = StatusSteps.Status(now);
            await OlderBotAsync(db, bot);
            await RunAsync(db, now.AddMinutes(-1), now.AddMinutes(-1).AddSeconds(3));

            Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(admin, 89, "Approve", await OpenAsync(admin, 89))).StatusCode);
            var runningOn = await OpenAsync(admin, 89);
            Assert.Contains("?handler=Announce\"", runningOn.Html);
            Assert.Contains(ArmorWords.AnnounceHelp, runningOn.Text);

            // Started again after the approval, as a restart from the controls page or a deploy does, and no run since
            factory.Clock.Advance(TimeSpan.FromSeconds(30));
            var start = factory.Clock.GetUtcNow().UtcDateTime;
            bot.run_id = 7;
            bot.started_at = start;
            bot.heartbeat_at = start.AddSeconds(20);
            await OlderBotAsync(db, bot);
            factory.Clock.Advance(TimeSpan.FromSeconds(25));
            var restarted = await OpenAsync(admin, 89);
            Assert.Contains(ArmorWords.AnnounceRestarted(TimeWords.Utc(start)), restarted.Text);
            Assert.DoesNotContain("handler=Announce", restarted.Html);
            Assert.DoesNotContain(ArmorWords.AnnounceHelp, restarted.Text);
            PageTests.AssertAccessible("a review page after the bot started again", restarted.Html);

            // Paused since: the first run still takes the armor as seen, so the pause's line, which says it goes out at the
            // resume, would be wrong
            await db.Requests().SavePause(true, "Testing the pause", start, TestUsers.Owner.Id, 1, start);
            var paused = await OpenAsync(admin, 89);
            Assert.Contains(ArmorWords.AnnounceRestarted(TimeWords.Utc(start)), paused.Text);
            Assert.DoesNotContain(ArmorWords.AnnouncePaused, paused.Text);
            await db.ClearAsync(MariaDbFixture.SettingsTable);

            // No run recorded at all, as before the bot's first run ever
            await db.ClearAsync(MariaDbFixture.JobStatusTable);
            Assert.Contains(ArmorWords.AnnounceRestarted(TimeWords.Utc(start)), (await OpenAsync(admin, 89)).Text);

            // The first run since the start, which took the armor as seen
            var ran = factory.Clock.GetUtcNow().UtcDateTime;
            await RunAsync(db, ran, ran.AddSeconds(2));
            factory.Clock.Advance(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain("id=\"announce\"", (await OpenAsync(admin, 89)).Html);
        }

        // The same rule in the words beside the decision: while the bot's next check is its first since it started, as after a
        // restart or while it's stopped, Approve's help, the approval's notice and the preview's note don't promise the post
        // that the line in Announce now's place says won't come. Fails if one of them promises it whatever that check is.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AnApprovalsWordsDontPromiseAPostAtTheBotsFirstCheck(bool stopped)
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(90));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, stopped ? "198.51.100.141" : "198.51.100.142");
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var bot = StatusSteps.Status(now);
            bot.started_at = now.AddMinutes(-2);
            bot.stopped_at = stopped ? now.AddMinutes(-1) : null;
            await OlderBotAsync(db, bot);
            await RunAsync(db, now.AddMinutes(-4), now.AddMinutes(-4).AddSeconds(2));
            const string Hedge = "if that's the bot's first check since it started, it won't post this one.";

            var pending = await OpenAsync(admin, 90);
            Assert.Matches($"<p id=\"approve-help\"[^<]*{Regex.Escape(Hedge)}", WebUtility.HtmlDecode(pending.Html));
            Assert.Matches($"<h3>The new-armor post</h3>\\s*<p[^<]*{Regex.Escape(Hedge)}", WebUtility.HtmlDecode(pending.Html));

            var response = await PostAsync(admin, 90, "Approve", pending);
            var approved = WebUtility.HtmlDecode(await admin.Client.GetStringAsync(SignInSteps.PathOf(response.Headers.Location), Token));
            Assert.Matches($"<div class=\"notice notice-done\"[^>]*>\\s*<p>Approved[.][^<]*{Regex.Escape(Hedge)}", approved);
            Assert.Matches($"<h3>The new-armor post</h3>\\s*<p[^<]*{Regex.Escape(Hedge)}", approved);
            Assert.Contains(stopped ? ArmorWords.AnnounceStopped : ArmorWords.AnnounceRestarted(TimeWords.Utc(now.AddMinutes(-2))), Text(approved));
        }

        // The status row keeps only the job's last run, and a run only once it has ended: armor an earlier run posted can have
        // a skip from before the bot's start, or before its stop, as the last, and armor approved while the first run since a
        // start is under way, after its read, is posted by the next. Fails if the lines for a start and a stop say the bot
        // won't post armor it may have posted, or that it hasn't run, which the row can't tell.
        [Fact]
        public async Task TheLinesForAStartAndAStopClaimOnlyWhatTheStatusRowShows()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(92));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "198.51.100.143");
            Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(admin, 92, "Approve", await OpenAsync(admin, 92))).StatusCode);
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            // Posted by the run after the approval, and 3 days on the last run is a skip, then the bot starts again
            await RunAsync(db, now.AddMinutes(1), now.AddMinutes(1).AddSeconds(2));
            Assert.DoesNotContain("id=\"announce\"", (await OpenAsync(admin, 92)).Html);
            var skipped = now.AddDays(3);
            await db.Status().SaveJobStatus(
            [
                new JobStatus
                {
                    job = NotificationJob,
                    schedule = "*/5 * * * *",
                    next_run_at = skipped.AddMinutes(5),
                    last_started_at = skipped,
                    last_ended_at = skipped,
                    last_result = JobResult.Skipped,
                    updated_at = skipped
                }
            ]);
            var bot = StatusSteps.Status(skipped.AddMinutes(1));
            bot.started_at = skipped.AddMinutes(1);
            await OlderBotAsync(db, bot);
            var restarted = await OpenAsync(admin, 92);
            Assert.Contains("if the bot hasn't posted this armor yet, it won't, unless the armor was approved during that run.", restarted.Text);
            Assert.Contains("and no notification run since is recorded yet.", restarted.Text);
            Assert.DoesNotContain("so the bot won't post this armor", restarted.Text);

            await db.ClearAsync(MariaDbFixture.StatusTable);
            var stop = StatusSteps.Status(skipped.AddMinutes(1));
            stop.stopped_at = skipped.AddMinutes(1);
            await OlderBotAsync(db, stop);
            var stopped = await OpenAsync(admin, 92);
            Assert.Contains("if the bot hasn't posted this armor yet, it won't. Announce now isn't offered.", stopped.Text);
            Assert.DoesNotContain("so the bot won't post this armor", stopped.Text);
        }

        // The bot refuses Announce now while posts are paused or a run is under way, but not as its first run since a start,
        // which takes the armor as seen and is answered Done, so the server refuses the form that a page drew before the bot
        // started again or stopped. Fails if that form still sends the request.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AnAnnounceFormDrawnBeforeARestartOrAStopSendsNothing(bool stopped)
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(94));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, stopped ? "198.51.100.144" : "198.51.100.145");
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var bot = StatusSteps.Status(now);
            await OlderBotAsync(db, bot);
            await RunAsync(db, now.AddMinutes(-1), now.AddMinutes(-1).AddSeconds(3));
            Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(admin, 94, "Approve", await OpenAsync(admin, 94))).StatusCode);
            var drawn = await OpenAsync(admin, 94);
            var key = AnnounceKey(drawn.Html);

            factory.Clock.Advance(TimeSpan.FromSeconds(40));
            var changed = factory.Clock.GetUtcNow().UtcDateTime;
            bot.heartbeat_at = changed;

            if (stopped)
                bot.stopped_at = changed;
            else
                (bot.run_id, bot.started_at) = (7, changed);

            await OlderBotAsync(db, bot);

            var refused = await RequestSteps.AnsweredAsync(admin, await AnnounceAsync(admin, 94, drawn, key));
            var html = await refused.Content.ReadAsStringAsync(Token);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.DoesNotContain(await db.SentAsync(), row => row.kind == RequestKinds.JobNotifications);
            Assert.Contains("so nothing was sent.", Text(html));
            Assert.Contains(stopped ? ArmorWords.AnnounceStopped : ArmorWords.AnnounceRestarted(TimeWords.Utc(changed)), Text(html));
            Assert.DoesNotContain("handler=Announce", html);
            PageTests.AssertAccessible("a review page after an Announce now the bot couldn't post", html);
        }

        // Announce now refused because the bot started again since the page was drawn: the notice's GET points to the reason
        // below, and once the bot's first notification run since has started, Announce now has no place on the page, so a
        // reload of the same address draws no reason below, and the words say why themselves. Fails if they point to a
        // reason the page doesn't draw.
        [Fact]
        public async Task AnAnnounceRefusedAfterARestartSaysWhyOnceARunHasStarted()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(98));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "198.51.100.217");
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var bot = StatusSteps.Status(now);
            await OlderBotAsync(db, bot);
            await RunAsync(db, now.AddMinutes(-1), now.AddMinutes(-1).AddSeconds(3));
            Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(admin, 98, "Approve", await OpenAsync(admin, 98))).StatusCode);
            var drawn = await OpenAsync(admin, 98);

            factory.Clock.Advance(TimeSpan.FromSeconds(40));
            var started = factory.Clock.GetUtcNow().UtcDateTime;
            (bot.run_id, bot.started_at, bot.heartbeat_at) = (7, started, started);
            await OlderBotAsync(db, bot);

            var address = await RequestSteps.NoticeOfAsync(await AnnounceAsync(admin, 98, drawn, AnnounceKey(drawn.Html)));
            var refused = Text(await (await admin.Client.GetAsync(address, Token)).Content.ReadAsStringAsync(Token));
            Assert.Contains(ArmorWords.AnnounceNotNow, refused);
            Assert.Contains(ArmorWords.AnnounceRestarted(TimeWords.Utc(started)), refused);

            await RunAsync(db, started.AddMinutes(1), started.AddMinutes(1).AddSeconds(3));
            factory.Clock.Advance(TimeSpan.FromMinutes(3));
            var later = await admin.Client.GetAsync(address, Token);
            var html = await later.Content.ReadAsStringAsync(Token);
            var text = Text(html);

            Assert.Equal(HttpStatusCode.Conflict, later.StatusCode);
            Assert.Contains(ArmorWords.AnnounceWasNotOffered, text);
            Assert.DoesNotContain("for the reason below", text);
            Assert.DoesNotContain("handler=Announce", html);
            Assert.DoesNotContain(await db.SentAsync(), row => row.kind == RequestKinds.JobNotifications);
            PageTests.AssertAccessible("an Announce now refused after a restart, once a notification run has started since", html);
        }

        // Check again, the next look at the cache clear's box without the script, loads the review page each time it's pressed,
        // keeping the action's notice and the list the reviewer came from: from the address the approval's redirect leads to,
        // and from the one Check again leads to, whose own leads back. Fails if it's the address the browser shows with a
        // fragment, which only scrolls, or loses the list.
        [Fact]
        public async Task CheckAgainLoadsThePageAgain()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(85, ArmorStatus.Pending, "Frost Knight"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "198.51.100.148");

            var page = await admin.Client.GetStringAsync("/admin/armor/85?list=pending&q=frost", Token);
            var form = Read(page);
            var action = WebUtility.HtmlDecode(Regex.Match(page, "action=\"([^\"]*handler=Approve[^\"]*)\"").Groups[1].Value);
            var approved = await CraftedAsync(admin, action, form.Token, ("expected", form.Expected), ("version", form.Version));

            var clear = Assert.Single(await ClearsAsync(db));
            Assert.Equal($"/admin/armor/85?done=approved&request={clear.id}&list=pending&q=frost#result", SignInSteps.PathOf(approved.Headers.Location));
            await RequestSteps.CheckAgainWalkAsync(admin, approved);
        }

        // ?request= draws a box only for the page's own kinds, the cache clear and the notification job, and the line about
        // the cache only beside the notice of an action that clears it. Anything else in the address shows nothing.
        [Fact]
        public async Task TheBoxAndTheLineShowOnlyWhereTheyFit()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(82, ArmorStatus.Approved, "Test Armor 82"), Submission(83, ArmorStatus.Pending, "Test Armor 83"),
                Submission(84, ArmorStatus.NotApproved, "Test Armor 84"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "198.51.100.140");
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var clear = await RequestSteps.AddAsync(db, factory, RequestKinds.CachesClear, now, TestUsers.Admin);
            var restart = await RequestSteps.AddAsync(db, factory, RequestKinds.BotRestart, now);

            var shown = await admin.Client.GetStringAsync($"/admin/armor/82?done=approved&request={clear}", Token);
            Assert.Contains($"Clear caches, request {clear}", Text(shown));
            Assert.Contains($"<a href=\"/admin/armor/82?done=approved&amp;request={clear}&amp;again=1#result\">Check again</a>", shown);

            // Check again keeps the action's word and the list the reviewer came from, its search encoded, and takes again
            // off once the page has it
            var listed = await admin.Client.GetStringAsync($"/admin/armor/82?done=approved&request={clear}&list=pending&q=frost%20bite", Token);
            Assert.Contains($"<a href=\"/admin/armor/82?done=approved&amp;request={clear}&amp;list=pending&amp;q=frost%20bite&amp;again=1#result\">Check again</a>", listed);
            var again = await admin.Client.GetStringAsync($"/admin/armor/82?done=approved&request={clear}&list=pending&q=frost%20bite&again=1", Token);
            Assert.Contains($"<a href=\"/admin/armor/82?done=approved&amp;request={clear}&amp;list=pending&amp;q=frost%20bite#result\">Check again</a>", again);

            foreach (var path in new[] { $"/admin/armor/82?request={restart}", $"/admin/armor/82?request={clear}x", "/admin/armor/82?request=0", "/admin/armor/82?request=99999" })
                Assert.DoesNotContain("id=\"result\"", await admin.Client.GetStringAsync(path, Token));

            // The line only beside the notice, and only for a word it knows
            Assert.Contains(ArmorWords.CacheNotCleared(ArmorWords.UnsentOff)!, Text(await admin.Client.GetStringAsync("/admin/armor/82?done=approved&unsent=off", Token)));

            foreach (var path in new[] { "/admin/armor/82?done=approved&unsent=bogus", "/admin/armor/83?done=approved&unsent=off", "/admin/armor/82?unsent=off" })
                Assert.DoesNotContain("armor-name cache", Text(await admin.Client.GetStringAsync(path, Token)));

            // Nor beside the notice of an action that clears nothing, such as a rejection
            var rejected = Text(await admin.Client.GetStringAsync("/admin/armor/84?done=rejected&unsent=off", Token));
            Assert.Contains(ArmorWords.Rejected, rejected);
            Assert.DoesNotContain("armor-name cache", rejected);
        }

        // The real repository, whose transactions commit and then time out, as a commit whose answer never came
        sealed class LateSubmissions(ArmorSubmissionData real) : IArmorSubmissionData
        {
            public async Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work)
            {
                await real.RunInTransaction(work);
                throw new TimeoutException("The database didn't answer within 15 seconds");
            }

            public Task<bool> ChangeStatus(long id, string? expectedStatus, string newStatus, DataTransaction? transaction = null) => real.ChangeStatus(id, expectedStatus, newStatus, transaction);
            public Task<ArmorSubmission?> GetSubmission(long id, DataTransaction? transaction = null) => real.GetSubmission(id, transaction);
            public Task<bool> IsApprovedNameTaken(string name, long exceptId, DataTransaction? transaction = null) => real.IsApprovedNameTaken(name, exceptId, transaction);
            public Task<Page<ArmorSubmission>> ListByStatus(string? status, int page, int pageSize) => real.ListByStatus(status, page, pageSize);
            public Task<Page<ArmorSubmission>> Search(string text, string? status, int page, int pageSize) => real.Search(text, status, page, pageSize);
        }

        [GeneratedRegex("""action="/admin/armor/[0-9]+\?handler=Announce[^"]*"[^>]*>\s*<input type="hidden" name="formkey" value="([0-9a-f]{32})" />""")]
        private static partial Regex AnnounceForm();
    }
}
