using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary;
using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Tests;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MinerBot.Dashboard.Armor;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Armor;
using static MinerBot.Dashboard.Tests.Support.ArmorSteps;

namespace MinerBot.Dashboard.Tests
{
    // Approve, Reject, Take down and Back to pending: each one transaction that changes the status only while the armor is
    // as the reviewer saw it, with its armor_reviews row and its audit row, or a refusal that writes only its audit row
    [Collection("Database")]
    public class ArmorActionTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static void Redirected(HttpResponseMessage response, long id, string done)
        {
            Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"The action answered {response.StatusCode}");
            Assert.Equal($"/admin/armor/{id}?done={done}", SignInSteps.PathOf(response.Headers.Location));
        }

        static async Task Refused(HttpResponseMessage response, HttpStatusCode status, string message)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Contains(message, await TextAsync(response));
        }

        static async Task<List<AuditEntry>> ArmorAuditAsync(MariaDbFixture db) =>
            (await db.AuditAsync()).Where(row => row.action.StartsWith("armor.", StringComparison.Ordinal)).ToList();

        [Fact]
        public async Task ApproveChangesTheStatusAndWritesItsReviewAndAuditRows()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(31));
            await using var factory = new DashboardFactory(db);
            var owner = await ReviewerAsync(factory, "203.0.113.140", TestUsers.Owner);
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var response = await PostAsync(owner, 31, "Approve", await OpenAsync(owner, 31));

            Redirected(response, 31, "approved");
            Assert.Equal(ArmorStatus.Approved, await db.StatusAsync(31));

            var review = Assert.Single(await db.ReviewsAsync());
            Assert.Equal(31, review.armor_id);
            Assert.Equal(ArmorStatus.Pending, review.from_status);
            Assert.Equal(ArmorStatus.Approved, review.to_status);
            Assert.Null(review.reason);
            Assert.Equal(TestUsers.Owner.Id, review.reviewed_by);
            Assert.Equal("Test Owner", review.reviewer_name);
            Assert.Equal(now, DateTime.SpecifyKind(review.reviewed_at, DateTimeKind.Utc));

            var audit = Assert.Single(await ArmorAuditAsync(db));
            Assert.Equal("armor.approve", audit.action);
            Assert.Equal(AuditOutcome.Ok, audit.outcome);
            Assert.Equal(TestUsers.Owner.Id, audit.actor_id);
            Assert.Equal("Test Owner", audit.actor_name);
            Assert.Equal(AuditRole.Owner, audit.actor_role);
            Assert.Equal("armor", audit.target_type);
            Assert.Equal("31", audit.target_id);
            Assert.Equal("""{"Status":"Pending Approval"}""", audit.before_json);
            Assert.Equal("""{"Status":"Approved"}""", audit.after_json);
            Assert.Null(audit.detail);
            Assert.Equal("203.0.113.140", audit.ip);
            Assert.False(string.IsNullOrEmpty(audit.request_id));
            Assert.Equal(now, DateTime.SpecifyKind(audit.at, DateTimeKind.Utc));

            // The page it goes back to says what the bot does next, and lists the change
            var after = WebUtility.HtmlDecode(await owner.Client.GetStringAsync(SignInSteps.PathOf(response.Headers.Location), Token));
            Assert.Contains(ArmorWords.Approved, after);
            Assert.Contains("From Pending Approval to Approved", after);
            Assert.Contains("Test Owner", after);
            Assert.Contains(now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC", after);
        }

        // The reason is the reviewer's own text, kept with its line ends made plain, and shown only encoded
        [Fact]
        public async Task RejectKeepsItsReasonForTheHistory()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(32));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.141");

            var response = await PostAsync(admin, 32, "Reject", await OpenAsync(admin, 32), true, ("reason", "  Not\r\nreal <b>art</b>  "));

            Redirected(response, 32, "rejected");
            Assert.Equal(ArmorStatus.NotApproved, await db.StatusAsync(32));

            var review = Assert.Single(await db.ReviewsAsync());
            Assert.Equal("Not\nreal <b>art</b>", review.reason);
            Assert.Equal("Test Admin", review.reviewer_name);

            var audit = Assert.Single(await ArmorAuditAsync(db));
            Assert.Equal("armor.reject", audit.action);
            Assert.Equal(AuditRole.Admin, audit.actor_role);
            Assert.Equal("""{"Status":"Not Approved"}""", audit.after_json);

            var html = await admin.Client.GetStringAsync("/admin/armor/32?done=rejected", Token);
            Assert.Contains("<p class=\"multiline\"><bdi class=\"revealed\">Not&#xA;real &lt;b&gt;art&lt;/b&gt;</bdi></p>", html);
            Assert.Contains(ArmorWords.RejectedWithReason, WebUtility.HtmlDecode(html));
        }

        // The page after an action says what that action did, from the history: a rejection's reason only when there is
        // one, a post that may not come for armor the bot may have seen approved before, and a move back to pending that
        // takes approved armor out of the bot, as Take down does
        [Fact]
        public async Task NoticeSaysWhatTheActionDid()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(54), Submission(55, ArmorStatus.Approved, "Test Armor 55"), Submission(56, ArmorStatus.Pending, "Test Armor 56"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.158");

            async Task<string> AfterAsync(HttpResponseMessage response) =>
                WebUtility.HtmlDecode(await admin.Client.GetStringAsync(SignInSteps.PathOf(response.Headers.Location), Token));

            var rejected = await AfterAsync(await PostAsync(admin, 54, "Reject", await OpenAsync(admin, 54)));
            Assert.Contains(ArmorWords.Rejected, rejected);
            Assert.DoesNotContain(ArmorWords.RejectedWithReason, rejected);

            // Approved from Not Approved, which a take-down leaves too, so the bot may already have seen it approved
            var again = await OpenAsync(admin, 54);
            Assert.Contains(ArmorWords.ApproveAgainHelp, again.Text);
            Assert.Contains(ArmorWords.PostMayNotComeNote, again.Text);
            Assert.Contains(ArmorWords.ApprovedAgain, await AfterAsync(await PostAsync(admin, 54, "Approve", again)));

            var approved = await OpenAsync(admin, 55);
            Assert.Contains(ArmorWords.BackToPendingFromApprovedHelp, approved.Text);
            Assert.Contains(ArmorWords.MovedBackFromApproved, await AfterAsync(await PostAsync(admin, 55, "BackToPending", approved)));

            // Approved before, as its history shows, so approving it again may not post it either
            Assert.Contains(ArmorWords.ApproveAgainHelp, (await OpenAsync(admin, 55)).Text);

            // A first approval from the queue promises the post
            var first = await OpenAsync(admin, 56);
            Assert.Contains(ArmorWords.ApproveHelp, first.Text);
            Assert.Contains(ArmorWords.PostNote, first.Text);
            Assert.Contains(ArmorWords.Approved, await AfterAsync(await PostAsync(admin, 56, "Approve", first)));
        }

        // Take down, then back to pending, then reject, then approve from Not Approved, each with its own rows
        [Fact]
        public async Task EveryActionMovesTheArmorAndIsRecorded()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(33, ArmorStatus.Approved));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.142");

            foreach (var (handler, done, status) in new[]
            {
                ("TakeDown", "taken-down", ArmorStatus.NotApproved),
                ("BackToPending", "pending", ArmorStatus.Pending),
                ("Reject", "rejected", ArmorStatus.NotApproved),
                ("Approve", "approved", ArmorStatus.Approved)
            })
            {
                Redirected(await PostAsync(admin, 33, handler, await OpenAsync(admin, 33)), 33, done);
                Assert.Equal(status, await db.StatusAsync(33));
            }

            Assert.Equal(
            [
                (ArmorStatus.Approved, ArmorStatus.NotApproved),
                (ArmorStatus.NotApproved, ArmorStatus.Pending),
                (ArmorStatus.Pending, ArmorStatus.NotApproved),
                (ArmorStatus.NotApproved, ArmorStatus.Approved)
            ], (await db.ReviewsAsync()).Select(review => (review.from_status, review.to_status)));

            Assert.Equal(["armor.takedown", "armor.pending", "armor.reject", "armor.approve"], (await ArmorAuditAsync(db)).Select(row => row.action));
            Assert.All(await ArmorAuditAsync(db), row => Assert.Equal(AuditOutcome.Ok, row.outcome));

            // The history, newest first
            var text = (await OpenAsync(admin, 33)).Text;
            Assert.True(text.IndexOf("From Not Approved to Approved", StringComparison.Ordinal) < text.IndexOf("From Approved to Not Approved", StringComparison.Ordinal));
        }

        // Armor with no status gets a place in the queue, and its review row and audit row say it had none
        [Fact]
        public async Task ArmorWithNoStatusGoesBackToPending()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(34, null));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.143");

            Redirected(await PostAsync(admin, 34, "BackToPending", await OpenAsync(admin, 34)), 34, "pending");

            Assert.Equal(ArmorStatus.Pending, await db.StatusAsync(34));
            Assert.Null(Assert.Single(await db.ReviewsAsync()).from_status);
            Assert.Equal("""{"Status":null}""", Assert.Single(await ArmorAuditAsync(db)).before_json);
        }

        // Each warning needs a tick, checked again on the server, and the audit row keeps which were ticked
        [Fact]
        public async Task ApproveNeedsEveryWarningTicked()
        {
            var db = await fixture.ReadyAsync();
            var armor = Submission(35, ArmorStatus.Pending, "Night​Raid");
            armor.ArmorDescription = "Visit https://example.com";
            await db.AddAsync(armor);
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.144");
            var page = await OpenAsync(admin, 35);

            Assert.Equal(["HiddenCharacters:Name", "LinkSyntax:Description"], page.Ticks);
            Assert.Contains("required />", page.Html);

            await Refused(await PostAsync(admin, 35, "Approve", page, false), HttpStatusCode.Conflict, ArmorWords.Unticked);
            await Refused(await PostAsync(admin, 35, "Approve", page, false, ("ticks", "HiddenCharacters:Name")), HttpStatusCode.Conflict, ArmorWords.Unticked);

            // A tick for another field doesn't count
            await Refused(await PostAsync(admin, 35, "Approve", page, false, ("ticks", "HiddenCharacters:Name"), ("ticks", "LinkSyntax:Author")),
                HttpStatusCode.Conflict, ArmorWords.Unticked);

            Assert.Equal(ArmorStatus.Pending, await db.StatusAsync(35));
            Assert.Empty(await db.ReviewsAsync());

            // A refusal records the status the armor has, and nothing after, since nothing changed
            var refusals = await ArmorAuditAsync(db);
            Assert.Equal(3, refusals.Count);
            Assert.All(refusals, row => Assert.Equal(AuditOutcome.Refused, row.outcome));
            Assert.All(refusals, row => Assert.Equal("""{"Status":"Pending Approval"}""", row.before_json));
            Assert.All(refusals, row => Assert.Null(row.after_json));
            Assert.Equal("Warnings that weren't ticked: HiddenCharacters:Name, LinkSyntax:Description", refusals[0].detail);
            Assert.Equal("Warnings that weren't ticked: LinkSyntax:Description", refusals[1].detail);

            Redirected(await PostAsync(admin, 35, "Approve", page), 35, "approved");
            Assert.Equal("""{"Status":"Approved","Ticked":["HiddenCharacters:Name","LinkSyntax:Description"]}""", (await ArmorAuditAsync(db))[^1].after_json);
        }

        // The server runs the rules again, so a blocking problem stops an approval even from a form someone made up. Among
        // them the owner's decision: a screenshot that isn't an upload on the website, which the bot would fetch from the
        // server's own network.
        [Theory]
        [InlineData("Night`Raid", null, "NameHasBacktick")]
        [InlineData("Test Armor 36", "http://127.0.0.1:8080/admin", "ScreenshotNotAllowed")]
        [InlineData("Test Armor 36", "https://murderminershub.com/wp-admin/admin-ajax.php?action=x", "ScreenshotNotAllowed")]
        [InlineData("Test Armor 36", "https://murderminershub.com/wp-content/uploads/%2e%2e/%2e%2e/wp-login.php", "ScreenshotNotAllowed")]
        public async Task BlockingRuleStopsEvenAMadeUpApproval(string name, string? screenshot, string kind)
        {
            var db = await fixture.ReadyAsync();
            var armor = Submission(36, ArmorStatus.Pending, name);
            armor.ArmorScreenshot = screenshot ?? armor.ArmorScreenshot;
            await db.AddAsync(armor);
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.145");
            var page = await OpenAsync(admin, 36);

            // The page doesn't offer it, so this is the token and version of its Reject form, sent to Approve
            Assert.DoesNotContain("handler=Approve", page.Html);
            await Refused(await PostAsync(admin, 36, "Approve", page), HttpStatusCode.Conflict, ArmorWords.Blocked);

            Assert.Equal(ArmorStatus.Pending, await db.StatusAsync(36));
            Assert.Empty(await db.ReviewsAsync());

            var refusal = Assert.Single(await ArmorAuditAsync(db));
            Assert.Equal(AuditOutcome.Refused, refusal.outcome);
            Assert.Equal("Blocked by the approval rules: " + kind, refusal.detail);
            Assert.Equal("""{"Status":"Pending Approval"}""", refusal.before_json);
            Assert.Null(refusal.after_json);
        }

        // A helmet someone crafted to end the card's code block, so what follows shows as a link in every server. No rule
        // looked at the helmet, so one click approved it. It blocks now, on the page and on the server.
        [Fact]
        public async Task CraftedHelmetBlocksApproval()
        {
            var db = await fixture.ReadyAsync();
            var armor = Submission(50);
            armor.ArmorChoice = "Bug```[Free Nitro](https://phish.example)```";
            await db.AddAsync(armor);
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.159");
            var page = await OpenAsync(admin, 50);

            Assert.DoesNotContain("handler=Approve", page.Html);
            Assert.Contains("The helmet has a backtick", page.Text);
            Assert.DoesNotContain("The rules found nothing", page.Text);

            await Refused(await PostAsync(admin, 50, "Approve", page), HttpStatusCode.Conflict, ArmorWords.Blocked);
            Assert.Equal(ArmorStatus.Pending, await db.StatusAsync(50));
            Assert.Empty(await db.ReviewsAsync());
            Assert.Equal("Blocked by the approval rules: StyleHasBacktick", Assert.Single(await ArmorAuditAsync(db)).detail);
        }

        // Only Approve runs the rules and needs the ticks. Fails if the rules or the tick check run for every action, when
        // armor approved before a rule, such as one whose screenshot isn't an upload, couldn't be taken down.
        [Theory]
        [InlineData("TakeDown")]
        [InlineData("Reject")]
        [InlineData("BackToPending")]
        public async Task OnlyApproveRunsTheRules(string handler)
        {
            var db = await fixture.ReadyAsync();

            // Approved armor whose screenshot isn't an upload and whose name another approved armor has; pending armor with
            // a backtick in its name and a link left unticked; and rejected armor with warnings left unticked
            var (armor, done, after) = handler switch
            {
                "TakeDown" => (Submission(48, ArmorStatus.Approved, "Frost"), "taken-down", ArmorStatus.NotApproved),
                "Reject" => (Submission(48, ArmorStatus.Pending, "Night`Raid"), "rejected", ArmorStatus.NotApproved),
                _ => (Submission(48, ArmorStatus.NotApproved, "Night​Raid"), "pending", ArmorStatus.Pending)
            };

            if (handler == "TakeDown")
                armor.ArmorScreenshot = "https://murderminershub.com/x.png";
            else
                armor.ArmorDescription = "Visit https://example.com";

            await db.AddAsync(armor, Submission(49, ArmorStatus.Approved, "frost"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.160");
            var page = await OpenAsync(admin, 48);

            if (handler == "TakeDown")
            {
                Assert.Contains("The screenshot isn't an upload on the website", page.Text);
                Assert.Contains("An approved armor already has this name", page.Text);
            }
            else
            {
                Assert.Contains("The description has a link", page.Text);
            }

            Assert.Contains($"?handler={handler}\"", page.Html);
            Redirected(await PostAsync(admin, 48, handler, page), 48, done);

            Assert.Equal(after, await db.StatusAsync(48));
            Assert.Single(await db.ReviewsAsync());
            Assert.Equal(AuditOutcome.Ok, Assert.Single(await ArmorAuditAsync(db)).outcome);
        }

        // Armor stored as 'approved' is Approved to the bot, which shows it, so it has to be possible to take it down. The
        // form sends the website's word, the change matches through the column's collation and saves the website's word,
        // and the audit row keeps the status as it was stored.
        [Fact]
        public async Task ArmorStoredAsApprovedSpeltDifferentlyCanBeTakenDown()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(51, "approved"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.161");
            var page = await OpenAsync(admin, 51);

            Assert.Equal(ArmorStatus.Approved, page.Expected);
            Redirected(await PostAsync(admin, 51, "TakeDown", page), 51, "taken-down");

            Assert.Equal(ArmorStatus.NotApproved, await db.StatusAsync(51));
            Assert.Equal(ArmorStatus.Approved, Assert.Single(await db.ReviewsAsync()).from_status);

            var audit = Assert.Single(await ArmorAuditAsync(db));
            Assert.Equal("""{"Status":"approved"}""", audit.before_json);
            Assert.Equal("""{"Status":"Not Approved"}""", audit.after_json);
        }

        // A status the table matches to none of the website's words can't be changed, even by a form someone made up, so the
        // check inside the transaction refuses it. Its refusals record the status as stored, and nothing after.
        [Fact]
        public async Task StatusTheWebsiteDoesntUseRefusesAMadeUpForm()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(52, "Pending"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.162");
            var token = await SignInSteps.FormTokenAsync(admin);
            var version = ArmorVersion.Of(Submission(52, "Pending"));

            foreach (var (handler, expected) in new[] { ("BackToPending", ""), ("Approve", ArmorStatus.Pending), ("TakeDown", ArmorStatus.Approved) })
            {
                var response = await CraftedAsync(admin, $"/admin/armor/52?handler={handler}", token, ("expected", expected), ("version", version));
                await Refused(response, HttpStatusCode.Conflict, ArmorWords.Changed);
                Assert.Contains(ArmorWords.UnknownStatus, await TextAsync(response));
            }

            Assert.Equal("Pending", await db.StatusAsync(52));
            Assert.Empty(await db.ReviewsAsync());

            var refusals = await ArmorAuditAsync(db);
            Assert.Equal(3, refusals.Count);
            Assert.All(refusals, row => Assert.Equal(AuditOutcome.Refused, row.outcome));
            Assert.All(refusals, row => Assert.Equal("""{"Status":"Pending"}""", row.before_json));
            Assert.All(refusals, row => Assert.Null(row.after_json));
        }

        // An action moves the armor out of the tab the reviewer was working through, so its page keeps that tab and search
        // for Back to the list, rather than sending them to the new status's tab
        [Fact]
        public async Task BackToTheListReturnsToTheTabTheReviewerCameFrom()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(53, ArmorStatus.Pending, "Frost Knight"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.163");

            var list = await admin.Client.GetStringAsync("/admin/armor?q=frost", Token);
            var link = WebUtility.HtmlDecode(Regex.Match(list, "href=\"(/admin/armor/53[^\"]*)\"").Groups[1].Value);
            Assert.Equal("/admin/armor/53?list=pending&q=frost", link);

            var page = Read(await admin.Client.GetStringAsync(link, Token));
            Assert.Contains("href=\"/admin/armor?status=pending&amp;q=frost\">Back to the list</a>", page.Html);

            // The form's own address carries them, and so does the page the action leads to
            var action = WebUtility.HtmlDecode(Regex.Match(page.Html, "action=\"([^\"]*handler=Approve[^\"]*)\"").Groups[1].Value);
            Assert.Contains("list=pending", action);
            Assert.Contains("q=frost", action);

            var response = await CraftedAsync(admin, action, page.Token, ("expected", page.Expected), ("version", page.Version));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/admin/armor/53?done=approved&list=pending&q=frost", SignInSteps.PathOf(response.Headers.Location));

            var after = await admin.Client.GetStringAsync(SignInSteps.PathOf(response.Headers.Location), Token);
            Assert.Contains(ArmorWords.Approved, WebUtility.HtmlDecode(after));
            Assert.Contains("href=\"/admin/armor?status=pending&amp;q=frost\">Back to the list</a>", after);

            // Opened on its own, or with a tab the list doesn't have, the page goes back to the tab of the armor's status
            Assert.Contains("href=\"/admin/armor?status=approved\">Back to the list</a>", await admin.Client.GetStringAsync("/admin/armor/53", Token));
            Assert.Contains("href=\"/admin/armor?status=approved\">Back to the list</a>", await admin.Client.GetStringAsync("/admin/armor/53?list=bogus&q=x", Token));
        }

        // Another armor approved with the same name since the page opened: the check inside the transaction refuses it
        [Fact]
        public async Task NameApprovedSinceThePageOpenedBlocks()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(37, ArmorStatus.Pending, "Frost"), Submission(38, ArmorStatus.Pending, "frost "));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.146");
            var first = await OpenAsync(admin, 37);
            var second = await OpenAsync(admin, 38);

            Redirected(await PostAsync(admin, 37, "Approve", first), 37, "approved");
            var response = await PostAsync(admin, 38, "Approve", second);

            await Refused(response, HttpStatusCode.Conflict, ArmorWords.Blocked);
            Assert.Contains("An approved armor already has this name", await TextAsync(response));
            Assert.Equal(ArmorStatus.Pending, await db.StatusAsync(38));
            Assert.Equal("Blocked by the approval rules: NameTaken", (await ArmorAuditAsync(db))[^1].detail);
        }

        // Two reviewers at once: the second one's action finds the status changed, and writes nothing but its refusal
        [Fact]
        public async Task StaleStatusIsAConflictAndWritesNoReview()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(39));
            await using var factory = new DashboardFactory(db);
            var first = await ReviewerAsync(factory, "203.0.113.147", TestUsers.Admin);
            var second = await ReviewerAsync(factory, "203.0.113.148", TestUsers.SecondAdmin);
            var firstPage = await OpenAsync(first, 39);
            var secondPage = await OpenAsync(second, 39);

            Redirected(await PostAsync(first, 39, "Reject", firstPage), 39, "rejected");
            var late = await PostAsync(second, 39, "Approve", secondPage);

            await Refused(late, HttpStatusCode.Conflict, ArmorWords.Changed);
            Assert.Equal(ArmorStatus.NotApproved, await db.StatusAsync(39));
            Assert.Equal(ArmorStatus.Pending, Assert.Single(await db.ReviewsAsync()).from_status);

            var audit = await ArmorAuditAsync(db);
            Assert.Equal([("armor.reject", AuditOutcome.Ok), ("armor.approve", AuditOutcome.Refused)], audit.Select(row => (row.action, row.outcome)));
            Assert.Equal("The armor had changed since the page showed it", audit[1].detail);
            Assert.Equal(TestUsers.SecondAdmin.Id, audit[1].actor_id);

            // The status the armor really had, not the one the late page showed, and no change after it
            Assert.Equal("""{"Status":"Not Approved"}""", audit[1].before_json);
            Assert.Null(audit[1].after_json);

            // The page it shows is the armor as it is now, whose forms work
            var now = Read(await late.Content.ReadAsStringAsync(Token));
            Assert.Equal(ArmorStatus.NotApproved, now.Expected);
            Redirected(await PostAsync(second, 39, "Approve", now), 39, "approved");
        }

        // A field the website changed after the page opened is a conflict, even with the same status, so nobody approves
        // what they didn't see
        [Theory]
        [InlineData("ArmorDescription = 'Changed after the page opened'")]
        [InlineData("ArmorScreenshot = 'https://murderminershub.com/wp-content/uploads/other.png'")]
        [InlineData("Helmet = 200")]
        [InlineData("ColorArray = 'AAAAAAAAAAAAAAAAAA=='")]
        public async Task ArmorChangedSinceThePageOpenedIsAConflict(string change)
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(40));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.149");
            var page = await OpenAsync(admin, 40);

            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.ArmorTable} SET {change} WHERE ID = 40");
            var response = await PostAsync(admin, 40, "Approve", page);

            await Refused(response, HttpStatusCode.Conflict, ArmorWords.Changed);
            Assert.Equal(ArmorStatus.Pending, await db.StatusAsync(40));
            Assert.Empty(await db.ReviewsAsync());

            // The page it shows has a version of its own, which works. Reject, since a stored save string that isn't the
            // colours' stops an approval.
            var fresh = Read(await response.Content.ReadAsStringAsync(Token));
            Assert.NotEqual(page.Version, fresh.Version);
            Redirected(await PostAsync(admin, 40, "Reject", fresh), 40, "rejected");
        }

        // The bot's hourly job stores the save string the page already showed, which isn't a change
        [Fact]
        public async Task SaveStringTheBotStoresIsNoConflict()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(41, ArmorStatus.Approved));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.150");
            var page = await OpenAsync(admin, 41);

            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.ArmorTable} SET ColorArray = 'AQIDBAUGBwgJCgsMDQ==' WHERE ID = 41");

            Redirected(await PostAsync(admin, 41, "TakeDown", page), 41, "taken-down");
            Assert.Equal(ArmorStatus.NotApproved, await db.StatusAsync(41));
        }

        // Checked before the database, which throws for anything it can't save: a form someone made up gets a 400, an armor
        // that isn't there a 404, never a 500, and nothing is written
        [Fact]
        public async Task MadeUpFormsAreRefusedBeforeTheDatabase()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(42), Submission(43, ArmorStatus.Approved, "Test Armor 43"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.151");
            var page = await OpenAsync(admin, 42);
            var approved = await OpenAsync(admin, 43);
            var version = page.Version;
            const string pending = ArmorStatus.Pending;

            var cases = new (string What, string Path, (string, string)[] Fields)[]
            {
                ("an unknown status", "/admin/armor/42?handler=Approve", [("expected", "Pending"), ("version", version)]),
                ("a status in other letters", "/admin/armor/42?handler=Approve", [("expected", "pending approval"), ("version", version)]),
                ("no version", "/admin/armor/42?handler=Approve", [("expected", pending)]),
                ("a version in capitals", "/admin/armor/42?handler=Approve", [("expected", pending), ("version", version.ToUpperInvariant())]),
                ("a short version", "/admin/armor/42?handler=Approve", [("expected", pending), ("version", "abc")]),
                ("an action the status doesn't offer", "/admin/armor/42?handler=TakeDown", [("expected", pending), ("version", version)]),
                ("approving approved armor", "/admin/armor/43?handler=Approve", [("expected", ArmorStatus.Approved), ("version", approved.Version)]),
                ("a reason over the limit", "/admin/armor/42?handler=Reject", [("expected", pending), ("version", version), ("reason", new string('a', 501))]),
                ("a tick that names nothing", "/admin/armor/42?handler=Approve", [("expected", pending), ("version", version), ("ticks", "Nothing:Name")]),
                ("too many ticks", "/admin/armor/42?handler=Approve", [("expected", pending), ("version", version), .. Enumerable.Repeat(("ticks", "LinkSyntax:Author"), ArmorForm.MaxTicks + 1)]),
                ("no handler", "/admin/armor/42", [("expected", pending), ("version", version)]),
                ("a handler that isn't there", "/admin/armor/42?handler=Explode", [("expected", pending), ("version", version)])
            };

            foreach (var (what, path, fields) in cases)
            {
                var response = await CraftedAsync(admin, path, page.Token, fields);
                Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{what} answered {response.StatusCode}");
            }

            // Another method, with the token and from the dashboard's own origin
            var put = await SignInSteps.PostAsync(admin, "/admin/armor/42?handler=Approve", page.Token, origin: DashboardFactory.PublicOrigin, method: HttpMethod.Put,
                fields: [new("expected", pending), new("version", version)]);
            Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);

            var missing = await CraftedAsync(admin, "/admin/armor/999?handler=Approve", page.Token, ("expected", pending), ("version", version));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            Assert.Equal(ArmorStatus.Pending, await db.StatusAsync(42));
            Assert.Equal(ArmorStatus.Approved, await db.StatusAsync(43));
            Assert.Empty(await db.ReviewsAsync());
            Assert.Empty(await ArmorAuditAsync(db));
            Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Level >= LogLevel.Error);
        }

        // The same as sign-out: the form's token, and the dashboard's own origin. murderminershub.com counts as the same site
        // to browsers, so its pages carry the session cookie.
        [Fact]
        public async Task ActionsNeedTheTokenAndTheDashboardsOrigin()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(44));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.152");
            var page = await OpenAsync(admin, 44);
            KeyValuePair<string, string>[] fields = [new("expected", ArmorStatus.Pending), new("version", page.Version)];
            const string path = "/admin/armor/44?handler=Approve";

            Assert.Equal(HttpStatusCode.BadRequest, (await SignInSteps.PostAsync(admin, path, null, origin: DashboardFactory.PublicOrigin, fields: fields)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await SignInSteps.PostAsync(admin, path, "made-up", origin: DashboardFactory.PublicOrigin, fields: fields)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SignInSteps.PostAsync(admin, path, page.Token, origin: "https://murderminershub.com", fields: fields)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SignInSteps.PostAsync(admin, path, page.Token, fetchSite: "same-site", fields: fields)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SignInSteps.PostAsync(admin, path, page.Token, fields: fields)).StatusCode);

            // Without a session, whatever the form holds, it's sign-in
            var anonymous = await SignInSteps.PostAsync(factory.Visitor("203.0.113.153"), path, page.Token, origin: DashboardFactory.PublicOrigin, fields: fields);
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.StartsWith("/signin?returnUrl=", SignInSteps.PathOf(anonymous.Headers.Location));

            Assert.Equal(ArmorStatus.Pending, await db.StatusAsync(44));
            Assert.Empty(await db.ReviewsAsync());
            Assert.Empty(await ArmorAuditAsync(db));

            // The same form, sent properly, works
            Redirected(await SignInSteps.PostAsync(admin, path, page.Token, origin: DashboardFactory.PublicOrigin, fields: fields), 44, "approved");
        }

        // A write that fails inside the transaction undoes the status and every other row with it, before anything could be
        // committed, so the error page says nothing was changed. Fails if it says it can't tell, which only a commit that
        // may have gone through calls for.
        [Theory]
        [InlineData("review")]
        [InlineData("audit")]
        public async Task FailedWriteUndoesTheWholeAction(string failing)
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(45));
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services =>
                {
                    if (failing == "review")
                        services.Replace(ServiceDescriptor.Transient<IArmorReviewData>(provider => new FailingReviews(ActivatorUtilities.CreateInstance<ArmorReviewData>(provider))));
                    else
                        services.Replace(ServiceDescriptor.Transient<IAuditLogData>(provider => new FailingAudit(ActivatorUtilities.CreateInstance<AuditLogData>(provider), "armor.approve")));
                }
            });
            var admin = await ReviewerAsync(factory, "203.0.113.154");

            var response = await PostAsync(admin, 45, "Approve", await OpenAsync(admin, 45));

            await Refused(response, HttpStatusCode.ServiceUnavailable, ErrorResponses.Unavailable);
            Assert.DoesNotContain(ArmorWords.Unsure, await TextAsync(response));
            Assert.Equal(ArmorStatus.Pending, await db.StatusAsync(45));
            Assert.Empty(await db.ReviewsAsync());
            Assert.Empty(await ArmorAuditAsync(db));
        }

        // A limit that passes during the commit can't stop it, so the change can land while the page says it can't tell.
        // Trying again is safe: the status the form expects is gone, so nothing more is written, and the page says the
        // armor already has the status the try was for, rather than that someone else changed it.
        [Fact]
        public async Task TimeoutAfterTheCommitSaysItMightHaveSaved()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(46));
            var faults = new Faults { TimeOutAfterCommit = true };
            await using var factory = Faulty(db, faults);
            var admin = await ReviewerAsync(factory, "203.0.113.155");
            var page = await OpenAsync(admin, 46);

            var unsure = await PostAsync(admin, 46, "Approve", page);
            await Refused(unsure, HttpStatusCode.ServiceUnavailable, ArmorWords.Unsure);
            Assert.Contains("<title>Not sure it was saved - MinerBot dashboard</title>", await unsure.Content.ReadAsStringAsync(Token));
            PageTests.AssertAccessible("a save that may not have landed", await unsure.Content.ReadAsStringAsync(Token));
            Assert.DoesNotContain(ErrorResponses.Unavailable, await TextAsync(unsure));
            Assert.Equal(ArmorStatus.Approved, await db.StatusAsync(46));
            Assert.Single(await db.ReviewsAsync());
            Assert.Equal(ArmorStatus.Approved, (await OpenAsync(admin, 46)).Expected);

            // The change's own row, and one written after it, outside the transaction, saying it may not have landed, with
            // the same action, target and before and after
            var rows = await ArmorAuditAsync(db);
            Assert.Equal([AuditOutcome.Ok, AuditOutcome.Unknown], rows.Select(row => row.outcome));
            Assert.Equal("armor.approve", rows[1].action);
            Assert.Equal("46", rows[1].target_id);
            Assert.Equal("""{"Status":"Pending Approval"}""", rows[1].before_json);
            Assert.Equal("""{"Status":"Approved"}""", rows[1].after_json);
            Assert.Equal(UnconfirmedChanges.Detail, rows[1].detail);
            Assert.Equal(rows[0].request_id, rows[1].request_id);
            Assert.Equal("203.0.113.155", rows[1].ip);

            faults.TimeOutAfterCommit = false;
            var again = await PostAsync(admin, 46, "Approve", page);
            await Refused(again, HttpStatusCode.Conflict, ArmorWords.Already(ArmorStatus.Approved));
            Assert.DoesNotContain(ArmorWords.Changed, await TextAsync(again));
            Assert.Contains("class=\"notice notice-done\" role=\"status\"", await again.Content.ReadAsStringAsync(Token));
            Assert.Single(await db.ReviewsAsync());
            Assert.Equal(AuditOutcome.Refused, (await ArmorAuditAsync(db))[^1].outcome);
        }

        // A refusal commits only its audit row, so a commit that times out after one leaves the armor as the work found it.
        // The page says why nothing was done, not that it can't tell, and no row says the approval may have landed.
        [Fact]
        public async Task TimeoutAfterARefusalStillShowsTheRefusal()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(58));
            var faults = new Faults();
            await using var factory = Faulty(db, faults);
            var admin = await ReviewerAsync(factory, "203.0.113.165");
            var page = await OpenAsync(admin, 58);

            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.ArmorTable} SET ArmorDescription = 'Changed after the page opened' WHERE ID = 58");
            faults.TimeOutAfterCommit = true;

            var response = await PostAsync(admin, 58, "Approve", page);

            await Refused(response, HttpStatusCode.Conflict, ArmorWords.Changed);
            Assert.DoesNotContain(ArmorWords.Unsure, await TextAsync(response));
            Assert.Equal(ArmorStatus.Pending, await db.StatusAsync(58));
            Assert.Empty(await db.ReviewsAsync());
            Assert.Equal([AuditOutcome.Refused], (await ArmorAuditAsync(db)).Select(row => row.outcome));
            Assert.Contains(factory.Logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("which changes nothing"));
        }

        // The row saying a change may not have landed is best effort: when it can't be written either, the page still says
        // the change may or may not have been saved, and the warning is logged
        [Fact]
        public async Task UnsureRowThatCantBeWrittenStillShowsUnsure()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(57));
            var faults = new Faults { TimeOutAfterCommit = true };
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services =>
                {
                    services.Replace(ServiceDescriptor.Transient<IArmorSubmissionData>(provider => new Submissions(ActivatorUtilities.CreateInstance<ArmorSubmissionData>(provider), faults)));
                    services.Replace(ServiceDescriptor.Transient<IAuditLogData>(provider => new FailingAudit(ActivatorUtilities.CreateInstance<AuditLogData>(provider), "armor.approve", AuditOutcome.Unknown)));
                }
            });
            var admin = await ReviewerAsync(factory, "203.0.113.164");

            var unsure = await PostAsync(admin, 57, "Approve", await OpenAsync(admin, 57));

            await Refused(unsure, HttpStatusCode.ServiceUnavailable, ArmorWords.Unsure);
            Assert.Equal(ArmorStatus.Approved, await db.StatusAsync(57));
            Assert.Equal([AuditOutcome.Ok], (await ArmorAuditAsync(db)).Select(row => row.outcome));
            Assert.Contains(factory.Logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("couldn't be written either"));
        }

        // A transaction that loses a deadlock runs again from the start, and its rows are written once
        [Fact]
        public async Task DeadlockedActionRunsAgainAndWritesOnce()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(47));
            var faults = new Faults { DeadlockFirstRun = true };
            await using var factory = Faulty(db, faults);
            var admin = await ReviewerAsync(factory, "203.0.113.156");

            Redirected(await PostAsync(admin, 47, "Approve", await OpenAsync(admin, 47)), 47, "approved");

            Assert.Equal(2, faults.Runs);
            Assert.Equal(ArmorStatus.Approved, await db.StatusAsync(47));
            Assert.Single(await db.ReviewsAsync());
            Assert.Single(await ArmorAuditAsync(db));
        }

        // The website's database down: the pages fail closed, and an action that couldn't read the armor says nothing changed
        [Fact]
        public async Task WebsiteDatabaseDownIsUnavailable()
        {
            var db = await fixture.ReadyAsync();
            var dead = new DeadDatabase();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions { SiteConnection = dead.SiteConnection });
            var admin = await ReviewerAsync(factory, "203.0.113.157");
            var token = await SignInSteps.FormTokenAsync(admin);

            foreach (var path in new[] { "/admin/armor", "/admin/armor/1" })
                await Refused(await admin.Client.GetAsync(path, Token), HttpStatusCode.ServiceUnavailable, ErrorResponses.Unavailable);

            var action = await CraftedAsync(admin, "/admin/armor/1?handler=Approve", token, ("expected", ArmorStatus.Pending), ("version", new string('a', 64)));
            await Refused(action, HttpStatusCode.ServiceUnavailable, ErrorResponses.Unavailable);

            foreach (var secret in new[] { dead.Login, dead.Password, dead.SiteDatabase })
                Assert.DoesNotContain(secret, factory.Logs.Text);
        }

        static TimeoutException Dropped() => new("The database didn't answer within 15 seconds");

        // Writes its row, then fails as a dropped connection would after the statement ran
        sealed class FailingReviews(ArmorReviewData real) : IArmorReviewData
        {
            public async Task AddReview(ArmorReview review, DataTransaction? transaction = null)
            {
                await real.AddReview(review, transaction);
                throw Dropped();
            }

            public Task<IReadOnlyList<ArmorReview>> ListReviews(long armorId) => real.ListReviews(armorId);
        }

        // Fails for one action's rows with one outcome: after writing an ok row, as a dropped connection would after the
        // statement ran, and before writing any other
        sealed class FailingAudit(AuditLogData real, string action, string outcome = AuditOutcome.Ok) : IAuditLogData
        {
            public async Task AddAudit(AuditEntry entry, DataTransaction? transaction = null)
            {
                var failing = entry.action == action && entry.outcome == outcome;

                if (failing && outcome != AuditOutcome.Ok)
                    throw Dropped();

                await real.AddAudit(entry, transaction);

                if (failing)
                    throw Dropped();
            }

            public Task<IReadOnlyList<AuditEntry>> LatestChanges(string targetType, IReadOnlyCollection<string> targetIds) => real.LatestChanges(targetType, targetIds);
            public Task<Page<AuditEntry>> ListAudit(AuditQuery query) => real.ListAudit(query);
        }

        // What the armor repository's transactions should do wrong. One for the whole app, since each request gets a
        // repository of its own.
        sealed class Faults
        {
            public bool DeadlockFirstRun { get; set; }
            public bool TimeOutAfterCommit { get; set; }

            // How many times a transaction's work ran
            public int Runs;
        }

        static DashboardFactory Faulty(MariaDbFixture db, Faults faults) => new(db, new DashboardFactoryOptions
        {
            Services = services => services.Replace(ServiceDescriptor.Transient<IArmorSubmissionData>(provider =>
                new Submissions(ActivatorUtilities.CreateInstance<ArmorSubmissionData>(provider), faults)))
        });

        // The real repository, whose transactions can lose a deadlock on their first run, or time out once they've committed
        sealed class Submissions(ArmorSubmissionData real, Faults faults) : IArmorSubmissionData
        {
            public async Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work)
            {
                var result = await real.RunInTransaction(async transaction =>
                {
                    var done = await work(transaction);

                    // InnoDB has rolled the whole transaction back when it reports a deadlock, which the rollback here stands for
                    if (Interlocked.Increment(ref faults.Runs) == 1 && faults.DeadlockFirstRun)
                        throw MySqlErrors.Deadlock();

                    return done;
                });

                if (faults.TimeOutAfterCommit)
                    throw Dropped();

                return result;
            }

            public Task<bool> ChangeStatus(long id, string? expectedStatus, string newStatus, DataTransaction? transaction = null) => real.ChangeStatus(id, expectedStatus, newStatus, transaction);
            public Task<ArmorSubmission?> GetSubmission(long id, DataTransaction? transaction = null) => real.GetSubmission(id, transaction);
            public Task<bool> IsApprovedNameTaken(string name, long exceptId, DataTransaction? transaction = null) => real.IsApprovedNameTaken(name, exceptId, transaction);
            public Task<Page<ArmorSubmission>> ListByStatus(string? status, int page, int pageSize) => real.ListByStatus(status, page, pageSize);
            public Task<Page<ArmorSubmission>> Search(string text, string? status, int page, int pageSize) => real.Search(text, status, page, pageSize);
        }
    }
}
