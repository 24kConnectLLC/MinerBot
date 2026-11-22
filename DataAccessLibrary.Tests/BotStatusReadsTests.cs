using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using DataAccessLibrary.Tests.Database;

namespace DataAccessLibrary.Tests
{
    // The dashboard's reads of the bot's status and history, from rows the bot's own BotStatusData and RetentionData
    // write, so the reads match what the bot writes. Every time comes back marked UTC.
    [Collection("Database")]
    public class BotStatusReadsTests(MariaDbFixture fixture)
    {
        static readonly DateTime Noon = new(2026, 11, 14, 12, 0, 0, 123, DateTimeKind.Utc);
        const ulong RunId = 18446744073709551615;

        async Task<MariaDbFixture> StartAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.StatusTable, MariaDbFixture.EventTable, MariaDbFixture.JobRunTable, MariaDbFixture.JobStatusTable,
                MariaDbFixture.SampleTable, MariaDbFixture.DailyTable);
            return db;
        }

        static BotEvent Event(uint seq, DateTime at, string kind = "steam.logged_on", string level = EventLevel.Info, string? detail = null) =>
            new() { run_id = RunId, seq = seq, at = at, kind = kind, level = level, detail = detail };

        static JobRun Run(uint seq, string job, DateTime started, string result = JobResult.Ok, DateTime? ended = null) =>
            new() { run_id = RunId, seq = seq, job = job, started_at = started, ended_at = ended ?? started.AddSeconds(1), result = result, duration_ms = 1000 };

        [Fact]
        public async Task EmptyTablesReadAsNothing()
        {
            var reads = (await StartAsync()).StatusReads();

            Assert.Null(await reads.GetStatus());
            var none = await reads.ListEvents(new EventQuery { IncludeRoutine = true });
            Assert.Equal(0, none.Total);
            Assert.Empty(none.Rows);
            Assert.Null(await reads.LatestEvent("discord.connected"));
            Assert.Null(await reads.FirstEventAfter("discord.disconnected", null));
            Assert.Empty(await reads.CountEvents(["bot.started"], Noon.AddDays(-30), Noon.AddDays(-7)));
            Assert.Empty(await reads.ListJobs());
            Assert.Empty(await reads.ListJobRuns(["ArmorJob.AddColorArrays"], 50));
            Assert.Empty(await reads.ListUptimeDays(Noon.Date.AddDays(-90), Noon.Date));
            Assert.Empty(await reads.CountSampleDays(Noon.Date.AddDays(-90), Noon));

            var total = await reads.CountSamples(Noon.AddDays(-1), Noon);
            Assert.Equal((0, 0, 0), (total.sampled_minutes, total.discord_minutes, total.steam_minutes));

            var start = await reads.FirstRecord();
            Assert.Null(start.first_minute);
            Assert.Null(start.first_day);
        }

        // As the bot wrote it, to the millisecond, every time UTC, and the token's day as its start
        [Fact]
        public async Task StatusRowIsReadBackInUtc()
        {
            var db = await StartAsync();

            await db.Status().SaveStatus(new BotStatus
            {
                run_id = RunId,
                version = "5a62c9fcc6112c68e671285da53fb25369ea1407",
                started_at = Noon,
                heartbeat_at = Noon.AddMinutes(1),
                stopped_at = Noon.AddMinutes(1),
                discord_state = DiscordState.Connected,
                discord_since = Noon.AddSeconds(1),
                discord_latency_ms = 18,
                discord_guilds = 10,
                steam_state = SteamState.NeedsSignIn,
                steam_since = Noon.AddSeconds(2),
                steam_last_result = "Revoked",
                steam_token_expires = new DateOnly(2027, 6, 3)
            });

            var row = await db.StatusReads().GetStatus();

            Assert.NotNull(row);
            Assert.Equal(RunId, row.run_id);
            Assert.Equal("5a62c9fcc6112c68e671285da53fb25369ea1407", row.version);
            Assert.Equal(Noon, row.started_at);
            Assert.Equal(Noon.AddMinutes(1), row.heartbeat_at);
            Assert.Equal(Noon.AddMinutes(1), row.stopped_at);
            Assert.Equal(DiscordState.Connected, row.discord_state);
            Assert.Equal(Noon.AddSeconds(1), row.discord_since);
            Assert.Equal(18, row.discord_latency_ms);
            Assert.Equal(10, row.discord_guilds);
            Assert.Equal(SteamState.NeedsSignIn, row.steam_state);
            Assert.Equal(Noon.AddSeconds(2), row.steam_since);
            Assert.Equal("Revoked", row.steam_last_result);
            Assert.Equal(new DateTime(2027, 6, 3, 0, 0, 0, DateTimeKind.Utc), row.steam_token_expires);

            Assert.All(new[] { row.started_at, row.heartbeat_at, row.stopped_at!.Value, row.discord_since!.Value, row.steam_since!.Value, row.steam_token_expires!.Value },
                time => Assert.Equal(DateTimeKind.Utc, time.Kind));
        }

        // Newest first, a tie broken by the order they were written, 50 to a page with the total on each, filtered by level
        // and kind, and without the routine reconnects unless asked
        [Fact]
        public async Task EventsArePagedAndFiltered()
        {
            var db = await StartAsync();
            var status = db.Status();

            await status.AddEvents(Enumerable.Range(1, 100).Select(seq => Event((uint)seq, Noon.AddSeconds(seq))).ToList());
            await status.AddEvents(
            [
                Event(101, Noon.AddSeconds(101), RoutineReconnect.Kind, EventLevel.Warning, RoutineReconnect.Detail),
                Event(102, Noon.AddSeconds(102), RoutineReconnect.Kind, EventLevel.Warning, "WebSocketException"),
                Event(103, Noon.AddSeconds(103), "job.failed", EventLevel.Error, "HousekeepingJob.ApplyRetention failed: TimeoutException"),
                Event(104, Noon.AddSeconds(104), "bot.started", EventLevel.Info, "version 5a62c9f"),
                Event(105, Noon.AddSeconds(104), "bot.stopping", EventLevel.Info)
            ]);
            var reads = db.StatusReads();

            var first = await reads.ListEvents(new EventQuery());
            Assert.Equal(104, first.Total);
            Assert.Equal(50, first.Rows.Count);
            Assert.Equal(["bot.stopping", "bot.started", "job.failed", RoutineReconnect.Kind, "steam.logged_on"], first.Rows.Take(5).Select(row => row.kind));
            Assert.Equal(Noon.AddSeconds(100), first.Rows[4].at);
            Assert.All(first.Rows, row => Assert.Equal(DateTimeKind.Utc, row.at.Kind));
            Assert.Equal("WebSocketException", first.Rows[3].detail);

            var last = await reads.ListEvents(new EventQuery { Page = 2 });
            Assert.Equal(104, last.Total);
            Assert.Equal([Noon.AddSeconds(4), Noon.AddSeconds(3), Noon.AddSeconds(2), Noon.AddSeconds(1)], last.Rows.Select(row => row.at));
            Assert.Empty((await reads.ListEvents(new EventQuery { Page = 3 })).Rows);

            var routine = await reads.ListEvents(new EventQuery { IncludeRoutine = true, PageSize = 5 });
            Assert.Equal(105, routine.Total);
            Assert.Equal(RoutineReconnect.Detail, routine.Rows[4].detail);

            Assert.Equal(1, (await reads.ListEvents(new EventQuery { Level = EventLevel.Error })).Total);
            Assert.Equal(1, (await reads.ListEvents(new EventQuery { Level = EventLevel.Warning })).Total);
            Assert.Equal(2, (await reads.ListEvents(new EventQuery { Level = EventLevel.Warning, IncludeRoutine = true })).Total);
            Assert.Equal(1, (await reads.ListEvents(new EventQuery { Kind = RoutineReconnect.Kind })).Total);
            Assert.Equal(2, (await reads.ListEvents(new EventQuery { Kind = RoutineReconnect.Kind, IncludeRoutine = true })).Total);
            Assert.Equal(0, (await reads.ListEvents(new EventQuery { Kind = "bot.started", Level = EventLevel.Error })).Total);
            Assert.Equal(100, (await reads.ListEvents(new EventQuery { Kind = "steam.logged_on", PageSize = 100 })).Rows.Count);
        }

        // The SQL hides exactly what RoutineReconnect.Is calls routine, in letter case and trailing spaces too, which the
        // column's collation alone would ignore
        [Fact]
        public async Task OnlyDiscordsOwnReconnectIsRoutine()
        {
            var db = await StartAsync();
            string?[] details = [RoutineReconnect.Detail, "gatewayreconnectexception", RoutineReconnect.Detail + " ", "WebSocketException", null];

            await db.Status().AddEvents(details.Select((detail, i) => Event((uint)(i + 1), Noon.AddSeconds(i), RoutineReconnect.Kind, EventLevel.Warning, detail))
                .Append(Event(10, Noon.AddSeconds(10), "discord.connected", EventLevel.Info, RoutineReconnect.Detail)).ToList());
            var reads = db.StatusReads();

            var shown = (await reads.ListEvents(new EventQuery())).Rows;
            var all = (await reads.ListEvents(new EventQuery { IncludeRoutine = true })).Rows;

            Assert.Equal(5, shown.Count);
            Assert.Equal(6, all.Count);
            Assert.All(shown, row => Assert.False(RoutineReconnect.Is(row.kind, row.detail)));
            Assert.True(RoutineReconnect.Is(Assert.Single(all.Except(shown, new IdComparer())).kind, Assert.Single(all.Except(shown, new IdComparer())).detail));
        }

        sealed class IdComparer : IEqualityComparer<BotEventRow>
        {
            public bool Equals(BotEventRow? x, BotEventRow? y) => x?.id == y?.id;
            public int GetHashCode(BotEventRow row) => row.id.GetHashCode();
        }

        // The newest of a kind, and the first of a kind after it in the list's order, a tie broken by the order written
        [Fact]
        public async Task LatestEventAndTheFirstAfterIt()
        {
            var db = await StartAsync();

            await db.Status().AddEvents(
            [
                Event(1, Noon, "discord.connected"),
                Event(2, Noon.AddMinutes(1), "discord.disconnected", EventLevel.Warning, "WebSocketException"),
                Event(3, Noon.AddMinutes(2), "discord.disconnected", EventLevel.Warning, "GatewayReconnectException"),
                Event(4, Noon.AddMinutes(3), "discord.connected"),
                Event(5, Noon.AddMinutes(5), "discord.disconnected", EventLevel.Warning, "WebSocketException, close code 1001"),
                Event(6, Noon.AddMinutes(3), "discord.disconnected", EventLevel.Warning, "no reason given")
            ]);
            var reads = db.StatusReads();

            var connected = await reads.LatestEvent("discord.connected");
            Assert.NotNull(connected);
            Assert.Equal(Noon.AddMinutes(3), connected.at);
            Assert.Equal(DateTimeKind.Utc, connected.at.Kind);

            Assert.Equal("no reason given", (await reads.FirstEventAfter("discord.disconnected", connected))?.detail);
            Assert.Equal("WebSocketException", (await reads.FirstEventAfter("discord.disconnected", null))?.detail);
            Assert.Null(await reads.FirstEventAfter("discord.connected", connected));
            Assert.Null(await reads.LatestEvent("bot.started"));
        }

        // The newest event of a kind since a time whose detail holds one of the phrases, as they're written: a later event
        // without them, an earlier one, another kind's, and one that holds a phrase only as LIKE's wildcards would read it are
        // passed over, a tie broken by the order written. Fails if the search ignores the phrases, the time or the kind, or
        // reads a phrase as a pattern.
        [Fact]
        public async Task LatestEventWithAPhrase()
        {
            var db = await StartAsync();

            await db.Status().AddEvents(
            [
                Event(1, Noon, "request.rejected", EventLevel.Warning, "request 1 refused: its signature didn't match"),
                Event(2, Noon.AddMinutes(1), "request.rejected", EventLevel.Warning, "request 2 refused: it isn't in the form the dashboard writes"),
                Event(3, Noon.AddMinutes(1), "request.rejected", EventLevel.Warning, "request 3 refused: its signature didn't match"),
                Event(4, Noon.AddMinutes(2), "request.rejected", EventLevel.Warning, "request 4 refused: its kind isn't on the bot's list"),
                Event(5, Noon.AddMinutes(3), "requests.off", EventLevel.Warning, "its signature didn't match"),
                Event(6, Noon.AddMinutes(4), "request.rejected", EventLevel.Warning, "request 6 refused: 100 of them")
            ]);
            var reads = db.StatusReads();
            string[] unsigned = ["the form the dashboard writes", "signature didn't match"];

            var latest = await reads.LatestEventWith("request.rejected", unsigned, Noon);
            Assert.Equal("request 3 refused: its signature didn't match", latest?.detail);
            Assert.Equal(DateTimeKind.Utc, latest!.at.Kind);
            Assert.Equal("request 2 refused: it isn't in the form the dashboard writes", (await reads.LatestEventWith("request.rejected", ["the form the dashboard writes"], Noon))?.detail);
            Assert.Null(await reads.LatestEventWith("request.rejected", unsigned, Noon.AddMinutes(2)));
            Assert.Null(await reads.LatestEventWith("bot.started", unsigned, Noon));
            Assert.Null(await reads.LatestEventWith("request.rejected", ["100%"], Noon));
            Assert.Null(await reads.LatestEventWith("request.rejected", ["signature_didn't"], Noon));
            Assert.Equal("request 6 refused: 100 of them", (await reads.LatestEventWith("request.rejected", ["100 of"], Noon))?.detail);
        }

        // From the first time on, and of those from the second, only for kinds that have any
        [Fact]
        public async Task EventsAreCountedSinceEachTime()
        {
            var db = await StartAsync();

            await db.Status().AddEvents(
            [
                Event(1, Noon.AddDays(-40), "bot.started"),
                Event(2, Noon.AddDays(-20), "bot.started"),
                Event(3, Noon.AddDays(-10), "bot.unclean_end", EventLevel.Warning),
                Event(4, Noon.AddDays(-3), "bot.started"),
                Event(5, Noon.AddHours(-1), "bot.started"),
                Event(6, Noon.AddDays(-7), "bot.started")
            ]);

            var counts = await db.StatusReads().CountEvents(["bot.started", "bot.unclean_end", "steam.offline"], Noon.AddDays(-30), Noon.AddDays(-7));

            Assert.Equal([("bot.started", 4L, 3L), ("bot.unclean_end", 1L, 0L)], counts.OrderBy(count => count.kind, StringComparer.Ordinal).Select(count => (count.kind, count.total, count.recent)));
        }

        // Every job by name, and the latest 50 runs of each one asked for, newest first, a tie broken by the order written
        [Fact]
        public async Task JobsAndTheirLatestRuns()
        {
            var db = await StartAsync();
            var status = db.Status();

            await status.SaveJobStatus(
            [
                new JobStatus { job = "SteamTokenJob.RenewSteamToken", schedule = "0 12 * * *", next_run_at = Noon.AddHours(1), updated_at = Noon },
                new JobStatus { job = "ArmorJob.AddColorArrays", schedule = "0 * * * *", next_run_at = Noon.AddHours(1), last_started_at = Noon, last_ended_at = Noon.AddSeconds(1),
                    last_result = JobResult.Failed, last_duration_ms = 1000, last_detail = "TimeoutException", updated_at = Noon.AddSeconds(1) }
            ]);
            await status.SaveJobRuns(Enumerable.Range(1, 60).Select(i => Run((uint)i, "ArmorJob.AddColorArrays", Noon.AddMinutes(-i))).ToList());
            await status.SaveJobRuns(
            [
                Run(61, "SteamTokenJob.RenewSteamToken", Noon.AddDays(-1)),
                Run(62, "SteamTokenJob.RenewSteamToken", Noon.AddDays(-1), JobResult.CutOff, ended: null),
                Run(63, "GuildNotificationJob.NotifyAllGuilds", Noon)
            ]);
            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.JobRunTable} SET ended_at = NULL, duration_ms = NULL WHERE seq = 62");
            var reads = db.StatusReads();

            var jobs = await reads.ListJobs();
            Assert.Equal(["ArmorJob.AddColorArrays", "SteamTokenJob.RenewSteamToken"], jobs.Select(job => job.job));
            Assert.Equal(JobResult.Failed, jobs[0].last_result);
            Assert.Equal("TimeoutException", jobs[0].last_detail);
            Assert.Equal(1000U, jobs[0].last_duration_ms);
            Assert.Equal(Noon, jobs[0].last_started_at);
            Assert.Null(jobs[1].last_started_at);
            Assert.All(new[] { jobs[0].next_run_at!.Value, jobs[0].last_started_at!.Value, jobs[0].last_ended_at!.Value, jobs[0].updated_at },
                time => Assert.Equal(DateTimeKind.Utc, time.Kind));

            var runs = await reads.ListJobRuns(["ArmorJob.AddColorArrays", "SteamTokenJob.RenewSteamToken"], 50);
            var armor = runs.Where(run => run.job == "ArmorJob.AddColorArrays").ToList();
            Assert.Equal(52, runs.Count);
            Assert.Equal(50, armor.Count);
            Assert.Equal(Noon.AddMinutes(-1), armor[0].started_at);
            Assert.Equal(Noon.AddMinutes(-50), armor[^1].started_at);
            Assert.All(runs, run => Assert.Equal(DateTimeKind.Utc, run.started_at.Kind));

            var steam = runs.Where(run => run.job == "SteamTokenJob.RenewSteamToken").ToList();
            Assert.Equal([JobResult.CutOff, JobResult.Ok], steam.Select(run => run.result));
            Assert.Null(steam[0].ended_at);
            Assert.Null(steam[0].duration_ms);

            Assert.Single(await reads.ListJobRuns(["ArmorJob.AddColorArrays"], 1));
            Assert.Empty(await reads.ListJobRuns([], 50));

            // A job named twice, or in letters the column's collation matches, still has its runs once, and a job without
            // runs adds none
            var named = await reads.ListJobRuns(["SteamTokenJob.RenewSteamToken", "ArmorJob.AddColorArrays", "steamtokenjob.renewsteamtoken",
                "SteamTokenJob.RenewSteamToken", "OldJob.Gone"], 50);
            Assert.Equal(runs.Select(run => run.id), named.Select(run => run.id));
        }

        // A day the bot's own clean-up totalled reads from uptime_daily, and the samples read by day or over any stretch,
        // with the record's start where the samples and the days say
        [Fact]
        public async Task UptimeIsReadAsTheCleanUpTotalsIt()
        {
            var db = await StartAsync();
            var day = Noon.Date.AddDays(-1);
            var next = day.AddDays(1);
            var first = day.AddHours(12);

            // 600 minutes from 12:00, Discord down for the first 5 and Steam for the first 20, then 2 hours of the next day
            var samples = Enumerable.Range(0, 600).Select(i => new UptimeSample { minute = first.AddMinutes(i), discord_up = i >= 5, steam_up = i >= 20 })
                .Concat(Enumerable.Range(0, 120).Select(i => new UptimeSample { minute = next.AddMinutes(i), discord_up = true, steam_up = true }))
                .ToList();

            foreach (var batch in samples.Chunk(100))
                await db.Status().SaveSamples(batch);

            await db.Retention().TotalUptimeDays(day, next);
            var reads = db.StatusReads();

            var totalled = Assert.Single(await reads.ListUptimeDays(day.AddDays(-5), next.AddDays(1)));
            Assert.Equal((day, 600, 595, 580), (totalled.day, totalled.sampled_minutes, totalled.discord_minutes, totalled.steam_minutes));
            Assert.Equal(DateTimeKind.Utc, totalled.day.Kind);
            Assert.Empty(await reads.ListUptimeDays(day.AddDays(-5), day));

            var days = await reads.CountSampleDays(day, next.AddDays(1));
            Assert.Equal([(day, 600, 595, 580), (next, 120, 120, 120)], days.Select(row => (row.day, row.sampled_minutes, row.discord_minutes, row.steam_minutes)));
            Assert.All(days, row => Assert.Equal(DateTimeKind.Utc, row.day.Kind));

            // Up to a minute, which isn't counted itself
            Assert.Equal([(next, 60, 60, 60)], (await reads.CountSampleDays(next, next.AddHours(1))).Select(row => (row.day, row.sampled_minutes, row.discord_minutes, row.steam_minutes)));

            var window = await reads.CountSamples(first.AddMinutes(-10), first.AddMinutes(10));
            Assert.Equal((10, 5, 0), (window.sampled_minutes, window.discord_minutes, window.steam_minutes));
            var stretch = await reads.CountSamples(day.AddHours(21), next.AddHours(1));
            Assert.Equal((120, 120, 120), (stretch.sampled_minutes, stretch.discord_minutes, stretch.steam_minutes));

            var start = await reads.FirstRecord();
            Assert.Equal(first, start.first_minute);
            Assert.Equal(day, start.first_day);
            Assert.Equal(DateTimeKind.Utc, start.first_minute!.Value.Kind);
            Assert.Equal(DateTimeKind.Utc, start.first_day!.Value.Kind);

            // Once the clean-up deletes that day's samples, the first minute kept is the next day's, and the day stays
            await db.Retention().DeleteSamples(next, 1000);
            start = await reads.FirstRecord();
            Assert.Equal(next, start.first_minute);
            Assert.Equal(day, start.first_day);
        }
    }
}
