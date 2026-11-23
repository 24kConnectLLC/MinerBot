using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Status;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Tests
{
    // A request's result box and its own page: where the request stands in fixed words, the bot's own words for how it
    // ended, the page's own clock deciding what the bot can no longer answer, and the fragment refresh.js looks at, with its
    // marker and when to look next, set only on a box the read gave
    [Collection("Database")]
    public partial class RequestPageTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static readonly DateTime Noon = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        const ulong Run = RequestSteps.Run, NewRun = 7;

        static RequestKind Kind(string word) => RequestKinds.Find(word)!;

        // A request asked for at noon by the Owner, as the reads give it, with what the bot wrote
        static RequestPageRow Row(string kind, string? state = null, double? claimed = null, double? ended = null, string? result = null, ulong? number = null,
            string? dm = null) => new()
        {
            id = 41,
            kind = kind,
            actor_id = TestUsers.Owner.Id,
            actor_name = "Test Owner",
            actor_role = RequestRoles.Owner,
            guild_id = Kind(kind).NeedsServer ? RequestSteps.ServerId : null,
            channel_id = Kind(kind).NeedsChannel ? RequestSteps.ChannelId : null,
            created_at = Noon,
            expires_at = Noon + Kind(kind).Expiry,
            state = state,
            run_id = state == null ? null : Run,
            claimed_at = claimed is { } taken ? Noon.AddSeconds(taken) : null,
            ended_at = ended is { } at ? Noon.AddSeconds(at) : null,
            result = result,
            result_number = number,
            dm_state = dm
        };

        // The bot's status row: up on Discord unless given otherwise, with the run's start and its connection
        static BotStatusRow StatusRow(DateTime now, ulong run = Run, string discord = DiscordState.Connected, double? since = null, DateTime? stopped = null) => new()
        {
            run_id = run,
            started_at = since is { } connected ? Noon.AddSeconds(connected - 5) : now.AddHours(-2),
            heartbeat_at = now.AddSeconds(-5),
            stopped_at = stopped,
            discord_state = discord,
            discord_since = since is { } at ? Noon.AddSeconds(at) : now.AddHours(-2),
            steam_state = SteamState.LoggedOn
        };

        static RequestView View(RequestPageRow row, DateTime now, BotStatusRow? status = null, BotEventRow? connected = null) =>
            RequestViews.Of(row, status, now, RequestViews.Waits, connected);

        // The bot's record of a connection to Discord at noon and the seconds given, as the reads give it
        static BotEventRow Connected(double seconds) => new() { id = 9, at = Noon.AddSeconds(seconds), kind = EventWords.DiscordConnected, level = EventLevel.Info };

        static Task<HttpResponseMessage> LookAsync(TestBrowser browser, ulong id) => LookAsync(browser, $"/admin/requests/{id}?handler=result");

        static Task<HttpResponseMessage> LookAsync(TestBrowser browser, string path)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add(BackgroundRefresh.Header, BackgroundRefresh.Value);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            return browser.Client.SendAsync(request, Token);
        }

        static bool HasMarkers(HttpResponseMessage response) =>
            response.Headers.Contains(BackgroundRefresh.FragmentHeader) || response.Headers.Contains(BackgroundRefresh.NextHeader);

        static string? Next(HttpResponseMessage response) => response.Headers.TryGetValues(BackgroundRefresh.NextHeader, out var values) ? values.Single() : null;

        // The page's text without its markup, so a sentence reads as one string
        static string Text(string html) => Spaces().Replace(Tags().Replace(WebUtility.HtmlDecode(html), " "), " ");

        // Each state in its own words, as the request's page draws it, from rows the dashboard wrote and the bot answered:
        // waiting, taken, done, failed, refused, expired, not sure, and a row whose time can't be read. Only a box that can
        // still change loads refresh.js.
        [Fact]
        public async Task EachStateIsShownInItsOwnWords()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.200");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var waiting = await RequestSteps.AddAsync(db, factory, RequestKinds.CachesClear, now);
            var running = await RequestSteps.TakenAsync(db, factory, RequestKinds.JobColors, now.AddSeconds(-5));
            var done = await RequestSteps.TakenAsync(db, factory, RequestKinds.CachesClear, now.AddSeconds(-10));
            await RequestSteps.FinishAsync(db, done, RequestStates.Done, "Cleared the armor-name and latest-maps caches.", now.AddSeconds(-8));
            var failed = await RequestSteps.TakenAsync(db, factory, RequestKinds.SteamRenew, now.AddSeconds(-20));
            await RequestSteps.FinishAsync(db, failed, RequestStates.Failed, "Steam didn't renew the token (TimeoutException).", now.AddSeconds(-18));
            await RequestSteps.DmAsync(db, failed, RequestDmStates.Sent);
            var refused = await RequestSteps.AddAsync(db, factory, RequestKinds.BotRestart, now.AddSeconds(-30));
            await RequestSteps.DecideAsync(db, refused, RequestStates.Refused, "The bot takes a restart only 5 minutes after it starts.", now.AddSeconds(-29));
            var expired = await RequestSteps.AddAsync(db, factory, RequestKinds.JobNotifications, now.AddMinutes(-3));
            await RequestSteps.DecideAsync(db, expired, RequestStates.Expired, "The bot saw this request only after it expired.", now.AddMinutes(-1));
            var unknown = await RequestSteps.TakenAsync(db, factory, RequestKinds.PostsPause, now.AddMinutes(-2));
            await RequestSteps.FinishAsync(db, unknown, RequestStates.Unknown, "The bot stopped while this ran, so it may or may not have happened.", now.AddMinutes(-1));
            await RequestSteps.DmAsync(db, unknown, RequestDmStates.Failed);
            var unreadable = await RequestSteps.AddAsync(db, factory, RequestKinds.CachesClear, now);
            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.RequestTable} SET created_at = '2027-00-10 00:00:00.000' WHERE id = @unreadable", new { unreadable });

            var expected = new (ulong Id, string Words, bool Refreshes)[]
            {
                (waiting, $"Queued Queued at {RequestWords.Clock(now)}. MinerBot isn't on Discord now, so it waits", true),
                (running, $"Running Taken at {RequestWords.Clock(now.AddSeconds(-4))}; the bot is acting on it.", true),
                (done, $"Done Done at {RequestWords.Clock(now.AddSeconds(-8))}: Cleared the armor-name and latest-maps caches.", false),
                (failed, $"Failed Failed at {RequestWords.Clock(now.AddSeconds(-18))}: Steam didn't renew the token (TimeoutException). The owner was sent a direct message about it.", false),
                (refused, $"Refused Refused at {RequestWords.Clock(now.AddSeconds(-29))}: The bot takes a restart only 5 minutes after it starts.", false),
                (expired, "Expired Expired: The bot saw this request only after it expired.", false),
                (unknown, $"Not sure it happened Not sure it happened, at {RequestWords.Clock(now.AddMinutes(-1))}: The bot stopped while this ran, so it may or may not have happened. The direct message to the owner failed.", false),
                (unreadable, "asked for by Test Owner (Owner) at an unreadable time. Can't follow This request holds a time MinerBot's database can't hand back", false)
            };

            foreach (var (id, words, refreshes) in expected)
            {
                var response = await owner.Client.GetAsync($"/admin/requests/{id}", Token);
                var html = await response.Content.ReadAsStringAsync(Token);

                Assert.True(response.StatusCode == HttpStatusCode.OK, $"Request {id}'s page answered {response.StatusCode}");
                Assert.Contains($"<h1>Bot request {id}</h1>", html);
                Assert.Contains(words, Text(html));
                Assert.Equal(refreshes, PageTests.LoadsRefreshScript(html));
                Assert.Equal(refreshes, html.Contains("data-refresh=", StringComparison.Ordinal));

                // A box that refreshes has a pace of its own, every 2 seconds while the request is young, which is also what
                // refresh.js caps at 15 minutes
                Assert.Equal(refreshes, html.Contains($"data-refresh-every=\"{RequestViews.SoonSeconds}\"", StringComparison.Ordinal));

                // Without the script, Check again is the next look: the page's own address, a GET, without a fragment, which a
                // link to the address the browser shows would only scroll to
                Assert.Equal(refreshes, html.Contains($"<a href=\"/admin/requests/{id}\">Check again</a>", StringComparison.Ordinal));

                if (RequestSteps.CheckAgainLink(html) is { } link)
                {
                    Assert.DoesNotContain('#', link);
                    Assert.True(RequestSteps.LoadsAgain($"/admin/requests/{id}", link), $"Check again on request {id}'s page is {link}, which only scrolls");
                }

                PageTests.AssertAccessible($"request {id}'s page", html);
            }
        }

        // Queued until 20 seconds past its expiry, since the bot may have taken it just in time, as request.sh waits; then
        // expired by the page's own clock, and final. While the bot isn't on Discord it says it waits, and a restart says the
        // bot takes it anyway while it runs, so a restart that expired says the bot wasn't running, never that it was off
        // Discord. Fails if a queued request never expires, or expires the moment it's due.
        [Fact]
        public void AQueuedRequestPastItsExpiryIsShownExpiredAfterTheGrace()
        {
            var clear = Row(RequestKinds.CachesClear);
            var due = Noon.AddMinutes(2);

            var queued = View(clear, due.AddSeconds(19), StatusRow(due));
            Assert.Equal((RequestStage.Queued, "Queued", "marker-quiet", false), (queued.Stage, queued.StageLabel, queued.Marker, queued.Final));
            Assert.Equal("Queued at 12:00:00 UTC. The bot looks for requests every 3 seconds.", queued.Says);

            var expired = View(clear, due.AddSeconds(21), StatusRow(due));
            Assert.Equal((RequestStage.Expired, "Expired", "marker-warn", true), (expired.Stage, expired.StageLabel, expired.Marker, expired.Final));
            Assert.Equal("Expired at 12:02:00 UTC without the bot taking it, so nothing happened. The bot takes requests only while it runs and is up on Discord: "
                + "the Bot page says what it was doing.", expired.Says);

            var off = View(clear, Noon.AddSeconds(5), StatusRow(Noon, discord: DiscordState.Disconnected));
            Assert.Equal("Queued at 12:00:00 UTC. MinerBot isn't on Discord now, so it waits, and it expires at 12:02:00 UTC if the bot isn't back by then.", off.Says);
            Assert.Equal(off.Says, View(clear, Noon.AddSeconds(5)).Says);
            Assert.Equal(off.Says, View(clear, Noon.AddMinutes(1), StatusRow(Noon.AddSeconds(-90))).Says);

            var restart = View(Row(RequestKinds.BotRestart), Noon.AddSeconds(5), StatusRow(Noon, discord: DiscordState.Disconnected));
            Assert.Equal("Queued at 12:00:00 UTC. Restarts are taken even while the bot is off Discord, as long as it runs.", restart.Says);

            var restartDue = Noon + Kind(RequestKinds.BotRestart).Expiry;
            var restartExpired = View(Row(RequestKinds.BotRestart), restartDue.AddSeconds(21), StatusRow(Noon, discord: DiscordState.Disconnected));
            Assert.Equal((RequestStage.Expired, true), (restartExpired.Stage, restartExpired.Final));
            Assert.Equal($"Expired at {RequestWords.Clock(restartDue)} without the bot taking it, so nothing happened. The bot takes a restart even while it's off "
                + "Discord, as long as it runs: the Bot page says whether it was running.", restartExpired.Says);

            // Every 2 seconds while the request is under 20 seconds old, then every 5
            Assert.Equal(2, View(clear, Noon.AddSeconds(19), StatusRow(Noon)).Next);
            Assert.Equal(5, View(clear, Noon.AddSeconds(21), StatusRow(Noon)).Next);
        }

        // Taken, it runs until its kind's action limit and 30 seconds have passed; then there's no answer, which is final.
        // Fails if a running request never ends.
        [Fact]
        public void ARunningRequestPastItsLimitIsNoAnswer()
        {
            var clear = Row(RequestKinds.CachesClear, RequestStates.Running, claimed: 1);

            var running = View(clear, Noon.AddSeconds(1 + 5 + 29));
            Assert.Equal((RequestStage.Running, "Running", "marker-quiet", false), (running.Stage, running.StageLabel, running.Marker, running.Final));
            Assert.Equal("Taken at 12:00:01 UTC; the bot is acting on it.", running.Says);

            var late = View(clear, Noon.AddSeconds(1 + 5 + 31));
            Assert.Equal((RequestStage.NoAnswer, "No answer", "marker-warn", true), (late.Stage, late.StageLabel, late.Marker, late.Final));
            Assert.Equal("No answer: the bot took it at 12:00:01 UTC and it's past its time. If the bot stopped while it ran, its next start marks it as may or may not "
                + "have happened. Reload to see.", late.Says);

            // A job has a minute
            var job = Row(RequestKinds.JobNotifications, RequestStates.Running, claimed: 1);
            Assert.Equal(RequestStage.Running, View(job, Noon.AddSeconds(1 + 60 + 29)).Stage);
            Assert.Equal(RequestStage.NoAnswer, View(job, Noon.AddSeconds(1 + 60 + 31)).Stage);
        }

        // A restart that's done waits for the bot to be back on Discord: a new run, connected and not stopping, as request.sh
        // tells it. It was back at the bot's own record of that run's first connection, exact, or until that's written, by
        // the status row's 5-second sample, with no seconds, since that's only the latest it can have been. Then it waits to
        // hear whether its direct message went, which the new run sends once it's up when the old one couldn't, a few seconds
        // after its status row says so: 45 seconds from the row's time, never the record's earlier one, then it says what's
        // recorded. Not back 6 minutes after it ended, it says where to look. Fails if the box ends at once, takes the old run
        // for the new one, says the sample's time is exact, takes an earlier run's connection, or ends while it still waits
        // to hear of the message.
        [Fact]
        public void ARestartShowsWhenTheBotIsBack()
        {
            var restart = Row(RequestKinds.BotRestart, RequestStates.Done, claimed: 1, ended: 2, result: "The bot is restarting. It's usually back on Discord within a minute.");
            var now = Noon.AddSeconds(30);

            foreach (var status in new BotStatusRow?[] { StatusRow(now), StatusRow(now, run: NewRun, discord: DiscordState.Connecting, since: 25),
                StatusRow(now, run: NewRun, since: 25, stopped: now), null })
            {
                var waiting = View(restart, now, status, Connected(17));
                Assert.Equal(("Waiting for the bot to come back on Discord.", false, 5), (waiting.Then!, waiting.Final, waiting.Next));
                Assert.Equal("Done at 12:00:02 UTC: The bot is restarting. It's usually back on Discord within a minute.", waiting.Says);
            }

            var back = View(restart, now, StatusRow(now, run: NewRun, since: 20));
            Assert.Equal("Back on Discord by 12:00:20 UTC: its new run started at 12:00:15 UTC.", back.Then);
            Assert.Equal(("Waiting to hear whether the direct message to the owner went.", false, 5), (back.Dm!, back.Final, back.Next));

            // At the bot's record of the new run's first connection; never an earlier run's, from before its start; and at
            // the sample when the sample read it first
            Assert.Equal("Back on Discord at 12:00:17 UTC, 17 s after the request: its new run started at 12:00:15 UTC.",
                View(restart, now, StatusRow(now, run: NewRun, since: 20), Connected(17)).Then);
            Assert.Equal(back.Then, View(restart, now, StatusRow(now, run: NewRun, since: 20), Connected(14)).Then);
            Assert.Equal("Back on Discord at 12:00:20 UTC, 20 s after the request: its new run started at 12:00:15 UTC.",
                View(restart, now, StatusRow(now, run: NewRun, since: 20), Connected(20.010)).Then);

            // A row without a sample's time and the record: the record's time
            var unsampled = StatusRow(now, run: NewRun, since: 20);
            unsampled.discord_since = null;
            Assert.Equal("Back on Discord at 12:00:17 UTC, 17 s after the request: its new run started at 12:00:15 UTC.", View(restart, now, unsampled, Connected(17)).Then);

            var told = View(Row(RequestKinds.BotRestart, RequestStates.Done, claimed: 1, ended: 2, result: "Restarting.", dm: RequestDmStates.Sent), now,
                StatusRow(now, run: NewRun, since: 20));
            Assert.Equal(("The owner was sent a direct message about it.", true), (told.Dm!, told.Final));

            // Back over 45 seconds after the bot ended it: the wait counts from when the new run's status row says it was back,
            // with the record or without it
            var slow = StatusRow(Noon.AddSeconds(70), run: NewRun, since: 65);
            var hearing = View(restart, Noon.AddSeconds(70), slow);
            Assert.Equal(("Waiting to hear whether the direct message to the owner went.", false), (hearing.Dm!, hearing.Final));
            Assert.False(View(restart, Noon.AddSeconds(65 + 45), slow).Final);
            Assert.False(View(restart, Noon.AddSeconds(65 + 45), slow, Connected(62)).Final);

            var unheard = View(restart, Noon.AddSeconds(65 + 46), slow);
            Assert.Equal(("No direct message to the owner is recorded.", true), (unheard.Dm!, unheard.Final));
            Assert.StartsWith("Back on Discord by 12:01:05 UTC: its new run started", unheard.Then);

            var unheardExact = View(restart, Noon.AddSeconds(65 + 46), slow, Connected(62));
            Assert.Equal(("No direct message to the owner is recorded.", true), (unheardExact.Dm!, unheardExact.Final));
            Assert.StartsWith("Back on Discord at 12:01:02 UTC, 62 s after the request", unheardExact.Then);

            Assert.False(View(restart, Noon.AddSeconds(2) + TimeSpan.FromMinutes(6), StatusRow(now)).Final);

            var notBack = View(restart, Noon.AddSeconds(3) + TimeSpan.FromMinutes(6), StatusRow(now));
            Assert.Equal(("The bot isn't back on Discord 6 minutes after it restarted. ./status.sh on the server says whether it runs.", true), (notBack.Then!, notBack.Final));

            // A restart the bot refused or failed has nothing to come back from
            Assert.Null(View(Row(RequestKinds.BotRestart, RequestStates.Refused, ended: 1, result: "Refused"), now).Then);
        }

        // The request's own page, and ?request= on the controls page, draw a restart's box any time later, as the audit log's
        // link does, while the bot's status row is only ever the bot as it is now. Past the 6 minutes the row tells how the
        // restart went only while the new run's state on Discord dates from within them: up on Discord since then, it's back
        // as it was; in one state off Discord since then, or still the old run, or no row, it isn't, stopped since or not. A
        // reconnect, a moment off Discord or a later run since, or the stop of a run that was up, says nothing of it, so the
        // box says where to look, and no longer waits to hear of the direct message. Fails if any of those is taken for the
        // restart's own return, or for a restart that never came back, or if a stop hides a restart that never came back.
        [Fact]
        public void ARestartSeenLaterSaysOnlyWhatTheStatusStillShows()
        {
            var restart = Row(RequestKinds.BotRestart, RequestStates.Done, claimed: 1, ended: 2, result: "Restarting.", dm: RequestDmStates.Sent);
            var later = Noon.AddDays(1);
            const string Moved = "The bot's status has changed since this restart, so it no longer shows whether and when the bot was back on Discord: the Bot page's events do.";
            const string NotBack = "The bot isn't back on Discord 6 minutes after it restarted. ./status.sh on the server says whether it runs.";

            // Back 20 seconds after the request, and up on Discord since: the line it ended with on the day, by the sample, or
            // at the bot's record of the run's first connection
            var up = View(restart, later, StatusRow(later, run: NewRun, since: 20));
            Assert.Equal(("Back on Discord by 12:00:20 UTC: its new run started at 12:00:15 UTC.", true), (up.Then!, up.Final));
            var recorded = View(restart, later, StatusRow(later, run: NewRun, since: 20), Connected(17));
            Assert.Equal(("Back on Discord at 12:00:17 UTC, 17 s after the request: its new run started at 12:00:15 UTC.", true), (recorded.Then!, recorded.Final));

            // The same run reconnected an hour ago, off Discord for the last 20 seconds, or stopped an hour ago, and a later run
            // since a deploy 3 hours ago
            var reconnected = StatusRow(later, run: NewRun, since: 20);
            reconnected.discord_since = later.AddHours(-1);
            var dropped = StatusRow(later, run: NewRun, discord: DiscordState.Disconnected, since: 20);
            dropped.discord_since = later.AddSeconds(-20);
            var deployed = StatusRow(later, run: NewRun + 1);
            (deployed.started_at, deployed.discord_since) = (later.AddHours(-3), later.AddHours(-3).AddSeconds(2));

            foreach (var status in new[] { reconnected, dropped, StatusRow(later, run: NewRun, since: 20, stopped: later.AddHours(-1)), deployed })
            {
                var box = View(restart, later, status);
                Assert.Equal((Moved, "The owner was sent a direct message about it.", true), (box.Then!, box.Dm!, box.Final));
            }

            // Whether it's within them goes by the status row's time, so the record of the run's first connection doesn't make
            // a reconnect since the restart's own return
            Assert.Equal(Moved, View(restart, later, reconnected, Connected(17)).Then);

            // Still the old run, up on Discord since before the request, stopped after it, or with a state from since, no row,
            // and a new run that hasn't been on Discord since before the 6 minutes ended, running, or stopped within them or
            // an hour ago
            foreach (var status in new BotStatusRow?[] { StatusRow(Noon), StatusRow(Noon, stopped: Noon.AddSeconds(3)), StatusRow(later), null,
                StatusRow(later, run: NewRun, discord: DiscordState.Connecting, since: 25),
                StatusRow(later, run: NewRun, discord: DiscordState.Connecting, since: 25, stopped: Noon.AddMinutes(3)),
                StatusRow(later, run: NewRun, discord: DiscordState.Disconnected, since: 25, stopped: later.AddHours(-1)) })
            {
                var box = View(restart, later, status);
                Assert.Equal((NotBack, true), (box.Then!, box.Final));
            }

            // A reconnect 10 seconds ago isn't the wait to hear of the direct message, of which none is recorded
            var moment = StatusRow(later, run: NewRun, since: 20);
            moment.discord_since = later.AddSeconds(-10);
            var unheard = View(Row(RequestKinds.BotRestart, RequestStates.Done, claimed: 1, ended: 2, result: "Restarting."), later, moment);
            Assert.Equal((Moved, "No direct message to the owner is recorded.", true), (unheard.Then!, unheard.Dm!, unheard.Final));
        }

        // When a new run was back on Discord, for the box and request.sh alike: the bot's record of the run's first
        // connection, from its start on, is exact, or the sample's time when that's earlier; without one, or with an earlier
        // run's, the sample's time, which isn't. Fails if the sample is taken as exact, or an earlier run's connection counts.
        [Fact]
        public void BackOnDiscordIsTheNewRunsFirstConnection()
        {
            // Started at 12:00:15, and sampled up on Discord at 12:00:20
            var status = StatusRow(Noon, run: NewRun, since: 20);

            Assert.Equal((Noon.AddSeconds(20), false), RequestViews.BackOnDiscord(status, null));
            Assert.Equal((Noon.AddSeconds(20), false), RequestViews.BackOnDiscord(status, Connected(14.999)));
            Assert.Equal((Noon.AddSeconds(15), true), RequestViews.BackOnDiscord(status, Connected(15)));
            Assert.Equal((Noon.AddSeconds(17), true), RequestViews.BackOnDiscord(status, Connected(17)));
            Assert.Equal((Noon.AddSeconds(20), true), RequestViews.BackOnDiscord(status, Connected(20.010)));

            // A row without the sample's time: the record's, or the run's start, not exact
            status.discord_since = null;
            Assert.Equal((Noon.AddSeconds(17), true), RequestViews.BackOnDiscord(status, Connected(17)));
            Assert.Equal((Noon.AddSeconds(15), false), RequestViews.BackOnDiscord(status, null));
        }

        // The box reads the bot's record of the new run's first connection only for a done restart whose status row is a new
        // run's, up on Discord and not stopped: once, from that run's start, and through the guard, as every read. Never for
        // the old run's row, a run that's stopped or not on Discord, no row, a restart that isn't done, or another kind. Fails
        // if the record is read for every box, from anywhere but the run's start, or a read that fails isn't the dashboard's
        // own answer.
        [Fact]
        public async Task TheConnectionIsReadOnlyForADoneRestartsNewRun()
        {
            var now = Noon.AddSeconds(30);
            var restart = Row(RequestKinds.BotRestart, RequestStates.Done, claimed: 1, ended: 2, result: "Restarting.");

            async Task<(RequestView View, List<BotEventRow?> Asked)> LoadAsync(RequestPageRow row, ConnectionReads reads)
            {
                var box = await new RequestBoxes(new OneRequest(row), reads, new StoppedClock(now), NullLogger<RequestBoxes>.Instance).LoadAsync(row.id);
                return (box!.View, reads.Asked);
            }

            var (back, asked) = await LoadAsync(restart, new ConnectionReads(StatusRow(now, run: NewRun, since: 20), Connected(17)));
            var from = Assert.Single(asked);
            Assert.Equal((Noon.AddSeconds(15), 0UL), (from!.at, from.id));
            Assert.Equal("Back on Discord at 12:00:17 UTC, 17 s after the request: its new run started at 12:00:15 UTC.", back.Then);

            foreach (var (row, status) in new (RequestPageRow, BotStatusRow?)[]
            {
                (restart, StatusRow(now)),
                (restart, StatusRow(now, run: NewRun, since: 20, stopped: now)),
                (restart, StatusRow(now, run: NewRun, discord: DiscordState.Connecting, since: 25)),
                (restart, null),
                (Row(RequestKinds.BotRestart), StatusRow(now, run: NewRun, since: 20)),
                (Row(RequestKinds.BotRestart, RequestStates.Failed, claimed: 1, ended: 2, result: "Failed."), StatusRow(now, run: NewRun, since: 20)),
                (Row(RequestKinds.CachesClear), StatusRow(now, run: NewRun, since: 20)),
                (Row(RequestKinds.CachesClear, RequestStates.Done, claimed: 1, ended: 2, result: "Cleared."), StatusRow(now, run: NewRun, since: 20))
            })
                Assert.Empty((await LoadAsync(row, new ConnectionReads(status, Connected(17)))).Asked);

            await Assert.ThrowsAsync<DashboardUnavailableException>(() => LoadAsync(restart, new ConnectionReads(StatusRow(now, run: NewRun, since: 20), fails: true)));
        }

        // Only the box refreshes, so once a pause, a resume, a status text or a leave is done, the page under it still shows
        // the state before, with the form for that state, as a refresh's page still shows the old copy: each box says to
        // reload, once the bot has done it. A leave is done once Discord answers the bot, before MinerBot's copy marks the
        // server as left at the bot's next write of its servers, so its box says to reload a few seconds later. Fails if a
        // done change leaves the page under the box to say the opposite, or a leave's box promises the copy has it at once.
        [Fact]
        public void ADoneChangeToWhatThePageShowsSaysToReloadIt()
        {
            var now = Noon.AddSeconds(30);

            foreach (var (kind, then) in new[]
            {
                (RequestKinds.PostsPause, "Reload the page to see the new settings."),
                (RequestKinds.PostsResume, "Reload the page to see the new settings."),
                (RequestKinds.StatusSet, "Reload the page to see the new settings."),
                (RequestKinds.GuildLeave, "MinerBot's copy marks the server as left a few seconds later: reload the page then to see it."),
                (RequestKinds.GuildRefresh, "Reload the page to see the new copy.")
            })
            {
                Assert.Equal(then, View(Row(kind, RequestStates.Done, claimed: 1, ended: 2, result: "Done."), now).Then);

                // Nothing changed yet, or nothing changed
                Assert.Null(View(Row(kind), now).Then);
                Assert.Null(View(Row(kind, RequestStates.Running, claimed: 1), now).Then);
                Assert.Null(View(Row(kind, RequestStates.Failed, claimed: 1, ended: 2, result: "Failed."), now).Then);
                Assert.Null(View(Row(kind, RequestStates.Refused, ended: 1, result: "Refused."), now).Then);
            }

            Assert.Null(View(Row(RequestKinds.CachesClear, RequestStates.Done, claimed: 1, ended: 2, result: "Cleared."), now).Then);
        }

        // The request's own page shows no settings, server or copy, so its box leaves out the line that says to reload the page
        // to see them, which reloading it, then or long after through the audit log's link, would only show again; the rest of
        // the box is the same, and its fragment is the one for this page. A restart's line follows the bot, so it stays.
        // Fails if the request's own page keeps a line that sends the reader to reload it.
        [Fact]
        public void TheRequestsOwnPageLeavesOutTheLineToReloadIt()
        {
            var now = Noon.AddSeconds(30);

            foreach (var kind in new[] { RequestKinds.PostsPause, RequestKinds.PostsResume, RequestKinds.StatusSet, RequestKinds.GuildLeave, RequestKinds.GuildRefresh })
            {
                var sent = View(Row(kind, RequestStates.Done, claimed: 1, ended: 2, result: "Done."), now);
                var own = RequestViews.OnItsOwnPage(sent);

                Assert.NotNull(sent.Then);
                Assert.Null(own.Then);
                Assert.Equal(sent with { Then = null, OwnPage = true }, own);
                Assert.Equal(("/admin/requests/41?handler=result", "/admin/requests/41?handler=result&own=1"), (sent.FragmentAddress, own.FragmentAddress));
            }

            var restart = Row(RequestKinds.BotRestart, RequestStates.Done, claimed: 1, ended: 2, result: "Restarting.");

            foreach (var (status, connected) in new (BotStatusRow?, BotEventRow?)[] { (StatusRow(now), null), (StatusRow(now, run: NewRun, since: 20), null),
                (StatusRow(now, run: NewRun, since: 20), Connected(17)) })
            {
                var box = View(restart, now, status, connected);
                Assert.NotNull(box.Then);
                Assert.Equal(box.Then, RequestViews.OnItsOwnPage(box).Then);
            }

            Assert.Equal(View(restart, Noon.AddSeconds(3) + TimeSpan.FromMinutes(6), StatusRow(now)).Then,
                RequestViews.OnItsOwnPage(View(restart, Noon.AddSeconds(3) + TimeSpan.FromMinutes(6), StatusRow(now))).Then);
        }

        // The same through the app: the request's own page, and the fragment its box refreshes from, leave out the line to
        // reload, while the box on the page that sent it, and the fragment refresh.js gets there, keep it, at the same pace.
        // Fails if the request's own page, drawn or refreshed, says to reload it to see what it doesn't show.
        [Fact]
        public async Task TheRequestsOwnPageNeverSaysToReloadIt()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.214");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var pause = await RequestSteps.TakenAsync(db, factory, RequestKinds.PostsPause, now.AddSeconds(-10));
            await RequestSteps.FinishAsync(db, pause, RequestStates.Done, "Posts are paused.", now.AddSeconds(-8));

            var page = await owner.Client.GetStringAsync($"/admin/requests/{pause}", Token);
            Assert.Contains($"Done at {RequestWords.Clock(now.AddSeconds(-8))}: Posts are paused.", Text(page));
            Assert.DoesNotContain(RequestViews.ReloadForSettings, Text(page));
            Assert.Contains($"data-refresh=\"/admin/requests/{pause}?handler=result&amp;own=1\"", page);

            var own = await LookAsync(owner, $"/admin/requests/{pause}?handler=result&own=1");
            var ownBox = Text(await own.Content.ReadAsStringAsync(Token));
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            Assert.Contains("Posts are paused.", ownBox);
            Assert.DoesNotContain(RequestViews.ReloadForSettings, ownBox);

            var sent = await LookAsync(owner, pause);
            Assert.Contains(RequestViews.ReloadForSettings, Text(await sent.Content.ReadAsStringAsync(Token)));
            Assert.Equal(("2", "2"), (Next(sent), Next(own)));

            var controls = await owner.Client.GetStringAsync($"/admin/bot/controls?request={pause}", Token);
            Assert.Contains(RequestViews.ReloadForSettings, Text(controls));
            Assert.Contains($"data-refresh=\"/admin/requests/{pause}?handler=result\"", controls);

            foreach (var (kind, result, reload) in new[]
            {
                (RequestKinds.GuildLeave, "Left the server.", RequestViews.ReloadForLeave),
                (RequestKinds.GuildRefresh, "Rewrote the copy of this server and its channels.", RequestViews.ReloadForCopy)
            })
            {
                var id = await RequestSteps.TakenAsync(db, factory, kind, now.AddSeconds(-10));
                await RequestSteps.FinishAsync(db, id, RequestStates.Done, result, now.AddSeconds(-8));

                var text = Text(await owner.Client.GetStringAsync($"/admin/requests/{id}", Token));
                Assert.Contains(result, text);
                Assert.DoesNotContain(reload, text);
                Assert.DoesNotContain(reload, Text(await (await LookAsync(owner, $"/admin/requests/{id}?handler=result&own=1")).Content.ReadAsStringAsync(Token)));
                Assert.Contains(reload, Text(await (await LookAsync(owner, id)).Content.ReadAsStringAsync(Token)));
            }
        }

        // A kind or a state this version doesn't know, as a later version writes them and a rollback of the dashboard leaves
        // them: the stored word as the label, a state as it's stored, with the bot's words, final; a running one timed by the
        // longest action any kind has; and no word about a direct message, which only the kinds known to send one get. Fails
        // if an unknown kind or state throws, or is taken for one this version knows.
        [Fact]
        public void ALaterKindOrStateIsShownAsStored()
        {
            var longest = RequestKinds.All.Max(kind => kind.ActionLimit);

            RequestPageRow Later(string? state, double? claimed = null, double? ended = null, string? dm = null) => new()
            {
                id = 41,
                kind = "guild.announce",
                actor_id = TestUsers.Owner.Id,
                actor_name = "Test Owner",
                actor_role = RequestRoles.Owner,
                guild_id = RequestSteps.ServerId,
                created_at = Noon,
                expires_at = Noon.AddMinutes(2),
                state = state,
                run_id = state == null ? null : Run,
                claimed_at = claimed is { } taken ? Noon.AddSeconds(taken) : null,
                ended_at = ended is { } at ? Noon.AddSeconds(at) : null,
                result = ended == null ? null : "Announced.",
                dm_state = dm
            };

            var done = View(Later(RequestStates.Done, claimed: 1, ended: 2, dm: RequestDmStates.Sent), Noon.AddSeconds(3));
            Assert.Equal(("guild.announce", "Done", "Done at 12:00:02 UTC: Announced.", true), (done.Label, done.StageLabel, done.Says, done.Final));
            Assert.Null(done.Dm);
            Assert.Null(done.Then);

            var queued = View(Later(null), Noon.AddSeconds(5), StatusRow(Noon));
            Assert.Equal(("guild.announce", RequestStage.Queued, false), (queued.Label, queued.Stage, queued.Final));

            var running = Later(RequestStates.Running, claimed: 1);
            var still = View(running, Noon.AddSeconds(1) + longest + TimeSpan.FromSeconds(29));
            Assert.Equal((RequestStage.Running, false), (still.Stage, still.Final));

            var late = View(running, Noon.AddSeconds(1) + longest + TimeSpan.FromSeconds(31));
            Assert.Equal((RequestStage.NoAnswer, true), (late.Stage, late.Final));

            var cancelled = View(Row(RequestKinds.CachesClear, "cancelled", claimed: 1, ended: 2, result: "Announced."), Noon.AddSeconds(3));
            Assert.Equal(("cancelled", "marker-warn", "cancelled: Announced.", true), (cancelled.StageLabel, cancelled.Marker, cancelled.Says, cancelled.Final));
            Assert.Null(cancelled.Then);
        }

        // The Owner's controls and the sign-in alerts tell the owner by direct message once the bot has acted: the box waits
        // up to 45 seconds to hear whether it went, then says what's recorded. Fails if the wait is dropped.
        [Fact]
        public void OwnerKindsWaitForTheirDirectMessage()
        {
            var pause = Row(RequestKinds.PostsPause, RequestStates.Done, claimed: 1, ended: 2, result: "Posts are paused.");

            var soon = View(pause, Noon.AddSeconds(2 + 44));
            Assert.Equal(("Waiting to hear whether the direct message to the owner went.", false), (soon.Dm!, soon.Final));

            var later = View(pause, Noon.AddSeconds(2 + 46));
            Assert.Equal(("No direct message to the owner is recorded.", true), (later.Dm!, later.Final));

            var sent = View(Row(RequestKinds.PostsPause, RequestStates.Done, claimed: 1, ended: 2, result: "Posts are paused.", dm: RequestDmStates.Sent), Noon.AddSeconds(3));
            Assert.Equal(("The owner was sent a direct message about it.", true), (sent.Dm!, sent.Final));

            var failed = View(Row(RequestKinds.SteamReconnect, RequestStates.Failed, claimed: 1, ended: 2, result: "Steam isn't logged on.", dm: RequestDmStates.Failed),
                Noon.AddSeconds(3));
            Assert.Equal(("The direct message to the owner failed. Discord refuses MinerBot's messages while the owner doesn't allow direct messages from members of a "
                + "server they share with MinerBot; the Bot page's events say how it failed.", true), (failed.Dm!, failed.Final));

            Assert.False(View(Row(RequestKinds.OwnerSigninAlert, RequestStates.Done, claimed: 1, ended: 2, result: "Told the owner."), Noon.AddSeconds(10)).Final);

            // A cache clear tells nobody, and a refused Owner control did nothing to tell
            var clear = View(Row(RequestKinds.CachesClear, RequestStates.Done, claimed: 1, ended: 2, result: "Cleared."), Noon.AddSeconds(3));
            Assert.Equal(((string?)null, true), (clear.Dm, clear.Final));
            Assert.Null(View(Row(RequestKinds.PostsPause, RequestStates.Refused, ended: 2, result: "Posts are already paused."), Noon.AddSeconds(3)).Dm);
        }

        // A test message that's done links to the message in Discord, built from the parsed numbers, and nothing else does
        [Fact]
        public void ATestMessageLinksToItsMessage()
        {
            var done = View(Row(RequestKinds.GuildTestMessage, RequestStates.Done, claimed: 1, ended: 2, result: "Sent the test message.", number: 1300000000000000001),
                Noon.AddSeconds(3));
            Assert.Equal(($"https://discord.com/channels/{RequestSteps.ServerId}/{RequestSteps.ChannelId}/1300000000000000001", "Open the test message in Discord"),
                (done.Link!, done.LinkText!));

            Assert.Null(View(Row(RequestKinds.GuildTestMessage, RequestStates.Done, claimed: 1, ended: 2, result: "Sent."), Noon.AddSeconds(3)).Link);
            Assert.Null(View(Row(RequestKinds.GuildTestMessage, RequestStates.Failed, claimed: 1, ended: 2, result: "Failed.", number: 5), Noon.AddSeconds(3)).Link);
            Assert.Null(View(Row(RequestKinds.CachesClear, RequestStates.Done, claimed: 1, ended: 2, result: "Cleared.", number: 5), Noon.AddSeconds(3)).Link);
        }

        // A lookup that's done links to its account's blacklist page, which offers to add it when it isn't there, only for
        // a number an account can be
        [Fact]
        public void ALookupShowsItsAccountAndEntry()
        {
            RequestView Lookup(ulong? number) =>
                View(Row(RequestKinds.SteamLookup, RequestStates.Done, claimed: 1, ended: 2, result: "That link is account [U:1:22202].", number: number), Noon.AddSeconds(3));

            Assert.Equal(("/admin/blacklist/22202", "Its blacklist page, [U:1:22202]"), (Lookup(22202).Link!, Lookup(22202).LinkText!));
            Assert.Equal("/admin/blacklist/4294967295", Lookup(uint.MaxValue).Link);
            Assert.Null(Lookup(0).Link);
            Assert.Null(Lookup((ulong)uint.MaxValue + 1).Link);
            Assert.Null(Lookup(null).Link);
        }

        // What was asked, by whom and about what, with links to the person's rows, the server, the request's audit row and the
        // page it was asked from, the channel's name from MinerBot's copy, and the reason as text someone typed
        [Fact]
        public async Task ThePageSaysWhatWasAskedAndWhere()
        {
            var db = await fixture.ReadyBotAsync();
            await StatusSteps.ServersAsync(db);
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.201");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var test = await RequestSteps.AddAsync(db, factory, RequestKinds.GuildTestMessage, now, TestUsers.Admin);
            var html = await owner.Client.GetStringAsync($"/admin/requests/{test}", Token);
            var text = Text(html);

            Assert.Contains($"What Send a test message Asked for by Test Admin (Admin), Discord ID {TestUsers.Admin.IdText} Asked for at {TimeWords.Utc(now)}", text);
            Assert.Contains($"Waits until {TimeWords.Utc(now.AddMinutes(2))} Server {RequestSteps.ServerId} Channel", text);
            Assert.Contains("<dd>#<bdi class=\"revealed\">general</bdi>, ID 3001</dd>", html);
            Assert.Contains("The bot's run Not yet Taken at Not yet Ended at Not yet", text);
            Assert.Contains($"href=\"/admin/audit?person={TestUsers.Admin.IdText}\"", html);
            Assert.Contains($"href=\"/admin/servers/{RequestSteps.ServerId}\"", html);
            Assert.Contains($"href=\"/admin/audit?type=request&amp;id={test}\"", html);
            Assert.Contains($"<a href=\"/admin/servers/{RequestSteps.ServerId}\">Back to the server's page</a>", WebUtility.HtmlDecode(html));

            // A reason with a hidden character, shown as its code; the way back to the controls
            var pause = await RequestSteps.AddAsync(db, factory, RequestKinds.PostsPause, now);
            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.RequestTable} SET argument = @text WHERE id = @pause", new { pause, text = "Night\u200Bly checks" });
            var paused = await owner.Client.GetStringAsync($"/admin/requests/{pause}", Token);
            Assert.Contains("<dd><bdi class=\"revealed\">Night<mark class=\"hidden-char\" title=\"zero-width space\">[U+200B]</mark>ly checks</bdi></dd>", WebUtility.HtmlDecode(paused));
            Assert.Contains("<a href=\"/admin/bot/controls\">Back to Bot controls</a>", paused);

            // A sign-in's message leads the Owner back to the Sessions page and an Admin to the audit log
            var alert = (await db.RequestsAsync()).First(row => row.kind == RequestKinds.OwnerSigninAlert).id;
            Assert.Contains("<a href=\"/admin/sessions\">Back to Sessions and security</a>", await owner.Client.GetStringAsync($"/admin/requests/{alert}", Token));
            var admin = factory.Visitor("203.0.113.202");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            Assert.Contains("<a href=\"/admin/audit\">Back to the audit log</a>", await admin.Client.GetStringAsync($"/admin/requests/{alert}", Token));

            var lookup = await RequestSteps.AddAsync(db, factory, RequestKinds.SteamLookup, now, TestUsers.Admin);
            Assert.Contains("<a href=\"/admin/blacklist\">Back to the blacklist</a>", await admin.Client.GetStringAsync($"/admin/requests/{lookup}", Token));
        }

        // The fragment is the box alone, with its marker, and when to look next: 2 seconds for a young request, 5 once it's
        // older, and end once what it shows can't change. Fails if end is never sent.
        [Fact]
        public async Task TheFragmentIsTheBoxWithItsMarkers()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var admin = factory.Visitor("203.0.113.203");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var id = await RequestSteps.AddAsync(db, factory, RequestKinds.CachesClear, now, TestUsers.Admin);

            var young = await LookAsync(admin, id);
            var box = await young.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.OK, young.StatusCode);
            Assert.Equal("text/html", young.Content.Headers.ContentType?.MediaType);
            Assert.Equal([RequestViews.Fragment], young.Headers.GetValues(BackgroundRefresh.FragmentHeader));
            Assert.Equal("2", Next(young));
            Assert.StartsWith("<div class=\"request-box\">", box.Trim());
            Assert.Contains($"Clear caches, request {id}, asked for by", Text(box));
            Assert.DoesNotContain("<html", box);
            Assert.DoesNotContain("<h1", box);
            Assert.DoesNotContain("<script", box);

            factory.Clock.Advance(TimeSpan.FromSeconds(25));
            Assert.Equal("5", Next(await LookAsync(admin, id)));

            await RequestSteps.ClaimAsync(db, id, now.AddSeconds(26));
            await RequestSteps.FinishAsync(db, id, RequestStates.Done, "Cleared the armor-name and latest-maps caches.", now.AddSeconds(27));
            var ended = await LookAsync(admin, id);
            Assert.Equal(BackgroundRefresh.NextEnd, Next(ended));
            Assert.Contains("Done at", Text(await ended.Content.ReadAsStringAsync(Token)));
        }

        // A request box left open looks every few seconds, and none of those looks counts as use: the session still ends 30
        // minutes after it was last touched. Fails if the fragment touches the session.
        [Fact]
        public async Task TheFragmentNeverKeepsASessionGoing()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.204");
            var key = await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var alert = Assert.Single(await db.RequestsAsync()).id;
            var signedIn = (await db.SessionAsync(key))!.last_seen_at;

            for (var minutes = 5; minutes < 30; minutes += 5)
            {
                factory.Clock.Advance(TimeSpan.FromMinutes(5));
                Assert.True((await LookAsync(owner, alert)).StatusCode == HttpStatusCode.OK, $"The look after {minutes} minutes failed");
                Assert.Equal(signedIn, (await db.SessionAsync(key))!.last_seen_at);
            }

            factory.Clock.Advance(TimeSpan.FromMinutes(5));
            var ended = await LookAsync(owner, alert);
            Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
            Assert.False(HasMarkers(ended));
            Assert.Null(await db.SessionAsync(key));
        }

        // Only a box the read gave carries the markers: a request that isn't there, someone signed out, a read that fails,
        // and the page itself carry neither, so refresh.js never puts any of them in a box. Fails if the markers are set
        // before the read.
        [Fact]
        public async Task AFragmentThatFailsHasNoMarker()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var admin = factory.Visitor("203.0.113.205");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            var alert = Assert.Single(await db.RequestsAsync()).id;

            var missing = await LookAsync(admin, alert + 1000);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.False(HasMarkers(missing));

            var page = await admin.Client.GetAsync($"/admin/requests/{alert}", Token);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.False(HasMarkers(page));

            var signedOut = await LookAsync(factory.Visitor("203.0.113.206"), alert);
            Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
            Assert.False(HasMarkers(signedOut));

            await using var failing = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IBotRequestReads, FailingReads>())
            });
            var other = failing.Visitor("203.0.113.207");
            await SignInSteps.SignedInAsync(failing, other, TestUsers.Admin);

            var failed = await LookAsync(other, alert);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.False(HasMarkers(failed));
            Assert.Contains(ErrorResponses.Unavailable, WebUtility.HtmlDecode(await failed.Content.ReadAsStringAsync(Token)));
        }

        // An ID that isn't one, or names no request, is the not-found page, for the page and its fragment alike
        [Fact]
        public async Task AMissingOrBadIdIsNotFound()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.208");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            foreach (var id in new[] { "0", "-1", "99999", "abc", "1.5", "99999999999999999999" })
            {
                Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync($"/admin/requests/{id}", Token)).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await LookAsync(owner, $"/admin/requests/{id}?handler=result")).StatusCode);
            }

            // Nothing here takes a form
            var token = await SignInSteps.FormTokenAsync(owner);
            var alert = Assert.Single(await db.RequestsAsync()).id;
            Assert.Equal(HttpStatusCode.BadRequest, (await SignInSteps.PostAsync(owner, $"/admin/requests/{alert}", token, origin: DashboardFactory.PublicOrigin)).StatusCode);
        }

        // The bot's nightly clean-up deletes a request after 90 days, while its audit row, which links to its page, stays 2
        // years. The page is still a 404, in words that say so and link the audit row, and the control's last use says it
        // wasn't asked for in the last 90 days, never that it never was; the fragment stays the plain 404, with no marker.
        // Fails if the page of a request that's gone reads as an address with nothing at it.
        [Fact]
        public async Task ARequestTheCleanUpDeletedSaysWhereItsRecordIs()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.211");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            var sent = await RequestSteps.PostControlAsync(owner, await RequestSteps.ControlsAsync(owner), ControlWords.ClearCaches);
            Assert.Equal(HttpStatusCode.Redirect, sent.StatusCode);
            var id = Assert.Single(await db.SentAsync()).id;

            // As HousekeepingJob deletes it 90 days on
            await db.RootExecuteAsync($"DELETE FROM {MariaDbFixture.RequestTable} WHERE id = @id", new { id });
            Assert.Contains($"<a href=\"/admin/requests/{id}\">", await owner.Client.GetStringAsync("/admin/audit", Token));

            var gone = await owner.Client.GetAsync($"/admin/requests/{id}", Token);
            var html = await gone.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
            Assert.Contains($"<h1>Bot request {id}</h1>", html);
            Assert.Contains(RequestWords.Gone(id), Text(html));
            Assert.Contains($"<a href=\"/admin/audit?type=request&amp;id={id}\">Its row in the audit log</a>", html);
            Assert.DoesNotContain("There's no page at that address.", Text(html));
            Assert.False(HasMarkers(gone));
            Assert.False(PageTests.LoadsRefreshScript(html), "A request's page with nothing to follow loads refresh.js");
            PageTests.AssertAccessible("a request the clean-up deleted", html);

            var look = await LookAsync(owner, id);
            Assert.Equal(HttpStatusCode.NotFound, look.StatusCode);
            Assert.False(HasMarkers(look));

            var line = Regex.Match((await RequestSteps.ControlsAsync(owner)).Html, "<h3>Clear the caches</h3>(.*?)</section>", RegexOptions.Singleline).Groups[1].Value;
            Assert.Contains("Not asked for in the last 90 days.", Text(line));
            Assert.DoesNotContain("Never asked for.", Text(line));
        }

        // A send whose commit the database didn't confirm, and that rolled back, leaves only the audit row written on its own,
        // which reads Not sure it was sent and links to the request's page by the ID the request was given. That page is the
        // answer to the row's question: a request that new that isn't there never landed. Fails if its words give the 90-day
        // clean-up as the only reason a request is missing.
        [Fact]
        public async Task ARequestWhoseSaveNeverLandedSaysSoOnItsPage()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider =>
                    new LostCommitQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider))))
            });
            var owner = factory.Visitor("203.0.113.215");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            var sent = await RequestSteps.AnsweredAsync(owner, await RequestSteps.PostControlAsync(owner, await RequestSteps.ControlsAsync(owner), ControlWords.ClearCaches));
            Assert.Contains(RequestWords.Unsure, Text(await sent.Content.ReadAsStringAsync(Token)));
            Assert.Empty(await db.SentAsync());
            var unsure = Assert.Single(await db.AuditAsync(), entry => entry.outcome == DataAccessLibrary.Audit.AuditOutcome.Unknown);
            var id = ulong.Parse(unsure.target_id!, CultureInfo.InvariantCulture);

            var audit = Text(await owner.Client.GetStringAsync("/admin/audit", Token));
            Assert.Contains("Not sure it was sent", audit);
            Assert.Contains($"Bot request {id}", audit);

            var gone = await owner.Client.GetAsync($"/admin/requests/{id}", Token);
            var text = Text(await gone.Content.ReadAsStringAsync(Token));
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
            Assert.Contains(RequestWords.Gone(id), text);
            Assert.Contains("A newer one whose save the database didn't confirm, as its row there says, never landed, so the bot never had it.", text);
        }

        // A request of a kind this version doesn't know, which a later version writes and a rollback of the dashboard leaves
        // behind, opens for the Owner and the Admins alike: its stored word as the label, the audit log as the way back, no
        // word about a direct message, and its box final. Fails if the page takes the kind for one this version knows.
        [Fact]
        public async Task ARequestOfALaterKindOpensAsStored()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.212");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var admin = factory.Visitor("203.0.113.213");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            await db.RootExecuteAsync($@"INSERT INTO {MariaDbFixture.RequestTable} (request_key, kind, actor_id, actor_name, actor_role, guild_id, created_at, expires_at,
                signature, state, run_id, claimed_at, ended_at, result, dm_state)
                VALUES (@key, 'guild.announce', @actor, 'Test Owner', 'owner', @guild, @at, @expires, @signature, 'done', @run, @at, @at, 'Announced.', 'sent')",
                new { key = RequestFields.NewKey(), actor = TestUsers.Owner.Id, guild = RequestSteps.ServerId, at = now.AddSeconds(-5), expires = now.AddMinutes(2),
                    signature = new byte[RequestSigning.SignatureLength], run = Run });
            var id = Assert.Single(await db.RequestsAsync(), row => row.kind == "guild.announce").id;

            foreach (var browser in new[] { owner, admin })
            {
                var page = await browser.Client.GetAsync($"/admin/requests/{id}", Token);
                var html = await page.Content.ReadAsStringAsync(Token);

                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                Assert.Contains($"guild.announce, request {id}, asked for by Test Owner (Owner)", Text(html));
                Assert.Contains("Done Done at", Text(html));
                Assert.Contains("<a href=\"/admin/audit\">Back to the audit log</a>", html);
                Assert.DoesNotContain("The owner was sent a direct message about it.", Text(html));
                PageTests.AssertAccessible("the page of a request of a later kind", html);

                var look = await LookAsync(browser, id);
                Assert.Equal(HttpStatusCode.OK, look.StatusCode);
                Assert.Equal(BackgroundRefresh.NextEnd, Next(look));
            }
        }

        // A request's page and its box show the Owner and the Admins neither the address the request was asked from, which
        // only its audit row holds, nor its key or its signature
        [Fact]
        public async Task NoAddressKeyOrSignatureOnAnyRequestPage()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("198.51.100.81");
            var admin = factory.Visitor("198.51.100.82");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            var rows = await db.RequestsAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(["198.51.100.81", "198.51.100.82"], (await db.AuditAsync()).Where(entry => entry.action == RequestKinds.OwnerSigninAlert).Select(entry => entry.ip!));

            foreach (var browser in new[] { owner, admin })
            {
                foreach (var row in rows)
                {
                    foreach (var path in new[] { $"/admin/requests/{row.id}", $"/admin/requests/{row.id}?handler=result" })
                    {
                        var response = await browser.Client.GetAsync(path, Token);
                        var body = await response.Content.ReadAsStringAsync(Token);

                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        Assert.DoesNotContain("198.51.100.8", body);
                        Assert.DoesNotContain(row.request_key, body, StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain(Convert.ToHexString(row.signature), body, StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain(Convert.ToBase64String(row.signature), body);
                    }
                }
            }
        }

        // A stored status text the rules now refuse is never shown, as the bot never shows one: its page names the rule it
        // breaks instead. One they pass is shown as text someone typed.
        [Fact]
        public async Task ARefusedStoredStatusTextIsNeverShownOnItsRequest()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.209");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var allowed = await RequestSteps.AddAsync(db, factory, RequestKinds.StatusSet, now, argument: "Mining at night");

            foreach (var (text, rule) in new[] { ("Join x.com/minerbot", "the status text has a link"), ("Hi @everyone", "the status text has Discord's mention syntax"),
                ("Back\u202Esoon", "the status text has a character that shows nothing or changes the text's direction") })
            {
                await db.RootExecuteAsync($"UPDATE {MariaDbFixture.RequestTable} SET argument = @text WHERE id = @allowed", new { text, allowed });
                var html = await owner.Client.GetStringAsync($"/admin/requests/{allowed}", Token);

                Assert.Contains($"Text A status text the rules now refuse ({rule}), which the bot doesn't show and this page doesn't either.", Text(html));
                Assert.DoesNotContain(text, WebUtility.HtmlDecode(html));
                Assert.DoesNotContain("x.com", html);
                Assert.DoesNotContain("@everyone", html);
            }

            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.RequestTable} SET argument = 'Mining at night' WHERE id = @allowed", new { allowed });
            Assert.Contains("Text Mining at night", Text(await owner.Client.GetStringAsync($"/admin/requests/{allowed}", Token)));
        }

        // A row holding a time MySql.Data can't read, which MariaDB's default mode lets the dashboard's own login write, never
        // takes the page down: its times read as unreadable, and its box says it can't be followed, and so does the box of a
        // request the bot took whose time of taking can't be read. Fails if the guard is gone from the read, or from the box
        // of a request the bot took.
        [Fact]
        public async Task AnUnreadableTimeDoesntBreakTheRequestPage()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.210");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            foreach (var time in new[] { "0000-00-00 00:00:00.000", "2027-00-10 00:00:00.000" })
            {
                var id = await RequestSteps.AddAsync(db, factory, RequestKinds.CachesClear, now);
                await db.RootExecuteAsync($"UPDATE {MariaDbFixture.RequestTable} SET created_at = @time, expires_at = @time WHERE id = @id", new { time, id });

                var page = await owner.Client.GetAsync($"/admin/requests/{id}", Token);
                var text = Text(await page.Content.ReadAsStringAsync(Token));

                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                Assert.Contains("Asked for at an unreadable time Waits until an unreadable time", text);
                Assert.Contains("Can't follow", text);

                var look = await LookAsync(owner, id);
                Assert.Equal((HttpStatusCode.OK, BackgroundRefresh.NextEnd), (look.StatusCode, Next(look)!));
            }

            // The bot's own column, written by hand: the box times a request the bot took from when it took it
            var taken = await RequestSteps.TakenAsync(db, factory, RequestKinds.CachesClear, now);
            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.RequestTable} SET claimed_at = '2027-00-10 00:00:00.000' WHERE id = @taken", new { taken });

            var running = await owner.Client.GetAsync($"/admin/requests/{taken}", Token);
            Assert.Equal(HttpStatusCode.OK, running.StatusCode);
            Assert.Contains("Can't follow", Text(await running.Content.ReadAsStringAsync(Token)));

            var runningLook = await LookAsync(owner, taken);
            Assert.Equal((HttpStatusCode.OK, BackgroundRefresh.NextEnd), (runningLook.StatusCode, Next(runningLook)!));
        }

        // A page's ?request= draws the box only for a request that page shows: one of its kinds and, on a server's page, that
        // server's. A restart named on a server's page, another server's test message, or a request that isn't there draws
        // nothing. Fails if the parameter is shown whatever it names.
        [Fact]
        public async Task TheBoxShowsOnlyWhereItFits()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var restart = await RequestSteps.AddAsync(db, factory, RequestKinds.BotRestart, now);
            var test = await RequestSteps.AddAsync(db, factory, RequestKinds.GuildTestMessage, now);
            var otherServer = await RequestSteps.AddAsync(db, factory, RequestKinds.GuildTestMessage, now, guildId: RequestSteps.OtherServerId);

            string[] serverKinds = [RequestKinds.GuildTestMessage, RequestKinds.GuildRefresh, RequestKinds.GuildLeave];
            var page = $"/admin/servers/{RequestSteps.ServerId}?request={test}#result";
            await using var scope = factory.Services.CreateAsyncScope();
            var boxes = scope.ServiceProvider.GetRequiredService<RequestBoxes>();

            var shown = await boxes.SectionAsync(test, serverKinds, RequestSteps.ServerId, page);
            Assert.NotNull(shown);
            Assert.Equal((test, page, "Your request", false), (shown.View.Id, shown.CheckAgain, shown.Heading, shown.View.Final));

            Assert.Null(await boxes.SectionAsync(restart, serverKinds, RequestSteps.ServerId, page));
            Assert.Null(await boxes.SectionAsync(otherServer, serverKinds, RequestSteps.ServerId, page));
            Assert.Null(await boxes.SectionAsync(test + 1000, serverKinds, RequestSteps.ServerId, page));
            Assert.Null(await boxes.SectionAsync(0, serverKinds, RequestSteps.ServerId, page));
            Assert.Null(await boxes.SectionAsync(null, serverKinds, RequestSteps.ServerId, page));

            // A page about no one server shows its kinds of any
            Assert.Equal(restart, (await boxes.SectionAsync(restart, [RequestKinds.BotRestart, RequestKinds.CachesClear], null, "/admin/bot/controls?request=1#result"))?.View.Id);
            Assert.Null(await boxes.SectionAsync(test, [RequestKinds.BotRestart, RequestKinds.CachesClear], null, "/admin/bot/controls?request=1#result"));
        }

        // Check again is the address the page builds, with again=1 added, or taken off when the page was opened with again in
        // any letter case and with any value or none, as the app's own query reads it, and #result. So it's never the address
        // shown. Fails if it equals that address, or keeps again once the page has it.
        [Fact]
        public void CheckAgainAddsOrTakesOffItsOwnParameter()
        {
            static QueryCollection Shown(params string[] keys) => new(keys.ToDictionary(key => key, _ => new StringValues(""), StringComparer.OrdinalIgnoreCase));

            Assert.Equal("/admin/bot/controls?request=5&again=1#result", RequestBoxes.CheckAgain("/admin/bot/controls?request=5", QueryCollection.Empty));
            Assert.Equal("/admin/bot/controls?request=5&again=1#result", RequestBoxes.CheckAgain("/admin/bot/controls?request=5", Shown("request", "notice")));
            Assert.Equal("/admin/bot/controls?request=5#result", RequestBoxes.CheckAgain("/admin/bot/controls?request=5", Shown("request", "Again")));
            Assert.Equal("/admin/bot/controls?request=5#result", RequestBoxes.CheckAgain("/admin/bot/controls?request=5", Shown("AGAIN")));

            // The review's address as it built it, its search encoded
            Assert.Equal("/admin/armor/82?done=approved&request=5&list=pending&q=frost%20bite&again=1#result",
                RequestBoxes.CheckAgain("/admin/armor/82?done=approved&request=5&list=pending&q=frost%20bite", QueryCollection.Empty));
        }

        // A restart's box on the request's own page, its fragment and ?request= on the controls page reads the bot's record of
        // the new run's first connection: by the status row's sample while only an earlier run's is there, then at the
        // record's time once the bot has written it, late, at the next look, which a reconnect since doesn't replace. Fails if
        // the box keeps the sample once the record is there, or takes an earlier run's connection or a reconnect for it.
        [Fact]
        public async Task ARestartsBoxReadsWhenTheNewRunConnected()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.215");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var restart = await RequestSteps.TakenAsync(db, factory, RequestKinds.BotRestart, now.AddSeconds(-30));
            await RequestSteps.FinishAsync(db, restart, RequestStates.Done, "The bot is restarting. It's usually back within a minute.", now.AddSeconds(-28));

            static BotEvent Connection(ulong run, uint seq, DateTime at) =>
                new() { run_id = run, seq = seq, at = at, kind = EventWords.DiscordConnected, level = EventLevel.Info, detail = "the first connection of this run" };

            var status = StatusSteps.Status(now.AddSeconds(-1));
            (status.run_id, status.started_at, status.discord_since) = (NewRun, now.AddSeconds(-15), now.AddSeconds(-9));
            await db.Status().SaveStatus(status);
            await db.Status().AddEvents([Connection(Run, 1, now.AddHours(-1))]);

            async Task<string[]> BoxesAsync() =>
            [
                Text(await owner.Client.GetStringAsync($"/admin/requests/{restart}", Token)),
                Text(await (await LookAsync(owner, restart)).Content.ReadAsStringAsync(Token)),
                Text(await owner.Client.GetStringAsync($"/admin/bot/controls?request={restart}", Token))
            ];

            var started = RequestWords.Clock(now.AddSeconds(-15));
            Assert.All(await BoxesAsync(), box => Assert.Contains($"Back on Discord by {RequestWords.Clock(now.AddSeconds(-9))}: its new run started at {started}.", box));

            await db.Status().AddEvents([Connection(NewRun, 1, now.AddSeconds(-13))]);
            var exact = $"Back on Discord at {RequestWords.Clock(now.AddSeconds(-13))}, 17 s after the request: its new run started at {started}.";
            Assert.All(await BoxesAsync(), box => Assert.Contains(exact, box));

            status.discord_since = now.AddSeconds(-2);
            await db.Status().SaveStatus(status);
            await db.Status().AddEvents([Connection(NewRun, 2, now.AddSeconds(-3))]);
            Assert.All(await BoxesAsync(), box => Assert.Contains(exact, box));
        }

        // The time a box is drawn at, which never moves
        sealed class StoppedClock(DateTime now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => new(now);
        }

        // The one request a box is for, as the reads give it
        sealed class OneRequest(RequestPageRow row) : IBotRequestReads
        {
            static NotSupportedException NotTheBoxs() => new("Not a read the box makes");

            public Task<RequestPageRow?> GetRequestView(ulong id) => Task.FromResult<RequestPageRow?>(id == row.id ? row : null);
            public Task<long> CountAlertsSince(DateTime since) => throw NotTheBoxs();
            public Task<RequestPageRow?> LatestDm() => throw NotTheBoxs();
            public Task<RequestPageRow?> LatestRefusedAlert() => throw NotTheBoxs();
            public Task<IReadOnlyList<AskedRow>> ListAskedBy(ulong actorId, DateTime since) => throw NotTheBoxs();
            public Task<IReadOnlyList<ClaimRow>> ListClaimsSince(DateTime since) => throw NotTheBoxs();
            public Task<IReadOnlyList<RequestPageRow>> ListLatest(IReadOnlyCollection<string> kinds, ulong? guildId = null) => throw NotTheBoxs();
            public Task<IReadOnlyList<WaitingRow>> ListWaiting(DateTime now) => throw NotTheBoxs();
        }

        // Every read fails as a database that stopped answering would
        sealed class FailingReads : IBotRequestReads
        {
            static TimeoutException Dropped() => new("The database didn't answer within 15 seconds");

            public Task<long> CountAlertsSince(DateTime since) => throw Dropped();
            public Task<RequestPageRow?> GetRequestView(ulong id) => throw Dropped();
            public Task<RequestPageRow?> LatestDm() => throw Dropped();
            public Task<RequestPageRow?> LatestRefusedAlert() => throw Dropped();
            public Task<IReadOnlyList<AskedRow>> ListAskedBy(ulong actorId, DateTime since) => throw Dropped();
            public Task<IReadOnlyList<ClaimRow>> ListClaimsSince(DateTime since) => throw Dropped();
            public Task<IReadOnlyList<RequestPageRow>> ListLatest(IReadOnlyCollection<string> kinds, ulong? guildId = null) => throw Dropped();
            public Task<IReadOnlyList<WaitingRow>> ListWaiting(DateTime now) => throw Dropped();
        }

        [GeneratedRegex("<[^>]+>")]
        private static partial Regex Tags();

        [GeneratedRegex("\\s+")]
        private static partial Regex Spaces();
    }
}
