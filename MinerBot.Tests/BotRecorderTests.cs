using System.Reflection;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using Microsoft.Extensions.Configuration;
using MinerBot_2._0.Status;
using SteamKit2;
using SteamWebAPIAccess;

namespace MinerBot.Tests
{
    // What the bot keeps of its status and history until it's written: within its limits, safe from any thread, and never
    // a reason for its caller to stop
    public class BotRecorderTests
    {
        static readonly DateTimeOffset Noon = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

        readonly FakeClock clock = new() { UtcNow = Noon };

        // The bot's settings, the way environment variables reach it
        internal static IConfiguration Settings(string? revision = null, string? botConnection = null)
        {
            var settings = new Dictionary<string, string?>();

            if (revision != null)
                settings[BotVersion.Setting] = revision;

            if (botConnection != null)
                settings["ConnectionStrings:BotConnection"] = botConnection;

            return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        }

        BotRecorder Recorder(string? revision = null) => new(clock, Settings(revision));

        static List<string> Kinds(BotRecorder recorder) => recorder.PeekEvents(int.MaxValue).Select(row => row.kind).ToList();

        static JobRun Run(uint seq, string result = JobResult.Ok, string job = "ArmorJob.AddColorArrays") =>
            new() { seq = seq, job = job, started_at = Noon.UtcDateTime, result = result };

        [Fact]
        public void EachRunStartsWithItsVersion()
        {
            var stamped = Recorder("C28E305AA0B1");
            var plain = Recorder("");

            Assert.Equal("c28e305aa0b1", stamped.Version);
            Assert.Null(plain.Version);
            var started = Assert.Single(stamped.PeekEvents(10));
            Assert.Equal((BotEvents.BotStarted, (string?)"version c28e305aa0b1"), (started.kind, started.detail));
            Assert.Equal("with no version, because the image wasn't built from a commit", Assert.Single(plain.PeekEvents(10)).detail);

            var (status, _) = stamped.StatusToWrite();

            Assert.Equal((stamped.RunId, "c28e305aa0b1", Noon.UtcDateTime, DiscordState.Disconnected, SteamState.Offline),
                (status.run_id, status.version, status.started_at, status.discord_state, status.steam_state));
            Assert.NotEqual(stamped.RunId, plain.RunId);
        }

