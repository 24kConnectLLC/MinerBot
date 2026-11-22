using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Status;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Tests
{
    // The banners about requests to the bot, on the Overview and the controls page: posts paused, requests off at either
    // end, the bot refusing requests as not the dashboard's, a stored status text the rules refuse, and, for the Owner
    // alone, their direct messages failing and a sign-in message refused. Each in fixed words, with a name or a reason's
    // hidden characters as their codes, and never a refused status text.
    [Collection("Database")]
    public partial class RequestBannerTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static readonly DateTime Now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

        static readonly BannerFacts Nothing = new(true, null, null, null, null, null, null, null);

        static BotStatusRow Status(DateTime started) => new() { run_id = 1, started_at = started, heartbeat_at = Now.AddSeconds(-5), discord_state = DiscordState.Connected,
            steam_state = SteamState.LoggedOn };

        static BotEventRow Event(string kind, DateTime at, string? detail = null) => new() { id = 7, at = at, kind = kind, level = EventLevel.Warning, detail = detail };

        static BotSettingsRow Paused(string reason = "Testing the pause") => new()
        {
            posts_paused = true,
            pause_reason = reason,
            pause_changed_at = Now.AddHours(-2),
            pause_changed_by = TestUsers.Owner.Id,
            pause_request_id = 11,
            updated_at = Now.AddHours(-2)
        };

        static RequestPageRow Request(ulong id, string kind, string? state = null, string? dm = null, DateTime? ended = null, string? result = null) => new()
        {
            id = id,
            kind = kind,
            actor_id = TestUsers.Owner.Id,
            actor_name = "Test Owner",
            actor_role = RequestRoles.Owner,
            created_at = Now.AddMinutes(-10),
            expires_at = Now.AddMinutes(-8),
            state = state,
            ended_at = ended,
            result = result,
            dm_state = dm
        };

        static string Text(string html) => Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(html), "<[^>]+>", " "), "\\s+", " ");

        // Each banner's words and link, the Owner's seven in order, all at once
        [Fact]
        public void EachBannerHasItsWords()
        {
            var settings = Paused("Testing\u200B the pause");
            settings.status_text = "Join x.com/minerbot";

            var facts = new BannerFacts(false, settings, "Test\u202EOwner", Status(Now.AddHours(-1)),
                Event(EventWords.RequestsOff, Now.AddHours(-1).AddSeconds(1), "the request-signing key isn't set, so the bot acts on no request from the dashboard"),
                Event(EventWords.RequestRejected, Now.AddHours(-3)), Request(41, RequestKinds.BotRestart, RequestStates.Done, RequestDmStates.Failed, Now.AddMinutes(-9)),
                Request(42, RequestKinds.OwnerSigninAlert, RequestStates.Refused, ended: Now.AddMinutes(-9), result: "The bot takes this at most 20 times an hour."));

            var banners = RequestBanners.Of(facts, DashboardRole.Owner, Now);

            Assert.Equal(
            [
                new Banner("MinerBot's armor, map and lobby posts are paused, since 2026-11-21 10:00:00 UTC, 2 hours ago, by Test[U+202E]Owner (request 11), because: "
                    + "Testing[U+200B] the pause. What the bot finds waits and goes out when posts are resumed, unless the bot restarts first, which loses it.",
                    "Resume them on the controls page", "/admin/bot/controls#posts"),
                new Banner("Requests to the bot are off: the dashboard has no request-signing key, so its controls, Announce now, test messages, refreshes, custom-link "
                    + "lookups and sign-in messages don't work. On the server, ./setup-dashboard.sh --no-questions makes one.", "The deploy notes", Banners.DeployNotes),
                new Banner("The bot takes no requests: when this run started, at 11:00:00 UTC, it found that the request-signing key isn't set, so the bot acts on no "
                    + "request from the dashboard. On the server, ./setup-dashboard.sh --no-questions puts the key in bot.env and recreates the bot, then "
                    + "./status.sh --check says whether both have it.", "The deploy notes", Banners.DeployNotes),
                new Banner("The bot refused a request as not made by this dashboard at 09:00:00 UTC, 3 hours ago: something other than the dashboard wrote it, or the "
                    + "bot's request-signing key isn't the dashboard's. ./status.sh --check on the server says which.", "Those refusals on the Bot page",
                    "/admin/bot?kind=request.rejected"),
                new Banner("Discord shows MinerBot's default status, Playing Murder Miners on my PC, because the stored status text breaks the rules (the status text has "
                    + "a link). Set another, or go back to the default, on the controls page.", "The controls page", "/admin/bot/controls#status-text"),
                new Banner("The bot's last direct message to you failed, at 11:51:00 UTC (request 41), so its messages about sign-ins and Owner-level requests aren't "
                    + "reaching you. Discord refuses them while you don't allow direct messages from members of a server you share with MinerBot.", "That request",
                    "/admin/requests/41"),
                new Banner("The bot refused a sign-in message at 11:51:00 UTC: The bot takes this at most 20 times an hour. If those sign-ins weren't all yours and your "
                    + "admins', check the Sessions page.", "Sessions", "/admin/sessions")
            ], banners);

            Assert.Empty(RequestBanners.Of(Nothing, DashboardRole.Owner, Now));
        }

        // A stored status text the rules refuse is never shown, here as on the controls page: the banner names the rule it
        // breaks and holds none of the text. A text they pass makes no banner. Fails if the stored text is shown as it is.
        [Theory]
        [InlineData("Join x.com/minerbot", "the status text has a link", "x.com")]
        [InlineData("Hi @everyone", "the status text has Discord's mention syntax", "@everyone")]
        [InlineData("Back\u202Esoon", "the status text has a character that shows nothing or changes the text's direction", "Back")]
        public void AStoredStatusTextTheRulesRefuseIsNeverShown(string stored, string rule, string part)
        {
            var settings = new BotSettingsRow { status_text = stored, status_changed_at = Now, status_request_id = 12, updated_at = Now };

            foreach (var role in new[] { DashboardRole.Owner, DashboardRole.Admin })
            {
                var banner = Assert.Single(RequestBanners.Of(Nothing with { Settings = settings }, role, Now));
                Assert.Contains($"because the stored status text breaks the rules ({rule})", banner.Text);
                Assert.DoesNotContain(part, banner.Text);
            }

            var line = SettingsWords.StatusLine(settings);
            Assert.Equal((StatusShown.Refused, (string?)null, rule), (line.Shown, line.Text, line.Rule));
            Assert.DoesNotContain(part, line.Start + line.End);

            settings.status_text = "Mining at night";
            Assert.Empty(RequestBanners.Of(Nothing with { Settings = settings }, DashboardRole.Owner, Now));
        }

        // The Owner's direct messages and the sign-in messages are about the Owner: an Admin never gets those two banners, the
        // rest alike. Fails if they're shown to Admins.
        [Fact]
        public void TheDirectMessageAndAlertBannersAreTheOwnersAlone()
        {
            var facts = Nothing with
            {
                LatestDm = Request(41, RequestKinds.PostsPause, RequestStates.Done, RequestDmStates.Failed, Now.AddMinutes(-9)),
                RefusedAlert = Request(42, RequestKinds.OwnerSigninAlert, RequestStates.Refused, ended: Now.AddMinutes(-9), result: "Too many.")
            };

            Assert.Equal(["That request", "Sessions"], RequestBanners.Of(facts, DashboardRole.Owner, Now).Select(banner => banner.LinkText));
            Assert.Empty(RequestBanners.Of(facts, DashboardRole.Admin, Now));

            // A message that went, and a sign-in message the bot took, make none
            Assert.Empty(RequestBanners.Of(facts with
            {
                LatestDm = Request(41, RequestKinds.PostsPause, RequestStates.Done, RequestDmStates.Sent, Now.AddMinutes(-9)),
                RefusedAlert = Request(42, RequestKinds.OwnerSigninAlert, RequestStates.Done, ended: Now.AddMinutes(-9), result: "Told the owner.")
            }, DashboardRole.Owner, Now));
        }

        // A request the bot refused as not the dashboard's is a banner for a day after, as is a refused sign-in message;
        // after that, the Bot page's events keep it
        [Fact]
        public void TheRejectedBannerLastsADay()
        {
            Assert.Single(RequestBanners.Of(Nothing with { Rejected = Event(EventWords.RequestRejected, Now.AddDays(-1)) }, DashboardRole.Admin, Now));
            Assert.Empty(RequestBanners.Of(Nothing with { Rejected = Event(EventWords.RequestRejected, Now.AddDays(-1).AddSeconds(-1)) }, DashboardRole.Admin, Now));

            RequestPageRow Alert(DateTime? ended) => Request(42, RequestKinds.OwnerSigninAlert, RequestStates.Refused, ended: ended, result: "Too many.");

            Assert.Single(RequestBanners.Of(Nothing with { RefusedAlert = Alert(Now.AddDays(-1)) }, DashboardRole.Owner, Now));
            Assert.Empty(RequestBanners.Of(Nothing with { RefusedAlert = Alert(Now.AddDays(-1).AddSeconds(-1)) }, DashboardRole.Owner, Now));

            // One whose times can't be read may be recent, so it's shown
            var unreadable = Alert(null);
            unreadable.created_at = null;
            Assert.Contains("at an unreadable time", Assert.Single(RequestBanners.Of(Nothing with { RefusedAlert = unreadable }, DashboardRole.Owner, Now)).Text);
        }

        // The bot refuses a row in another form, or one whose signature didn't match, as not the dashboard's; every other
        // reason its event gives comes after the signature passed, so the dashboard signed that row, and the banner says the
        // two apps run different versions rather than blaming a forger or the keys. A detail it can't read keeps the forger's
        // words. Fails if every refusal gets them.
        [Theory]
        [InlineData("request 99 refused: its signature didn't match", false)]
        [InlineData("request 99 refused: it isn't in the form the dashboard writes", false)]
        [InlineData("requests 98 to 99 refused: their signature didn't match (2 in one minute)", false)]
        [InlineData("requests 98 to 99 refused: they aren't in the form the dashboard writes (2 in one minute)", false)]
        [InlineData(null, false)]
        [InlineData("request 99 (guild.leave) refused: its role may not ask for its kind", true)]
        [InlineData("request 99 refused: its kind isn't on the bot's list", true)]
        [InlineData("requests 98 to 99 refused: their details don't fit their kind (2 in one minute)", true)]
        public void TheRejectedBannerSaysWhetherTheDashboardSignedIt(string? detail, bool signed)
        {
            foreach (var role in new[] { DashboardRole.Owner, DashboardRole.Admin })
            {
                var banner = Assert.Single(RequestBanners.Of(Nothing with { Rejected = Event(EventWords.RequestRejected, Now.AddMinutes(-10), detail) }, role, Now));

                Assert.Equal(signed
                    ? "The bot refused a request this dashboard signed at 11:50:00 UTC, 10 minutes ago, as its kind, role, times or details don't fit the bot's rules. "
                      + "That happens while the dashboard and the bot run different versions, as after a deploy or a rollback of one of them: ./status.sh on the server "
                      + "shows the commit each runs."
                    : "The bot refused a request as not made by this dashboard at 11:50:00 UTC, 10 minutes ago: something other than the dashboard wrote it, or the bot's "
                      + "request-signing key isn't the dashboard's. ./status.sh --check on the server says which.", banner.Text);
                Assert.Equal(("Those refusals on the Bot page", "/admin/bot?kind=request.rejected"), (banner.LinkText, banner.Link));
            }
        }

        // A refusal of a request the dashboard signed, as a deploy of one app brings, doesn't hide an earlier one in the day of
        // a row it didn't sign: the forger's words come first, at that one's time, and the versions' follow. One older than the
        // day, or one the dashboard signed, leaves the versions' words as they are, and a latest refusal it didn't sign keeps
        // the forger's words alone, at its own time. Fails if only the latest refusal is read.
        [Fact]
        public void AnEarlierUnsignedRefusalOutlastsALaterSignedOne()
        {
            var signed = Event(EventWords.RequestRejected, Now.AddHours(-1), "request 99 (guild.leave) refused: its role may not ask for its kind");
            var forged = Event(EventWords.RequestRejected, Now.AddHours(-3), "requests 97 to 98 refused: their signature didn't match (2 in one minute)");

            foreach (var role in new[] { DashboardRole.Owner, DashboardRole.Admin })
            {
                var banner = Assert.Single(RequestBanners.Of(Nothing with { Rejected = signed, UnsignedRejected = forged }, role, Now));
                Assert.Equal("The bot refused a request as not made by this dashboard at 09:00:00 UTC, 3 hours ago: something other than the dashboard wrote it, or the "
                    + "bot's request-signing key isn't the dashboard's. ./status.sh --check on the server says which. It also refused a request this dashboard signed, at "
                    + "11:00:00 UTC, 1 hour ago, which happens while the dashboard and the bot run different versions: ./status.sh on the server shows the commit each runs.",
                    banner.Text);
                Assert.Equal(("Those refusals on the Bot page", "/admin/bot?kind=request.rejected"), (banner.LinkText, banner.Link));
            }

            var versions = Assert.Single(RequestBanners.Of(Nothing with { Rejected = signed }, DashboardRole.Owner, Now)).Text;
            Assert.StartsWith("The bot refused a request this dashboard signed at 11:00:00 UTC", versions);

            var older = Event(EventWords.RequestRejected, Now.AddDays(-1).AddSeconds(-1), forged.detail);
            var alsoSigned = Event(EventWords.RequestRejected, Now.AddHours(-3), "request 96 refused: its kind isn't on the bot's list");

            foreach (var earlier in new[] { older, alsoSigned })
                Assert.Equal(versions, Assert.Single(RequestBanners.Of(Nothing with { Rejected = signed, UnsignedRejected = earlier }, DashboardRole.Owner, Now)).Text);

            var latest = Assert.Single(RequestBanners.Of(Nothing with
            {
                Rejected = Event(EventWords.RequestRejected, Now.AddMinutes(-10), "request 100 refused: its signature didn't match"),
                UnsignedRejected = forged
            }, DashboardRole.Owner, Now)).Text;
            Assert.StartsWith("The bot refused a request as not made by this dashboard at 11:50:00 UTC, 10 minutes ago:", latest);
            Assert.DoesNotContain("It also refused", latest);
        }

        // The bot's words for its reasons, read from its source, so the two can't drift: the two for a row the dashboard
        // didn't sign each hold words banner 4 looks for, and none of the rest does, for one request or for many
        [Fact]
        public void TheUnsignedReasonsAreTheBotsOwnWords()
        {
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "bot-source", "RequestTexts.cs"));
            var reasons = new[] { "One", "Many" }
                .Select(name => Regex.Match(source, $@"static string {name}\(Rejection reason\) => reason switch\s*\{{(.*?)\}};", RegexOptions.Singleline))
                .SelectMany(body =>
                {
                    Assert.True(body.Success, "The bot's RequestTexts.cs has no One or Many switch over Rejection");
                    return ReasonWords().Matches(body.Groups[1].Value);
                })
                .Select(match => (Reason: match.Groups["reason"].Success ? match.Groups["reason"].Value : "Details", Words: match.Groups["words"].Value))
                .ToList();

            Assert.Equal(14, reasons.Count);

            foreach (var (reason, words) in reasons)
                Assert.True(RequestBanners.UnsignedReasons.Any(unsigned => words.Contains(unsigned, StringComparison.Ordinal)) == (reason is "Form" or "Signature"),
                    $"Banner 4 reads the bot's words for {reason}, \"{words}\", the wrong way");
        }

        // The bot records that its requests are off once a run, and the events carry no run, so only one at or after this
        // run's start counts: one from an earlier run says nothing about this one
        [Fact]
        public void RequestsOffAtTheBotOnlyForThisRun()
        {
            var started = Now.AddHours(-1);

            Assert.Empty(RequestBanners.Of(Nothing with { Status = Status(started), RequestsOff = Event(EventWords.RequestsOff, started.AddSeconds(-1)) }, DashboardRole.Owner, Now));
            Assert.Single(RequestBanners.Of(Nothing with { Status = Status(started), RequestsOff = Event(EventWords.RequestsOff, started) }, DashboardRole.Owner, Now));
            Assert.Single(RequestBanners.Of(Nothing with { Status = Status(started), RequestsOff = Event(EventWords.RequestsOff, started.AddSeconds(3)) }, DashboardRole.Admin, Now));

            // No status row: no run to tell it by
            Assert.Empty(RequestBanners.Of(Nothing with { RequestsOff = Event(EventWords.RequestsOff, Now) }, DashboardRole.Owner, Now));
        }

        // Only the Owner resumes posts, so an Admin's banner says so; and once the request that paused them is gone, after 90
        // days, the banner names the request alone
        [Fact]
        public void ThePausedBannerTellsAnAdminTheOwnerResumes()
        {
            var owner = Assert.Single(RequestBanners.Of(Nothing with { Settings = Paused(), PausedBy = "Test Owner" }, DashboardRole.Owner, Now));
            var admin = Assert.Single(RequestBanners.Of(Nothing with { Settings = Paused(), PausedBy = "Test Owner" }, DashboardRole.Admin, Now));

            Assert.Equal(("Resume them on the controls page", "/admin/bot/controls#posts"), (owner.LinkText, owner.Link));
            Assert.DoesNotContain("The Owner can resume them.", owner.Text);
            Assert.Equal(owner.Text + " The Owner can resume them.", admin.Text);
            Assert.Equal(("The controls page", "/admin/bot/controls#posts"), (admin.LinkText, admin.Link));

            var gone = Assert.Single(RequestBanners.Of(Nothing with { Settings = Paused() }, DashboardRole.Owner, Now));
            Assert.Contains("2 hours ago, on request 11, because: Testing the pause.", gone.Text);

            var resumed = Paused();
            (resumed.posts_paused, resumed.pause_reason) = (false, null);
            Assert.Empty(RequestBanners.Of(Nothing with { Settings = resumed }, DashboardRole.Owner, Now));
        }

        // Only the Owner sets the status text, so an Admin's banner about a stored one the rules refuse says the Owner can, as
        // the paused banner says the Owner resumes, rather than sending them to a control their page doesn't have. Fails if an
        // Admin is told to set one.
        [Fact]
        public void TheStatusTextBannerTellsAnAdminTheOwnerSetsIt()
        {
            var settings = new BotSettingsRow { status_text = "Join x.com/minerbot", status_changed_at = Now, status_request_id = 12, updated_at = Now };

            var owner = Assert.Single(RequestBanners.Of(Nothing with { Settings = settings }, DashboardRole.Owner, Now));
            var admin = Assert.Single(RequestBanners.Of(Nothing with { Settings = settings }, DashboardRole.Admin, Now));

            Assert.EndsWith("(the status text has a link). Set another, or go back to the default, on the controls page.", owner.Text);
            Assert.EndsWith("(the status text has a link). The Owner can set another or go back to the default.", admin.Text);
            Assert.DoesNotContain("Set another", admin.Text);
        }

        // The Overview draws the request banners after its own, by role: a pause and a refused status text for both, and the
        // Owner's failed direct message for the Owner alone. The text the rules refuse is nowhere on it.
        [Fact]
        public async Task TheOverviewShowsTheRequestBanners()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var owner = factory.Visitor("198.51.100.150");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var admin = factory.Visitor("198.51.100.151");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            var pause = await RequestSteps.AddAsync(db, factory, RequestKinds.PostsPause, now.AddMinutes(-5));
            await RequestSteps.ClaimAsync(db, pause, now.AddMinutes(-5).AddSeconds(1));
            await RequestSteps.FinishAsync(db, pause, RequestStates.Done, "Paused posts.", now.AddMinutes(-5).AddSeconds(2));
            await RequestSteps.DmAsync(db, pause, RequestDmStates.Failed);
            await db.Requests().SavePause(true, "Testing the pause", now.AddMinutes(-5), TestUsers.Owner.Id, pause, now.AddMinutes(-5));
            await db.Requests().SaveStatus("Join x.com/minerbot", now.AddMinutes(-4), TestUsers.Owner.Id, pause, now.AddMinutes(-4));

            var ownerPage = await owner.Client.GetStringAsync("/admin", Token);
            var adminPage = await admin.Client.GetStringAsync("/admin", Token);

            foreach (var page in new[] { ownerPage, adminPage })
            {
                Assert.Contains($"posts are paused, since {TimeWords.Utc(now.AddMinutes(-5))}, 5 minutes ago, by Test Owner (request {pause}), because: Testing the pause.", Text(page));
                Assert.Contains("because the stored status text breaks the rules (the status text has a link)", Text(page));
                Assert.DoesNotContain("x.com", page);
                PageTests.AssertAccessible("the Overview with the request banners", page);
            }

            Assert.Contains($"The bot's last direct message to you failed, at {RequestWords.Clock(now.AddMinutes(-5).AddSeconds(2))} (request {pause})", Text(ownerPage));
            Assert.DoesNotContain("direct message", Text(adminPage));
            Assert.Contains("The Owner can resume them.", Text(adminPage));
        }

        // A sign-in's message to the owner is a request with its own audit row beside the sign-in's, but no change, so the
        // Overview's latest changes leave it out, while the audit page lists it. Fails if the Overview shows it.
        [Fact]
        public async Task TheOverviewLeavesSigninAlertsOutOfItsLatestChanges()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("198.51.100.152");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            Assert.Contains(await db.AuditAsync(), entry => entry.action == RequestKinds.OwnerSigninAlert);

            var overview = await owner.Client.GetStringAsync("/admin", Token);
            var changes = Text(Regex.Match(overview, "<h2 id=\"changes\">.*?</section>", RegexOptions.Singleline).Value);
            Assert.Contains("Signed in", changes);
            Assert.DoesNotContain("Asked the bot to tell the owner about a sign-in", changes);

            Assert.Contains("Asked the bot to tell the owner about a sign-in", Text(await owner.Client.GetStringAsync("/admin/audit", Token)));
        }

        // Sign-in messages the bot refused stay a banner for a day, and a problem on the Sessions page, after later sign-ins:
        // every sign-in writes a message, the Owner's own too, so by the time the Owner opens a page the latest is soon one
        // the bot took. Fails if only the latest message is read.
        [Fact]
        public async Task ARefusedSigninMessageOutlastsLaterSignIns()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            const string limit = "The bot takes this at most 20 times an hour.";
            ulong refused = 0;

            foreach (var minutes in new[] { 125, 124 })
            {
                refused = await RequestSteps.AddAsync(db, factory, RequestKinds.OwnerSigninAlert, now.AddMinutes(-minutes), user: TestUsers.Admin);
                await RequestSteps.DecideAsync(db, refused, RequestStates.Refused, limit, now.AddMinutes(-minutes));
            }

            // The Owner signs in two hours on, and the bot takes their message
            var owner = factory.Visitor("198.51.100.153");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var own = (await db.RequestsAsync()).Last();
            Assert.Equal(RequestKinds.OwnerSigninAlert, own.kind);
            await RequestSteps.ClaimAsync(db, own.id, now.AddSeconds(1));
            await RequestSteps.FinishAsync(db, own.id, RequestStates.Done, "Told the owner.", now.AddSeconds(2));

            var banner = $"The bot refused a sign-in message at {RequestWords.Clock(now.AddMinutes(-124))}: {limit} If those sign-ins weren't all yours and your admins', "
                + "check the Sessions page.";
            Assert.Contains(banner, Text(await owner.Client.GetStringAsync("/admin", Token)));
            Assert.Contains(banner, Text(await owner.Client.GetStringAsync("/admin/bot/controls", Token)));

            var sessions = Text(await owner.Client.GetStringAsync("/admin/sessions", Token));
            Assert.Contains($"The latest, request {own.id} at {TimeWords.Utc(own.created_at)}: Done. The bot refused an earlier one, request {refused} at "
                + $"{TimeWords.Utc(now.AddMinutes(-124))}: {limit}", sessions);
        }

        // The pages read the latest refusal of a row the dashboard didn't sign when the bot's latest refusal was of one it
        // signed, as after a deploy of one app: the Overview's and the controls page's banner 4, and the Sessions page's check
        // of requests, name the newer of two such rows first and then the signed one. Fails if they read only the latest.
        [Fact]
        public async Task AnEarlierUnsignedRefusalShowsAfterALaterSignedOne()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            await db.Status().AddEvents(
            [
                StatusSteps.Event(1, now.AddHours(-3), EventWords.RequestRejected, EventLevel.Warning, "request 97 refused: its signature didn't match"),
                StatusSteps.Event(2, now.AddHours(-2), EventWords.RequestRejected, EventLevel.Warning, "request 98 refused: it isn't in the form the dashboard writes"),
                StatusSteps.Event(3, now.AddHours(-1), EventWords.RequestRejected, EventLevel.Warning, "request 99 (guild.leave) refused: its role may not ask for its kind")
            ]);
            var owner = factory.Visitor("198.51.100.234");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            var words = $"The bot refused a request as not made by this dashboard at {RequestWords.Clock(now.AddHours(-2))}, 2 hours ago: something other than the dashboard "
                + $"wrote it, or the bot's request-signing key isn't the dashboard's. ./status.sh --check on the server says which. It also refused a request this dashboard "
                + $"signed, at {RequestWords.Clock(now.AddHours(-1))}, 1 hour ago,";

            foreach (var page in new[] { "/admin", "/admin/bot/controls", "/admin/sessions" })
                Assert.Contains(words, Text(await owner.Client.GetStringAsync(page, Token)));
        }

        // A reason in the bot's One or Many: Rejection.Form => "...", or the default, _ => "...", which is Details
        [GeneratedRegex(@"(?:Rejection\.(?<reason>\w+)|_) => ""(?<words>[^""]*)""")]
        private static partial Regex ReasonWords();
    }
}
