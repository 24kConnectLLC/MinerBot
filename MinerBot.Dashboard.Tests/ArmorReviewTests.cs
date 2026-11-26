using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MinerBot.Dashboard.Armor;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Armor;
using MinerBot.Shared.Delivery;
using static MinerBot.Dashboard.Tests.Support.ArmorSteps;

namespace MinerBot.Dashboard.Tests
{
    // The review page beside a bot that keeps the record of its posts: where an armor was posted, from that record, and Back
    // to pending from Approved only before the post, checked again in the review's transaction. A read of the record that
    // fails costs that part alone, never the other actions.
    [Collection("Database")]
    public partial class ArmorReviewTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        // Servers in the bot's copy, and one it doesn't have
        const ulong Posted = 1000000000000000901, Waits = 1000000000000000902, Dropped = 1000000000000000903, Unknown = 1000000000000000904,
            Checked = 1000000000000000905, Unconfirmed = 1000000000000000906;

        // A page's text without its markup, so a sentence reads as one string
        static string Text(string html) => Regex.Replace(Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(html), "<[^>]+>", " "), "\\s+", " "), " ([.,:])(?= |$)", "$1");

        // A bot that keeps the record of its posts, up now, and its copy of the servers but Unknown
        static async Task<DateTime> StartAsync(MariaDbFixture db, DashboardFactory factory)
        {
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            await StatusSteps.StatusAsync(db, now);

            foreach (var (server, name) in new[] { (Posted, "Post Server"), (Waits, "Wait Server"), (Dropped, "Drop Server"), (Checked, "Check Server"), (Unconfirmed, "Doubt Server") })
                await db.BotGuilds().SaveGuild(StatusSteps.Server(server, name), [StatusSteps.Channel(server % 1000, "armor", 0)], now, StatusSteps.RunId, (uint)(server % 1000));

            return now;
        }

        // Where it was posted, as text, from its heading to the next section's, or nothing when the page has none
        static string Section(string html) => html.IndexOf("<h2 id=\"posted\">", StringComparison.Ordinal) is var start and >= 0
            ? Text(html[start..html.IndexOf("<section aria-labelledby=\"submission\">", StringComparison.Ordinal)]).Trim()
            : "";

        // Each server the armor was owed to, in the record's order, with what became of it there: posted with a link built
        // from the three numbers, waiting with its next try, not posted and why, being checked, and may not have arrived; a
        // server the copy doesn't have by its ID. Back to pending isn't offered, and the page says how many servers have it.
        [Fact]
        public async Task WhereItWasPostedListsTheServersWithLinks()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(201, ArmorStatus.Approved, "Test Armor 201"));
            await using var factory = new DashboardFactory(db);
            var now = await StartAsync(db, factory);

            var rows = await PostSteps.QueueAsync(db, PostSource.Armor, 201, now.AddHours(-1), Posted, Waits, Dropped, Unknown, Checked, Unconfirmed);
            await PostSteps.SentAsync(db, [rows[0]], 901, now.AddMinutes(-50), 5000000000000000201);
            await PostSteps.FailedAsync(db, [rows[1]], 902, now.AddMinutes(-45), DeliveryReasons.DiscordPermissions, 403, 50013, now.AddMinutes(20));
            await PostSteps.DroppedAsync(db, [rows[2]], DeliveryReasons.SwitchedOff, now.AddMinutes(-40));
            await PostSteps.SentAsync(db, [rows[3]], 904, now.AddMinutes(-35), 5000000000000000204);
            await PostSteps.UnsettledAsync(db, [rows[4]], 905, now.AddMinutes(-30), DeliveryState.InDoubt);
            await PostSteps.UnsettledAsync(db, [rows[5]], 906, now.AddMinutes(-25), DeliveryState.Unconfirmed);

            var admin = await ReviewerAsync(factory, "198.51.100.230");
            var page = await OpenAsync(admin, 201);
            var section = Section(page.Html);
            PageTests.AssertAccessible("a review page with where the armor was posted", page.Html);

            Assert.Contains(ArmorWords.PostsIntro, section);
            Assert.Equal(
            [
                $"Post Server: {ArmorWords.PostedAt(now.AddMinutes(-50))} Open it in Discord",
                $"Wait Server: {ArmorWords.WaitingUntil(now.AddMinutes(20))}",
                $"Drop Server: not posted: its kind was switched off on the dashboard",
                $"Server {Unknown}: {ArmorWords.PostedAt(now.AddMinutes(-35))} Open it in Discord",
                $"Check Server: {ArmorWords.BeingChecked}",
                $"Doubt Server: {ArmorWords.MayNotHaveArrived}"
            ], Regex.Matches(page.Html[page.Html.IndexOf("id=\"posted\"", StringComparison.Ordinal)..], "<li>(.*?)</li>", RegexOptions.Singleline)
                .Take(6).Select(match => Text(match.Groups[1].Value).Trim()));

            Assert.Contains("<a href=\"https://discord.com/channels/1000000000000000901/901/5000000000000000201\">Open it in Discord</a>", page.Html);
            Assert.Contains("<a href=\"https://discord.com/channels/1000000000000000904/904/5000000000000000204\">Open it in Discord</a>", page.Html);
            Assert.Equal(2, Regex.Count(page.Html, "href=\"https://discord\\.com/channels/"));

            Assert.DoesNotContain("handler=BackToPending", page.Html);
            Assert.Contains(ArmorWords.PostedIn(4), page.Text);
            Assert.Contains("handler=TakeDown", page.Html);
            Assert.Contains(ArmorWords.TakeDownHelpKept, page.Text);
            Assert.DoesNotContain("id=\"announce\"", page.Html);
        }

        // Armor that was approved when the bot's record began is taken as posted: the page says so, with no servers, and Back
        // to pending isn't offered
        [Fact]
        public async Task BaselineArmorSaysItCountsAsPosted()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(202, ArmorStatus.Approved, "Test Armor 202"));
            await using var factory = new DashboardFactory(db);
            var now = await StartAsync(db, factory);
            await PostSteps.BaselineAsync(db, 202, now.AddDays(-2));

            var admin = await ReviewerAsync(factory, "198.51.100.231");
            var page = await OpenAsync(admin, 202);

            Assert.Equal("Where it was posted " + ArmorWords.Baseline(now.AddDays(-2)), Section(page.Html));
            Assert.Contains($"MinerBot started its record of posts on {now.AddDays(-2):yyyy-MM-dd} UTC, when this armor was already approved, so it counts it as posted and doesn't post it again.",
                page.Text);
            Assert.DoesNotContain("handler=BackToPending", page.Html);
            Assert.Contains(ArmorWords.CountedAsPosted, page.Text);
            Assert.DoesNotContain("id=\"announce\"", page.Html);
        }

        // Back to pending from Approved is offered while the record shows no post: before the bot found the armor, and while
        // it waits; not once a message has it, sent or being sent. From Not Approved it's offered whatever the record says, as
        // that undoes a take-down, not a post.
        [Fact]
        public async Task BackToPendingIsOnlyOfferedBeforeThePost()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(203, ArmorStatus.Pending, "Test Armor 203"), Submission(204, ArmorStatus.NotApproved, "Test Armor 204"));
            await using var factory = new DashboardFactory(db);
            var now = await StartAsync(db, factory);
            var admin = await ReviewerAsync(factory, "198.51.100.232");
            Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(admin, 203, "Approve", await OpenAsync(admin, 203))).StatusCode);

            var before = await OpenAsync(admin, 203);
            Assert.Contains("handler=BackToPending", before.Html);
            Assert.Contains(ArmorWords.BackToPendingFromApprovedHelpKept, before.Text);
            Assert.Contains(ArmorWords.NotPostedYet, Section(before.Html));
            Assert.Contains("handler=Announce", before.Html);

            var rows = await PostSteps.QueueAsync(db, PostSource.Armor, 203, now, Posted, Waits);
            var queued = await OpenAsync(admin, 203);
            Assert.Contains("handler=BackToPending", queued.Html);
            Assert.Contains($"Post Server: {ArmorWords.Waiting}", Section(queued.Html));
            Assert.DoesNotContain("handler=Announce", queued.Html);

            await PostSteps.ClaimAsync(db, [rows[0]], 901, now.AddMinutes(1));
            var sending = await OpenAsync(admin, 203);
            Assert.DoesNotContain("handler=BackToPending", sending.Html);
            Assert.Contains(ArmorWords.PostedIn(1), sending.Text);

            await PostSteps.SentAsync(db, [rows[1]], 902, now.AddMinutes(2), 5000000000000000203);
            Assert.Contains(ArmorWords.PostedIn(2), (await OpenAsync(admin, 203)).Text);

            // Taken down after its post: moving it back undoes the take-down
            await PostSteps.SentAsync(db, await PostSteps.QueueAsync(db, PostSource.Armor, 204, now, Posted), 901, now.AddMinutes(3), 5000000000000000204);
            var takenDown = await OpenAsync(admin, 204);
            Assert.Contains("handler=BackToPending", takenDown.Html);
            Assert.Contains(ArmorWords.BackToPendingHelp, takenDown.Text);
        }

        // A page drawn before the bot posted the armor still has Back to pending, but the POST reads the record again inside
        // the review's transaction: once it shows a post, the move is refused with a refused audit row, the armor stays
        // approved, and the page says why. Drawn while the bot was older, the POST is refused the same way.
        [Fact]
        public async Task BackToPendingPostAfterAPostIsRefused()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(205, ArmorStatus.Approved, "Test Armor 205"));
            await using var factory = new DashboardFactory(db);
            var now = await StartAsync(db, factory);
            var admin = await ReviewerAsync(factory, "198.51.100.233");

            var drawn = await OpenAsync(admin, 205);
            Assert.Contains("handler=BackToPending", drawn.Html);

            await PostSteps.SentAsync(db, await PostSteps.QueueAsync(db, PostSource.Armor, 205, now, Posted), 901, now.AddMinutes(1), 5000000000000000205);

            var answer = await RequestSteps.AnsweredAsync(admin, await PostAsync(admin, 205, "BackToPending", drawn));
            var html = await answer.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
            Assert.Contains(ArmorWords.WasPosted, Text(html));
            Assert.Equal(ArmorStatus.Approved, await db.StatusAsync(205));
            Assert.Empty(await db.ReviewsAsync());

            var refused = Assert.Single(await db.AuditAsync(), entry => entry.action == AuditWords.ArmorPending);
            Assert.Equal((AuditOutcome.Refused, "The armor had been posted, so moving it back to pending was refused", "{\"Status\":\"Approved\"}"),
                (refused.outcome, refused.detail, refused.before_json));
            Assert.Empty(await db.SentAsync());
        }

        // The night's clean-up keeps an armor's queue rows, which say where it was posted, though the records of its messages go
        // after 90 days: an armor posted in two servers 91 days ago still lists both as posted, now without their links, and
        // Back to pending from Approved is neither offered nor let through
        [Fact]
        public async Task PostedArmorStaysPostedAfterTheCleanUp()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(209, ArmorStatus.Approved, "Test Armor 209"));
            await using var factory = new DashboardFactory(db);
            var now = await StartAsync(db, factory);

            var at = now.AddDays(-91);
            var rows = await PostSteps.QueueAsync(db, PostSource.Armor, 209, at, Posted, Waits);
            await PostSteps.SentAsync(db, [rows[0]], 901, at, 5000000000000000209);
            await PostSteps.SentAsync(db, [rows[1]], 902, at, 5000000000000000210);

            // PostingRetentionJob's rules, with its cutoff: the start of the day 90 days before today
            var cutoff = now.Date.AddDays(-90);
            Assert.Equal(2, await db.PostingRetention().DeleteDeliveries(cutoff, PostingRetentionData.ChunkLimit));
            await db.PostingRetention().DeleteSettledQueueRows(cutoff, PostingRetentionData.ChunkLimit);

            var admin = await ReviewerAsync(factory, "198.51.100.235");
            var page = await OpenAsync(admin, 209);

            Assert.Contains(ArmorWords.PostsIntro, Section(page.Html));
            Assert.Equal([$"Post Server: {ArmorWords.PostedAt(at)}", $"Wait Server: {ArmorWords.PostedAt(at)}"],
                Regex.Matches(page.Html[page.Html.IndexOf("id=\"posted\"", StringComparison.Ordinal)..], "<li>(.*?)</li>", RegexOptions.Singleline)
                    .Take(2).Select(match => Text(match.Groups[1].Value).Trim()));
            Assert.DoesNotContain("href=\"https://discord.com/channels/", page.Html);
            Assert.DoesNotContain("handler=BackToPending", page.Html);
            Assert.Contains(ArmorWords.PostedIn(2), page.Text);

            var answer = await RequestSteps.AnsweredAsync(admin, await PostAsync(admin, 209, "BackToPending", page));
            Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
            Assert.Contains(ArmorWords.WasPosted, Text(await answer.Content.ReadAsStringAsync(Token)));
            Assert.Equal(ArmorStatus.Approved, await db.StatusAsync(209));
        }

        // A post that may not have arrived still says so after the clean-up: its row keeps why once the record of its message
        // is gone, so the page never shows it as posted at the time the bot stopped looking for it
        [Fact]
        public async Task PostThatMayNotHaveArrivedStaysSoAfterTheCleanUp()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(210, ArmorStatus.Approved, "Test Armor 210"));
            await using var factory = new DashboardFactory(db);
            var now = await StartAsync(db, factory);

            // Discord didn't answer the second server's post, and for an hour the bot couldn't read the channel to look for it
            var at = now.AddDays(-91);
            var rows = await PostSteps.QueueAsync(db, PostSource.Armor, 210, at, Posted, Unconfirmed);
            await PostSteps.SentAsync(db, [rows[0]], 901, at, 5000000000000000210);
            var key = await PostSteps.ClaimAsync(db, [rows[1]], 906, at);
            Assert.True(await db.Notifications().SettleAsync(new Settlement { AttemptKey = key, Outcome = DeliveryState.InDoubt, At = at.AddSeconds(15), Reason = DeliveryReasons.NoAnswer }));
            Assert.True(await db.Notifications().SettleAsync(new Settlement { AttemptKey = key, Outcome = DeliveryState.Unconfirmed, At = at.AddHours(1) }));

            var cutoff = now.Date.AddDays(-90);
            Assert.Equal(2, await db.PostingRetention().DeleteDeliveries(cutoff, PostingRetentionData.ChunkLimit));
            await db.PostingRetention().DeleteSettledQueueRows(cutoff, PostingRetentionData.ChunkLimit);

            var admin = await ReviewerAsync(factory, "198.51.100.236");
            var page = await OpenAsync(admin, 210);

            Assert.Equal([$"Post Server: {ArmorWords.PostedAt(at)}", $"Doubt Server: {ArmorWords.MayNotHaveArrived}"],
                Regex.Matches(page.Html[page.Html.IndexOf("id=\"posted\"", StringComparison.Ordinal)..], "<li>(.*?)</li>", RegexOptions.Singleline)
                    .Take(2).Select(match => Text(match.Groups[1].Value).Trim()));
            Assert.Contains(ArmorWords.PostedIn(2), page.Text);
        }

        // The record can't be read, as with a missing right: the page still opens, says it can't tell whether the armor was
        // posted, and offers no Back to pending from Approved, while Approve, Reject and Take down work
        [Fact]
        public async Task DeliveryReadFailureKeepsApprovalsWorking()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddAsync(Submission(206, ArmorStatus.Approved, "Test Armor 206"), Submission(207, ArmorStatus.Pending, "Test Armor 207"),
                Submission(208, ArmorStatus.Pending, "Test Armor 208"));
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IDeliveryReads>(_ => new FailingDeliveries()))
            });
            await StartAsync(db, factory);
            var admin = await ReviewerAsync(factory, "198.51.100.234");

            var approved = await OpenAsync(admin, 206);
            Assert.Contains(ArmorWords.PostsUnknown, Section(approved.Html));
            Assert.DoesNotContain("handler=BackToPending", approved.Html);
            Assert.Contains(ArmorWords.PostsUnknownNoBack, approved.Text);
            Assert.DoesNotContain("id=\"announce\"", approved.Html);
            PageTests.AssertAccessible("a review page whose record can't be read", approved.Html);

            Assert.Equal("/admin/armor/207?done=approved", SignInSteps.PathOf((await PostAsync(admin, 207, "Approve", await OpenAsync(admin, 207))).Headers.Location)?.Split('&')[0]);
            Assert.Equal(ArmorStatus.Approved, await db.StatusAsync(207));
            Assert.Equal("/admin/armor/208?done=rejected", SignInSteps.PathOf((await PostAsync(admin, 208, "Reject", await OpenAsync(admin, 208))).Headers.Location));
            Assert.StartsWith("/admin/armor/206?done=taken-down", SignInSteps.PathOf((await PostAsync(admin, 206, "TakeDown", approved)).Headers.Location));
            Assert.Equal(ArmorStatus.NotApproved, await db.StatusAsync(206));
        }
    }
}
