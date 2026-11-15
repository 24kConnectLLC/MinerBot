using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using DataAccessLibrary.Tests.Database;

namespace DataAccessLibrary.Tests
{
    // The bot's status and history. The bot writes again whatever it isn't sure went through, so every write here must be
    // safe to repeat.
    [Collection("Database")]
    public class BotStatusDataTests(MariaDbFixture fixture)
    {
        static readonly DateTime Noon = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        const ulong RunId = 18446744073709551615;

        async Task<MariaDbFixture> StartAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.StatusTable, MariaDbFixture.EventTable, MariaDbFixture.JobRunTable, MariaDbFixture.JobStatusTable,
                MariaDbFixture.SampleTable);
            return db;
        }

        async Task<long> CountAsync(string table) => (await fixture.RootQueryAsync<long>($"SELECT COUNT(*) FROM {table}")).Single();

        static BotStatus Status(ulong runId = RunId) => new()
        {
            run_id = runId,
            version = "c28e305aa0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5",
            started_at = Noon.AddMilliseconds(123),
            heartbeat_at = Noon.AddMinutes(1).AddMilliseconds(456),
            discord_state = DiscordState.Connected,
            discord_since = Noon.AddSeconds(3),
            discord_latency_ms = 42,
            discord_guilds = 1234,
            steam_state = SteamState.NeedsSignIn,
            steam_since = Noon.AddSeconds(30).AddMilliseconds(789),
            steam_last_result = "Revoked",
            steam_token_expires = new DateOnly(2027, 5, 2)
        };

        // The stop write of Status()'s run, with every other column different from Status()'s too, so each column's rule
        // shows on its own
        static BotStatus Stop() => new()
        {
            run_id = RunId,
            version = "d39f416bb1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6",
            started_at = Noon.AddMilliseconds(124),
            heartbeat_at = Noon.AddMinutes(2),
            stopped_at = Noon.AddMinutes(2),
            discord_state = DiscordState.Disconnected,
            discord_since = Noon.AddMinutes(2).AddMilliseconds(-1),
            discord_latency_ms = null,
            discord_guilds = null,
            steam_state = SteamState.LoggedOn,
            steam_since = Noon.AddMinutes(1).AddMilliseconds(789),
            steam_last_result = null,
            steam_token_expires = new DateOnly(2027, 5, 3)
        };

        // Every column, so a test compares whole rows
        static (ulong, string?, DateTime, DateTime, DateTime?, string, DateTime?, int?, int?, string, DateTime?, string?, DateOnly?) Columns(BotStatus row) =>
            (row.run_id, row.version, row.started_at, row.heartbeat_at, row.stopped_at, row.discord_state, row.discord_since, row.discord_latency_ms,
             row.discord_guilds, row.steam_state, row.steam_since, row.steam_last_result, row.steam_token_expires);

        static BotEvent Event(uint seq, string kind = "discord.connected", string? detail = null, ulong runId = RunId) => new()
        {
            run_id = runId,
            seq = seq,
            at = Noon.AddSeconds(seq).AddMilliseconds(7),
            kind = kind,
            level = EventLevel.Info,
            detail = detail
        };

        static JobRun Run(uint seq, string result, DateTime? ended = null, uint? duration = null, string? detail = null) => new()
        {
            run_id = RunId,
            seq = seq,
            job = "ArmorJob.AddColorArrays",
            started_at = Noon.AddMilliseconds(5),
            ended_at = ended,
            result = result,
            duration_ms = duration,
            detail = detail
        };

        // Times come back as the UTC times written, to the millisecond, and the token's date as a date
        [Fact]
        public async Task StatusRowIsWrittenAndReadBack()
        {
            var db = await StartAsync();
            var status = db.Status();

            Assert.Null(await status.GetStatus());

            await status.SaveStatus(Status());

            var stored = await status.GetStatus();
            Assert.NotNull(stored);
            Assert.Equal(RunId, stored.run_id);
            Assert.Equal("c28e305aa0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5", stored.version);
            Assert.Equal(Noon.AddMilliseconds(123), stored.started_at);
            Assert.Equal(Noon.AddMinutes(1).AddMilliseconds(456), stored.heartbeat_at);
            Assert.Null(stored.stopped_at);
            Assert.Equal(DiscordState.Connected, stored.discord_state);
            Assert.Equal(Noon.AddSeconds(3), stored.discord_since);
            Assert.Equal(42, stored.discord_latency_ms);
            Assert.Equal(1234, stored.discord_guilds);
            Assert.Equal(SteamState.NeedsSignIn, stored.steam_state);
            Assert.Equal(Noon.AddSeconds(30).AddMilliseconds(789), stored.steam_since);
            Assert.Equal("Revoked", stored.steam_last_result);
            Assert.Equal(new DateOnly(2027, 5, 2), stored.steam_token_expires);
        }

        // One row, which the next run's first write replaces, and a clean stop sets its stop
        [Fact]
        public async Task StatusRowIsOneRowThatEachWriteReplaces()
        {
            var db = await StartAsync();
            var status = db.Status();

            await status.SaveStatus(Status());

            var next = Status(runId: 7);
            next.version = null;
            next.discord_state = DiscordState.Disconnected;
            next.discord_latency_ms = null;
            next.discord_guilds = null;
            next.steam_state = SteamState.LoggedOn;
            next.steam_last_result = null;
            next.steam_token_expires = null;
            next.stopped_at = Noon.AddHours(1);
            await status.SaveStatus(next);
            await status.SaveStatus(next);

            Assert.Equal(1, await CountAsync(MariaDbFixture.StatusTable));

            var stored = await status.GetStatus();
            Assert.NotNull(stored);
            Assert.Equal(7UL, stored.run_id);
            Assert.Null(stored.version);
            Assert.Equal(Noon.AddHours(1), stored.stopped_at);
            Assert.Equal(DiscordState.Disconnected, stored.discord_state);
            Assert.Null(stored.discord_latency_ms);
            Assert.Equal(SteamState.LoggedOn, stored.steam_state);
            Assert.Null(stored.steam_last_result);
            Assert.Null(stored.steam_token_expires);
            Assert.Equal(1, (await fixture.RootQueryAsync<int>($"SELECT id FROM {MariaDbFixture.StatusTable}")).Single());
        }

        // A clean stop's write lands on the run's own earlier write, and every column takes the stop's value: the rules that
        // keep a stopped run's row look at the row as it was, not at the stop this write sets
        [Fact]
        public async Task StopWriteReplacesTheRunsEarlierWrite()
        {
            var db = await StartAsync();
            var status = db.Status();

            await status.SaveStatus(Status());
            await status.SaveStatus(Stop());

            var stopped = await status.GetStatus();
            Assert.NotNull(stopped);
            Assert.Equal(Columns(Stop()), Columns(stopped));
        }

        // The stop write is a run's last. A write of the same run that the bot gave up on when it stopped, and that lands
        // after the stop write, changes nothing in any column, or the next start would count a clean stop as an unclean
        // end. The next run's write replaces the row as always.
        [Fact]
        public async Task StoppedRunIsKeptWhenAnEarlierWriteOfItLandsLate()
        {
            var db = await StartAsync();
            var status = db.Status();

            await status.SaveStatus(Stop());
            await status.SaveStatus(Status());

            var kept = await status.GetStatus();
            Assert.NotNull(kept);
            Assert.Equal(Columns(Stop()), Columns(kept));

            await status.SaveStatus(Status(runId: 7));

            var next = await status.GetStatus();
            Assert.NotNull(next);
            Assert.Equal(Columns(Status(runId: 7)), Columns(next));
        }

        // A batch that's written again, whole or in part, lands once
        [Fact]
        public async Task EventsThatAreWrittenAgainLandOnce()
        {
            var db = await StartAsync();
            var status = db.Status();

            await status.AddEvents([Event(1), Event(2, "bot.started", "version c28e305"), Event(3)]);
            await status.AddEvents([Event(1), Event(2, "bot.started", "version c28e305"), Event(3)]);
            await status.AddEvents([Event(3), Event(4, "job.failed")]);

            // Another run's events with the same numbers are other events
            await status.AddEvents([Event(1, runId: 5)]);

            Assert.Equal(5, await CountAsync(MariaDbFixture.EventTable));

            var rows = await fixture.RootQueryAsync<(ulong, uint, DateTime, string, string, string?)>(
                $"SELECT run_id, seq, at, kind, level, detail FROM {MariaDbFixture.EventTable} WHERE run_id = @RunId ORDER BY seq", new { RunId });

            Assert.Equal([1u, 2u, 3u, 4u], rows.Select(row => row.Item2));
            Assert.Equal((RunId, 2u, Noon.AddSeconds(2).AddMilliseconds(7), "bot.started", EventLevel.Info, "version c28e305"), rows[1]);
            Assert.Equal("job.failed", rows[3].Item4);
        }

        [Fact]
        public async Task EventsComeInBatchesOfAHundred()
        {
            var db = await StartAsync();

            await db.Status().AddEvents(Enumerable.Range(1, 100).Select(seq => Event((uint)seq)).ToList());

            Assert.Equal(100, await CountAsync(MariaDbFixture.EventTable));
        }

        // Strict mode would refuse a detail that's too long, and with it the whole batch, which would never go through
        [Fact]
        public async Task LongTextIsClipped()
        {
            var db = await StartAsync();
            var status = db.Status();
            var detail = string.Concat(Enumerable.Range(0, 40).Select(i => $"{i:00000000}\U0001F600"));
            var stored = string.Concat(Enumerable.Range(0, 33).Select(i => $"{i:00000000}\U0001F600")) + "000";

            Assert.Equal(300, stored.EnumerateRunes().Count());

            await status.AddEvents([Event(1, detail: detail)]);
            await status.SaveJobRuns([Run(1, JobResult.Ok, Noon.AddSeconds(1), 1000, detail)]);
            await status.SaveJobStatus([new JobStatus { job = "ArmorJob.AddColorArrays", schedule = "0 * * * *", last_started_at = Noon, last_result = JobResult.Ok, last_detail = detail, updated_at = Noon }]);
            var row = Status();
            row.steam_last_result = new string('r', 50);
            await status.SaveStatus(row);

            Assert.Equal([stored], await fixture.RootQueryAsync<string>($"SELECT detail FROM {MariaDbFixture.EventTable}"));
            Assert.Equal([stored], await fixture.RootQueryAsync<string>($"SELECT detail FROM {MariaDbFixture.JobRunTable}"));
            Assert.Equal([stored], await fixture.RootQueryAsync<string>($"SELECT last_detail FROM {MariaDbFixture.JobStatusTable}"));
            Assert.Equal(new string('r', 40), (await status.GetStatus())!.steam_last_result);
        }

        // A run that was cut off gets its end when it ends, and stays cut_off, whatever the end's write says. The write from
        // when it was cut off, if it lands late, doesn't take the end away.
        [Fact]
        public async Task CutOffRunGetsItsEndAndStaysCutOff()
        {
            var db = await StartAsync();
            var status = db.Status();

            await status.SaveJobRuns([Run(1, JobResult.CutOff, detail: "still running when the time limit of 270 s cut it off")]);
            await status.SaveJobRuns([Run(1, JobResult.Ok, Noon.AddSeconds(312), 312000, "ended after 312 s")]);

            Assert.Equal([JobResult.CutOff], await fixture.RootQueryAsync<string>($"SELECT result FROM {MariaDbFixture.JobRunTable}"));

            await status.SaveJobRuns([Run(1, JobResult.CutOff, detail: "still running when the time limit of 270 s cut it off")]);

            var rows = await fixture.RootQueryAsync<(DateTime?, string, uint?, string?)>($"SELECT ended_at, result, duration_ms, detail FROM {MariaDbFixture.JobRunTable}");

            Assert.Equal([(Noon.AddSeconds(312), JobResult.CutOff, 312000u, "ended after 312 s")], rows);
        }

        // A run's row is written again when its write went through but the bot didn't hear, and a run that ended has
        // nothing for a write to change, whatever it says
        [Fact]
        public async Task JobRunsAreOneRowEach()
        {
            var db = await StartAsync();
            var status = db.Status();

            await status.SaveJobRuns([Run(1, JobResult.Ok, Noon.AddSeconds(2), 2000, "deleted 0 audit rows"), Run(2, JobResult.Failed, Noon.AddSeconds(3), 3000, "TimeoutException")]);
            await status.SaveJobRuns([Run(1, JobResult.Ok, Noon.AddSeconds(2), 2000, "deleted 0 audit rows"), Run(2, JobResult.Failed, Noon.AddSeconds(3), 3000, "TimeoutException")]);
            await status.SaveJobRuns([Run(3, JobResult.Skipped, Noon.AddSeconds(4), detail: "the run that was cut off still runs")]);

            var rows = await fixture.RootQueryAsync<(uint, string, DateTime, DateTime?, string, uint?, string?)>(
                $"SELECT seq, job, started_at, ended_at, result, duration_ms, detail FROM {MariaDbFixture.JobRunTable} ORDER BY seq");

            Assert.Equal(3, rows.Count);
            Assert.Equal((1u, "ArmorJob.AddColorArrays", Noon.AddMilliseconds(5), Noon.AddSeconds(2), JobResult.Ok, 2000u, "deleted 0 audit rows"), rows[0]);
            Assert.Equal(JobResult.Failed, rows[1].Item5);
            Assert.Equal((JobResult.Skipped, (uint?)null), (rows[2].Item5, rows[2].Item6));
        }

        // Each start writes every job's schedule and next run without a last run, which keeps the one the row has, such as
        // the run before a restart. After a run, the last run is all its own.
        [Fact]
        public async Task JobStatusWithoutALastRunKeepsTheOneTheRowHas()
        {
            var db = await StartAsync();
            var status = db.Status();

            await status.SaveJobStatus([new JobStatus
            {
                job = "HousekeepingJob.ApplyRetention",
                schedule = "30 4 * * *",
                next_run_at = Noon,
                last_started_at = Noon.AddDays(-1),
                last_ended_at = Noon.AddDays(-1).AddSeconds(2),
                last_result = JobResult.Ok,
                last_duration_ms = 2000,
                last_detail = "deleted 1 event",
                updated_at = Noon.AddDays(-1)
            }]);

            await status.SaveJobStatus([new JobStatus { job = "HousekeepingJob.ApplyRetention", schedule = "45 4 * * *", next_run_at = Noon.AddDays(1), updated_at = Noon }]);

            var kept = await fixture.RootQueryAsync<(string, DateTime?, DateTime?, DateTime?, string?, uint?, string?, DateTime)>(
                $"SELECT schedule, next_run_at, last_started_at, last_ended_at, last_result, last_duration_ms, last_detail, updated_at FROM {MariaDbFixture.JobStatusTable}");

            Assert.Equal([("45 4 * * *", Noon.AddDays(1), Noon.AddDays(-1), Noon.AddDays(-1).AddSeconds(2), JobResult.Ok, 2000u, "deleted 1 event", Noon)], kept);

            // A run that was cut off has no end yet, so the end of the run before goes too
            await status.SaveJobStatus([new JobStatus { job = "HousekeepingJob.ApplyRetention", schedule = "45 4 * * *", next_run_at = Noon.AddDays(1), last_started_at = Noon,
                last_result = JobResult.CutOff, last_detail = "still running", updated_at = Noon }]);

            var cutOff = await fixture.RootQueryAsync<(DateTime?, DateTime?, string?, uint?, string?)>(
                $"SELECT last_started_at, last_ended_at, last_result, last_duration_ms, last_detail FROM {MariaDbFixture.JobStatusTable}");

            Assert.Equal([(Noon, (DateTime?)null, JobResult.CutOff, (uint?)null, "still running")], cutOff);
        }

        // A job that was renamed or removed leaves no row behind
        [Fact]
        public async Task JobsThatNoLongerExistAreDeleted()
        {
            var db = await StartAsync();
            var status = db.Status();

            await status.SaveJobStatus(new[] { "ArmorJob.AddColorArrays", "SteamTokenJob.RenewSteamToken", "OldJob.Gone" }
                .Select(job => new JobStatus { job = job, schedule = "0 * * * *", updated_at = Noon }).ToList());

            Assert.Equal(1, await status.DeleteOtherJobs(["ArmorJob.AddColorArrays", "SteamTokenJob.RenewSteamToken", "HousekeepingJob.ApplyRetention"]));
            Assert.Equal(["ArmorJob.AddColorArrays", "SteamTokenJob.RenewSteamToken"],
                await fixture.RootQueryAsync<string>($"SELECT job FROM {MariaDbFixture.JobStatusTable} ORDER BY job"));
        }

        // The minute the bot ran, with whether each was up, once whatever how often it's written
        [Fact]
        public async Task SamplesAreOneRowForEachMinute()
        {
            var db = await StartAsync();
            var status = db.Status();

            await status.SaveSamples([new UptimeSample { minute = Noon, discord_up = true, steam_up = false }, new UptimeSample { minute = Noon.AddMinutes(1), discord_up = true, steam_up = true }]);
            await status.SaveSamples([new UptimeSample { minute = Noon.AddMinutes(1), discord_up = false, steam_up = true }]);

            var rows = await fixture.RootQueryAsync<(DateTime, bool, bool)>($"SELECT minute, discord_up, steam_up FROM {MariaDbFixture.SampleTable} ORDER BY minute");

            Assert.Equal([(Noon, true, false), (Noon.AddMinutes(1), false, true)], rows);
        }
    }
}