        // 7 to 40 hex digits, as deploy.sh passes the commit, and nothing else
        [Theory]
        [InlineData("c28e305", "c28e305")]
        [InlineData("C28E305AA0B1C2D3E4F5A6B7C8D9E0F1A2B3C4D5", "c28e305aa0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5")]
        [InlineData("c28e30", null)]
        [InlineData("c28e305aa0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5a", null)]
        [InlineData("g28e305", null)]
        [InlineData(" c28e305", null)]
        [InlineData("c28e305\n", null)]
        [InlineData("main", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void VersionIsACommitOrNothing(string? value, string? version)
        {
            Assert.Equal(version, BotVersion.Parse(value));
        }

        [Fact]
        public void EventsAreNumberedInEachRunWithTheirLevelAndTime()
        {
            var first = Recorder();
            clock.UtcNow = Noon.AddSeconds(5);
            first.Record(BotEvents.JobFailed, "ArmorJob.AddColorArrays failed: TimeoutException");
            var second = Recorder();

            var events = first.PeekEvents(10);

            Assert.Equal([(1u, BotEvents.BotStarted, EventLevel.Info, Noon.UtcDateTime), (2u, BotEvents.JobFailed, EventLevel.Error, Noon.AddSeconds(5).UtcDateTime)],
                events.Select(row => (row.seq, row.kind, row.level, row.at)));
            Assert.All(events, row => Assert.Equal(first.RunId, row.run_id));
            Assert.All(events, row => Assert.Equal(DateTimeKind.Utc, row.at.Kind));
            Assert.Equal(1u, Assert.Single(second.PeekEvents(10)).seq);
        }

        [Fact]
        public void KindsThatArentOnTheListAreDropped()
        {
            var recorder = Recorder();

            recorder.Record("made.up", "anything");
            recorder.Record("", null);
            recorder.Record(null!, null);

            Assert.Equal([BotEvents.BotStarted], Kinds(recorder));
        }

        // A database that's down for hours costs the oldest history, never the bot's memory
        [Fact]
        public void QueuesKeepTheirNewestUpToTheirLimits()
        {
            var recorder = Recorder();

            for (var i = 0; i < 1005; i++)
                recorder.Record(BotEvents.AlertSent, $"alert {i}");

            for (var i = 0; i < 505; i++)
                recorder.RecordJobRun(Run(recorder.NewJobRun()), "0 * * * *", null);

            for (var i = 0; i < 365; i++)
                recorder.Sample(Noon.UtcDateTime.AddMinutes(i), true, true);

            var events = recorder.PeekEvents(int.MaxValue);

            Assert.Equal(BotRecorder.EventLimit, events.Count);
            Assert.Equal(("alert 5", 7u), (events[0].detail, events[0].seq));
            Assert.Equal(1006u, events[^1].seq);
            Assert.Equal(BotRecorder.JobRunLimit, recorder.PeekJobRuns(int.MaxValue).Count);
            Assert.Equal(6u, recorder.PeekJobRuns(1)[0].seq);
            Assert.Equal(BotRecorder.SampleLimit, recorder.PeekSamples(int.MaxValue).Count);
            Assert.Equal(Noon.UtcDateTime.AddMinutes(5), recorder.PeekSamples(1)[0].minute);

            Assert.Equal((6, 5, 5), recorder.TakeDropped());
            Assert.Equal((0, 0, 0), recorder.TakeDropped());
        }

        // An item leaves only once its write went through, and what arrived while it was written stays
        [Fact]
        public void OnlyWhatWasWrittenLeaves()
        {
            var recorder = Recorder();
            var batch = recorder.PeekEvents(100);

            recorder.Record(BotEvents.AlertSent, "after the batch was taken");
            recorder.EventsWritten(batch);

            Assert.Equal([BotEvents.AlertSent], Kinds(recorder));

            // The limit dropped half of a batch while it was written: the rest of it goes, and nothing after it
            var full = Recorder();

            for (var i = 0; i < BotRecorder.EventLimit - 1; i++)
                full.Record(BotEvents.AlertSent, $"alert {i}");

            var taken = full.PeekEvents(100);

            for (var i = 0; i < 50; i++)
                full.Record(BotEvents.AlertSent, $"new {i}");

            full.EventsWritten(taken);

            var left = full.PeekEvents(int.MaxValue);
            Assert.Equal(950, left.Count);
            Assert.Equal(101u, left[0].seq);
        }

        // A run that was cut off and ends after a newer run of its job began changes only its own row
        [Fact]
        public void JobStatusShowsTheNewestRun()
        {
            var recorder = Recorder();
            const string job = "HousekeepingJob.ApplyRetention";

            recorder.JobsFound([(job, "30 4 * * *", Noon.UtcDateTime.AddHours(1))]);

            var cutOff = recorder.NewJobRun();
            var next = recorder.NewJobRun();

            recorder.RecordJobRun(Run(cutOff, JobResult.CutOff, job), "30 4 * * *", Noon.UtcDateTime.AddDays(1));
            recorder.RecordJobRun(Run(next, JobResult.Skipped, job), "30 4 * * *", Noon.UtcDateTime.AddDays(2));

            var ended = Run(cutOff, JobResult.CutOff, job);
            ended.ended_at = Noon.UtcDateTime.AddMinutes(5);
            ended.detail = "ended after 300 s";
            recorder.RecordJobRun(ended, "30 4 * * *", Noon.UtcDateTime.AddDays(2));

            var (row, _) = Assert.Single(recorder.PeekJobStatus(100));

            Assert.Equal((JobResult.Skipped, (DateTime?)Noon.UtcDateTime.AddDays(2), "30 4 * * *"), (row.last_result, row.next_run_at, row.schedule));
            Assert.Equal([cutOff, next, cutOff], recorder.PeekJobRuns(10).Select(run => run.seq));
            Assert.All(recorder.PeekJobRuns(10), run => Assert.Equal(recorder.RunId, run.run_id));
            Assert.Equal([job], recorder.JobNames);
        }

        [Fact]
        public void JobStatusThatChangesWhileItsWrittenIsWrittenAgain()
        {
            var recorder = Recorder();

            recorder.JobsFound([("ArmorJob.AddColorArrays", "0 * * * *", Noon.UtcDateTime)]);

            var batch = recorder.PeekJobStatus(100);
            Assert.Null(Assert.Single(batch).Row.last_result);

            recorder.RecordJobRun(Run(recorder.NewJobRun()), "0 * * * *", Noon.UtcDateTime.AddHours(1));
            recorder.JobStatusWritten(batch);

            var again = Assert.Single(recorder.PeekJobStatus(100));
            Assert.Equal(JobResult.Ok, again.Row.last_result);

            recorder.JobStatusWritten([again]);
            Assert.Empty(recorder.PeekJobStatus(100));
        }

        // A change of a state is written at once; the latency, which changes all the time, waits for the next 30 seconds
        [Fact]
        public void StateChangesAreNoticedAndTheLatencyIsNot()
        {
            var recorder = Recorder();
            var (_, start) = recorder.StatusToWrite();
            var steam = new SteamStatus(SteamConnection.Offline, Noon.AddSeconds(-30), EResult.ServiceUnavailable);

            clock.UtcNow = Noon.AddSeconds(5);
            recorder.UpdateStatus(new DiscordReading(DiscordState.Connecting, false, null, null), steam);
            var (connecting, afterConnecting) = recorder.StatusToWrite();

            Assert.Equal(start + 1, afterConnecting);
            Assert.Equal((DiscordState.Connecting, (DateTime?)Noon.AddSeconds(5).UtcDateTime), (connecting.discord_state, connecting.discord_since));
            Assert.Equal((SteamState.Offline, (DateTime?)Noon.AddSeconds(-30).UtcDateTime, "ServiceUnavailable"),
                (connecting.steam_state, connecting.steam_since, connecting.steam_last_result));

            clock.UtcNow = Noon.AddSeconds(10);
            var loggedOn = new SteamStatus(SteamConnection.LoggedOn, Noon.AddSeconds(8), EResult.ServiceUnavailable, new DateOnly(2027, 5, 2));
            recorder.UpdateStatus(new DiscordReading(DiscordState.Connected, true, 40, 7), loggedOn);
            var (up, afterUp) = recorder.StatusToWrite();

            Assert.Equal(start + 2, afterUp);
            Assert.Equal((DiscordState.Connected, (int?)40, (int?)7), (up.discord_state, up.discord_latency_ms, up.discord_guilds));
            Assert.Equal((SteamState.LoggedOn, (string?)null, (DateOnly?)new DateOnly(2027, 5, 2)), (up.steam_state, up.steam_last_result, up.steam_token_expires));

            clock.UtcNow = Noon.AddSeconds(15);
            recorder.UpdateStatus(new DiscordReading(DiscordState.Connected, true, 55, 7), loggedOn);
            var (later, afterLatency) = recorder.StatusToWrite();

            Assert.Equal(afterUp, afterLatency);
            Assert.Equal((55, (DateTime?)Noon.AddSeconds(10).UtcDateTime), (later.discord_latency_ms, later.discord_since));

            recorder.UpdateStatus(new DiscordReading(DiscordState.Connected, true, 55, 7), loggedOn with { State = SteamConnection.NeedsSignIn, LastLogOnResult = EResult.Revoked });
            var (signIn, afterSignIn) = recorder.StatusToWrite();

            Assert.Equal(afterUp + 1, afterSignIn);
            Assert.Equal((SteamState.NeedsSignIn, "Revoked"), (signIn.steam_state, signIn.steam_last_result));
        }

        // Nothing in the bot makes these fail, but whatever does never reaches the caller
        [Fact]
        public void RecordingNeverThrows()
        {
            var failing = new FailingClock();
            var recorder = new BotRecorder(failing, Settings());
            failing.Fails = true;

            recorder.Record(BotEvents.AlertSent, "the clock fails");
            recorder.JobsFound([("ArmorJob.AddColorArrays", "0 * * * *", DateTime.UtcNow)]);
            recorder.JobsFound(null!);
            recorder.RecordJobRun(Run(1), "0 * * * *", null);
            recorder.RecordJobRun(null!, "0 * * * *", null);
            recorder.UpdateStatus(new DiscordReading(DiscordState.Connected, true, 1, 1), new SteamStatus(SteamConnection.LoggedOn, DateTimeOffset.UtcNow));
            recorder.UpdateStatus(new DiscordReading(DiscordState.Connected, true, 1, 1), null!);
            recorder.Sample(DateTime.UtcNow, true, true);

            Assert.Equal([BotEvents.BotStarted], Kinds(recorder));
        }

        [Fact]
        public async Task ManyThreadsRecordAtOnce()
        {
            var recorder = Recorder();

            await Task.WhenAll(Enumerable.Range(0, 8).Select(thread => Task.Run(() =>
            {
                for (var i = 0; i < 100; i++)
                    recorder.Record(BotEvents.AlertSent, $"{thread}:{i}");
            }, TestContext.Current.CancellationToken)));

            Assert.Equal(Enumerable.Range(1, 801).Select(seq => (uint)seq), recorder.PeekEvents(int.MaxValue).Select(row => row.seq));
        }

        // One list of every kind, each one the events table takes. Written out, because the dashboard's pages will read the
        // rows that hold them.
        [Fact]
        public void EveryKindFitsTheTableAndHasALevel()
        {
            Assert.All(BotEvents.All, kind => Assert.Matches(@"^[a-z_]+\.[a-z_]+\z", kind));
            Assert.All(BotEvents.All, kind => Assert.InRange(kind.Length, 3, 40));
            Assert.All(BotEvents.All, kind => Assert.True(EventLevel.IsKnown(BotEvents.LevelOf(kind)), kind));

            Assert.Equal(["alert.failed", "alert.sent", "bot.started", "bot.stopping", "bot.unclean_end", "database.unreachable", "discord.connected",
                "discord.disconnected", "job.cut_off", "job.failed", "records.dropped", "steam.logged_off", "steam.logged_on", "steam.logon_failed",
                "steam.needs_sign_in", "steam.offline", "steam.token_renewed", "steam.token_saved"], BotEvents.All.Order(StringComparer.Ordinal));
        }

        // Every constant of the bot's own and every kind Steam reports are on the list, so none is dropped
        [Fact]
        public void EveryKindTheCodeRecordsIsOnTheList()
        {
            static List<string> Constants(Type type) => type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!)
                .ToList();

            Assert.All(Constants(typeof(BotEvents)), kind => Assert.Contains(kind, BotEvents.All));
            Assert.Equal(Constants(typeof(SteamEvents)).Order(), SteamEvents.All.Order());
            Assert.All(SteamEvents.All, kind => Assert.Contains(kind, BotEvents.All));
            Assert.Equal(Constants(typeof(BotEvents)).Count + SteamEvents.All.Count, BotEvents.All.Count);
        }

        sealed class FailingClock : TimeProvider
        {
            public bool Fails;

            public override DateTimeOffset GetUtcNow() => Fails ? throw new InvalidOperationException("The clock failed") : Noon;
        }
    }
}
