using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using DataAccessLibrary;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MinerBot_2._0.Status;
using MySql.Data.MySqlClient;
using SteamWebAPIAccess;

namespace MinerBot.Tests
{
    // Writing the status and history: an item leaves the recorder only once its write went through, a database that's down
    // costs only the recording, and nothing ever leaves the writer's loop. The ticks are called one by one here, on a clock
    // the test moves, except where the loop itself is what's tested.
    public class StatusWriterTests
    {
        // Made up, and never connected to: FakeStatusData stands in for the database
        const string Connection = "Server=127.0.0.1;Database=bot_db;Uid=minerbot-bot;Pwd=password";

        static readonly DateTimeOffset Noon = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

        readonly FakeClock clock = new() { UtcNow = Noon };
        readonly FakeStatusData data = new();
        readonly RecordingLogger<StatusWriter> logger = new();
        readonly BotRecorder recorder;
        readonly StatusWriter writer;

        public StatusWriterTests()
        {
            recorder = new BotRecorder(clock, BotRecorderTests.Settings());
            writer = Writer(Connection);
        }

        StatusWriter Writer(string? connection, TimeProvider? clockToUse = null) =>
            new(recorder, data, BotRecorderTests.Settings(botConnection: connection), clockToUse ?? clock, logger) { Interval = TimeSpan.FromMilliseconds(20) };

        Task TickAsync() => writer.TickAsync(TestContext.Current.CancellationToken);

        // MySql.Data's messages can name the login, as this one does
        static MySqlException Refused(int number) =>
            (MySqlException)typeof(MySqlException)
                .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string), typeof(int), typeof(Exception)])!
                .Invoke([$"Access denied for user 'minerbot-bot'@'localhost'", number, new SocketException()]);

        static async Task WaitUntilAsync(Func<bool> condition, string failure)
        {
            var clock = Stopwatch.StartNew();

            while (!condition())
            {
                Assert.True(clock.Elapsed < WaitLimit, failure);
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        [Fact]
        public async Task WhatWasWrittenLeavesTheRecorder()
        {
            recorder.Record(BotEvents.AlertSent, "MinerBot is signed in to Steam again.");
            recorder.RecordJobRun(new JobRun { seq = recorder.NewJobRun(), job = "ArmorJob.AddColorArrays", started_at = Noon.UtcDateTime, ended_at = Noon.UtcDateTime,
                result = JobResult.Ok }, "0 * * * *", Noon.UtcDateTime.AddHours(1));
            recorder.JobsFound([("ArmorJob.AddColorArrays", "0 * * * *", Noon.UtcDateTime.AddHours(1))]);
            recorder.Sample(Noon.UtcDateTime, true, true);

            await TickAsync();

            Assert.Equal([BotEvents.BotStarted, BotEvents.AlertSent], data.Events.Select(row => row.kind));
            Assert.Single(data.JobRuns);
            Assert.Equal("ArmorJob.AddColorArrays", Assert.Single(data.Jobs).job);
            Assert.Single(data.Samples);
            Assert.Equal(["ArmorJob.AddColorArrays"], Assert.Single(data.KeptJobs));

            var status = Assert.Single(data.Statuses);
            Assert.Equal((recorder.RunId, Noon.UtcDateTime, (DateTime?)null), (status.run_id, status.heartbeat_at, status.stopped_at));

            Assert.Empty(recorder.PeekEvents(100));
            Assert.Empty(recorder.PeekJobRuns(100));
            Assert.Empty(recorder.PeekJobStatus(100));
            Assert.Empty(recorder.PeekSamples(100));

            // Nothing new, and the status row isn't due: nothing is written, and the jobs that no longer exist went once
            var calls = data.Calls;
            clock.UtcNow = Noon.AddSeconds(5);
            await TickAsync();

            Assert.Equal(calls, data.Calls);
            Assert.Single(data.KeptJobs);
        }

        // 30 seconds, then twice as long each time, up to 5 minutes, and never sooner. Everything stays meanwhile, and the
        // whole outage gets one warning, with the error's number and nothing of its message.
        [Fact]
        public async Task FailedWriteKeepsEverythingAndWaitsLongerEachTime()
        {
            data.Failure = Refused(1045);
            recorder.Record(BotEvents.AlertSent, "kept");

            await TickAsync();

            Assert.Equal([BotEvents.BotStarted, BotEvents.AlertSent], recorder.PeekEvents(100).Select(row => row.kind));
            Assert.Empty(data.Events);

            var attempts = new List<int>();
            var last = data.Calls;

            for (var second = 1; second <= 1200; second++)
            {
                clock.UtcNow = Noon.AddSeconds(second);
                await TickAsync();

                if (data.Calls != last)
                {
                    attempts.Add(second);
                    last = data.Calls;
                }
            }

            Assert.Equal([30, 90, 210, 450, 750, 1050], attempts);
            Assert.Equal(2, recorder.PeekEvents(100).Count);

            var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.Contains("(MySqlException, MySQL error 1045)", warning.Message);
            Assert.Null(warning.Exception);
            Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("minerbot-bot") || entry.Exception != null);
        }

        // A write that fails keeps what it was writing, and the next attempt writes it
        [Fact]
        public async Task ItemsStayUntilTheirWriteGoesThrough()
        {
            await TickAsync();

            data.Failure = new TimeoutException("The database didn't answer within 15 seconds");
            recorder.Record(BotEvents.AlertSent, "kept");
            recorder.Sample(Noon.UtcDateTime, true, true);
            clock.UtcNow = Noon.AddSeconds(5);
            await TickAsync();

            Assert.Equal([BotEvents.AlertSent], recorder.PeekEvents(100).Select(row => row.kind));
            Assert.Single(recorder.PeekSamples(100));

            data.Failure = null;
            clock.UtcNow = Noon.AddSeconds(35);
            await TickAsync();

            Assert.Contains(data.Events, row => row.detail == "kept");
            Assert.Single(data.Samples);
            Assert.Empty(recorder.PeekSamples(100));
        }

        // One line when writes work again, then the outage, and what the limits dropped meanwhile, go into the history
        [Fact]
        public async Task RecoveryIsLoggedAndTheOutageRecorded()
        {
            data.Failure = new TimeoutException("The database didn't answer within 15 seconds");
            await TickAsync();

            // Each limit overflows by its own count, so each count shows under its own name
            for (var i = 0; i < 1005; i++)
                recorder.Record(BotEvents.AlertSent, $"alert {i}");

            for (var i = 0; i < BotRecorder.JobRunLimit + 5; i++)
                recorder.RecordJobRun(new JobRun { seq = recorder.NewJobRun(), job = "ArmorJob.AddColorArrays", started_at = Noon.UtcDateTime, ended_at = Noon.UtcDateTime,
                    result = JobResult.Ok }, "0 * * * *", null);

            for (var i = 0; i < BotRecorder.SampleLimit + 7; i++)
                recorder.Sample(Noon.UtcDateTime.AddMinutes(i), true, true);

            data.Failure = null;
            clock.UtcNow = Noon.AddSeconds(30);
            await TickAsync();

            Assert.Equal(1000, data.Events.Count);
            Assert.Equal(BotRecorder.JobRunLimit, data.JobRuns.Count);
            Assert.Equal(BotRecorder.SampleLimit, data.Samples.Count);
            Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("takes the bot's status and history again"));
            Assert.Equal(
            [
                (BotEvents.DatabaseUnreachable, (string?)"from 2026-11-14 12:00:00 to 2026-11-14 12:00:30 UTC"),
                (BotEvents.RecordsDropped, "6 events, 5 job runs and 7 minutes, the oldest, because the bot couldn't keep more")
            ], recorder.PeekEvents(100).Select(row => (row.kind, row.detail)));

            clock.UtcNow = Noon.AddSeconds(35);
            await TickAsync();

            Assert.Equal([BotEvents.DatabaseUnreachable, BotEvents.RecordsDropped], data.Events.Skip(1000).Select(row => row.kind));
            Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        }

        [Fact]
        public async Task StatusIsWrittenEvery30SecondsAndAtOnceAfterAChange()
        {
            var steam = new SteamStatus(SteamConnection.LoggedOn, Noon);
            var up = new DiscordReading(DiscordState.Connected, true, 40, 3);

            recorder.UpdateStatus(up, steam);
            await TickAsync();

            foreach (var second in new[] { 5, 10, 29, 30, 35, 59, 60 })
            {
                clock.UtcNow = Noon.AddSeconds(second);
                recorder.UpdateStatus(up with { LatencyMs = second }, steam);
                await TickAsync();
            }

            clock.UtcNow = Noon.AddSeconds(62);
            recorder.UpdateStatus(new DiscordReading(DiscordState.Connecting, false, null, null), steam);
            await TickAsync();

            clock.UtcNow = Noon.AddSeconds(63);
            await TickAsync();

            Assert.Equal([0, 30, 60, 62], data.Statuses.Select(status => (int)(status.heartbeat_at - Noon.UtcDateTime).TotalSeconds));
            Assert.Equal((30, DiscordState.Connected), (data.Statuses[1].discord_latency_ms, data.Statuses[1].discord_state));
            Assert.Equal(DiscordState.Connecting, data.Statuses[^1].discord_state);
        }

        // The run before's row, read once before the first write: a run that never stopped cleanly crashed, was killed, or
        // went with a reboot
        [Theory]
        [InlineData("crashed", true)]
        [InlineData("stopped", false)]
        [InlineData("this run", false)]
        [InlineData("no row", false)]
        public async Task FirstWriteTellsWhetherTheRunBeforeStoppedCleanly(string before, bool unclean)
        {
            data.Before = before switch
            {
                "no row" => null,
                _ => new BotStatus
                {
                    run_id = before == "this run" ? recorder.RunId : 7,
                    started_at = Noon.UtcDateTime.AddHours(-5),
                    heartbeat_at = Noon.UtcDateTime.AddMinutes(-3),
                    stopped_at = before == "stopped" ? Noon.UtcDateTime.AddMinutes(-3) : null,
                    discord_state = DiscordState.Connected,
                    steam_state = SteamState.LoggedOn
                }
            };

            await TickAsync();
            clock.UtcNow = Noon.AddSeconds(30);
            await TickAsync();

            Assert.Equal(1, data.Reads);
            Assert.Equal(2, data.Statuses.Count);

            var events = data.Events.Where(row => row.kind == BotEvents.UncleanEnd).Select(row => row.detail).ToList();

            if (unclean)
                Assert.Equal(["the run that started at 2026-11-14 07:00:00 UTC never stopped cleanly; its last heartbeat was at 2026-11-14 11:57:00 UTC"], events);
            else
                Assert.Empty(events);
        }

        // A row the database refuses is the bot's own mistake: it goes, rather than failing every write after it
        [Fact]
        public async Task RowsTheDatabaseRefusesAreDropped()
        {
            data.RefuseEvents = true;
            recorder.Sample(Noon.UtcDateTime, true, false);

            await TickAsync();

            Assert.Empty(recorder.PeekEvents(100));
            Assert.Empty(data.Events);
            Assert.Single(data.Samples);
            Assert.True(logger.Logged(LogLevel.Error, "MinerBot's database refused rows the bot recorded, so they were dropped (ArgumentException)"));

            data.RefuseEvents = false;
            recorder.Record(BotEvents.AlertSent, "after");
            clock.UtcNow = Noon.AddSeconds(5);
            await TickAsync();

            Assert.Equal([BotEvents.AlertSent], data.Events.Select(row => row.kind));
            Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
        }

        // The status row with the time it stopped, then whatever is queued
        [Fact]
        public async Task StopWritesTheStopTimeThenWhatsQueued()
        {
            await TickAsync();
            recorder.Record(BotEvents.BotStopping);
            clock.UtcNow = Noon.AddSeconds(3);

            await writer.StopWriteAsync();

            var last = data.Statuses[^1];
            Assert.Equal((Noon.AddSeconds(3).UtcDateTime, (DateTime?)Noon.AddSeconds(3).UtcDateTime), (last.heartbeat_at, last.stopped_at));
            Assert.Equal(BotEvents.BotStopping, data.Events[^1].kind);
            Assert.Equal(2, data.Statuses.Count);
        }

        // A bot that stops before the writer's first tick, 5 seconds after the start, makes its first write when it stops.
        // That write still reads the run before's row first, so a run that never stopped cleanly isn't overwritten unseen.
        [Fact]
        public async Task StopBeforeTheFirstTickStillTellsOfAnUncleanEnd()
        {
            data.Before = new BotStatus
            {
                run_id = 7,
                started_at = Noon.UtcDateTime.AddHours(-5),
                heartbeat_at = Noon.UtcDateTime.AddMinutes(-3),
                discord_state = DiscordState.Connected,
                steam_state = SteamState.LoggedOn
            };

            await writer.StopWriteAsync().WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.Equal(1, data.Reads);
            Assert.Equal((DateTime?)Noon.UtcDateTime, Assert.Single(data.Statuses).stopped_at);
            Assert.Equal(["the run that started at 2026-11-14 07:00:00 UTC never stopped cleanly; its last heartbeat was at 2026-11-14 11:57:00 UTC"],
                data.Events.Where(row => row.kind == BotEvents.UncleanEnd).Select(row => row.detail));
        }

        // A database that doesn't answer can't hold up the bot's stop: the last write gives up after 5 seconds in all
        [Fact]
        public async Task StopWriteGivesUpWithinItsLimit()
        {
            data.Hold = new TaskCompletionSource().Task;
            var timer = Stopwatch.StartNew();

            await writer.StopWriteAsync().WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.InRange(timer.Elapsed, TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(8));
            Assert.Empty(data.Statuses);
            Assert.True(logger.Logged(LogLevel.Warning, "The bot's last status and history before it stopped weren't all written"));
        }

        // The database is back while the writer waits to try again, and the bot stops first: the last write is the one that
        // finds it back, so the outage, and what the limits dropped meanwhile, go with it, as there's no write after it
        [Fact]
        public async Task OutageThatEndsWithTheStopIsRecorded()
        {
            data.Failure = new TimeoutException("The database didn't answer within 15 seconds");
            await TickAsync();

            for (var minute = 0; minute < BotRecorder.SampleLimit + 60; minute++)
                recorder.Sample(Noon.UtcDateTime.AddMinutes(minute), true, true);

            data.Failure = null;
            clock.UtcNow = Noon.AddHours(7);
            await writer.StopWriteAsync();

            Assert.Equal(
            [
                (BotEvents.DatabaseUnreachable, (string?)"from 2026-11-14 12:00:00 to 2026-11-14 19:00:00 UTC"),
                (BotEvents.RecordsDropped, "0 events, 0 job runs and 60 minutes, the oldest, because the bot couldn't keep more")
            ], data.Events.Where(row => row.kind != BotEvents.BotStarted).Select(row => (row.kind, row.detail)));
            Assert.Equal(BotRecorder.SampleLimit, data.Samples.Count);
            Assert.Equal(Noon.AddHours(7).UtcDateTime, data.Statuses[^1].stopped_at);
            Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("takes the bot's status and history again"));
        }

        // The same with the event queue full: the last write's own two events push two more of the oldest out, and
        // records.dropped counts both, as no write after it could
        [Fact]
        public async Task StopWithAFullEventQueueCountsEveryEventItLost()
        {
            data.Failure = new TimeoutException("The database didn't answer within 15 seconds");
            await TickAsync();

            for (var i = 0; i < 1005; i++)
                recorder.Record(BotEvents.AlertSent, $"alert {i}");

            data.Failure = null;
            clock.UtcNow = Noon.AddMinutes(10);
            await writer.StopWriteAsync();

            // bot.started, the 1,005 alerts and the outage's two events are 1,008, and the queue kept the newest 1,000
            Assert.Equal(1000, data.Events.Count);
            Assert.Equal("alert 7", data.Events[0].detail);
            Assert.Equal(
            [
                (BotEvents.DatabaseUnreachable, (string?)"from 2026-11-14 12:00:00 to 2026-11-14 12:10:00 UTC"),
                (BotEvents.RecordsDropped, "8 events, 0 job runs and 0 minutes, the oldest, because the bot couldn't keep more")
            ], data.Events.TakeLast(2).Select(row => (row.kind, row.detail)));
            Assert.Equal((0, 0, 0), recorder.TakeDropped());
        }

        // As the host stops it: the loop ends, and the last write follows
        [Fact]
        public async Task StoppingTheBotMakesTheLastWrite()
        {
            await writer.StartAsync(TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => data.Statuses.Count > 0, "The writer never wrote");
            await writer.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.True(writer.ExecuteTask!.IsCompletedSuccessfully);
            Assert.Equal(Noon.UtcDateTime, data.Statuses[^1].stopped_at);
        }

        // A write that's under way when the bot stops, on a database that stopped answering, is given up at once, and the
        // last write then gives up within its own limit, so a hung database can't hold up the bot's stop. The run's first
        // tick went through, so what hangs is a batch, as on most ticks.
        [Fact]
        public async Task StoppingTheBotGivesUpAWriteThatHangs()
        {
            await TickAsync();
            var calls = data.Calls;
            data.Hold = new TaskCompletionSource().Task;
            recorder.Record(BotEvents.AlertSent, "held");
            writer.StopLimit = TimeSpan.FromMilliseconds(200);

            await writer.StartAsync(TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => data.Calls > calls, "The writer never wrote the event");

            var timer = Stopwatch.StartNew();
            await writer.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"The stop took {timer.Elapsed}");
            Assert.True(writer.ExecuteTask!.IsCompletedSuccessfully);
            Assert.Single(data.Statuses);
            Assert.True(logger.Logged(LogLevel.Warning, "The bot's last status and history before it stopped weren't all written"));
        }

        // The bot and its health check work without it, and the warning never quotes the setting
        [Theory]
        [InlineData(null, "isn't set")]
        [InlineData("", "isn't set")]
        [InlineData("Server=127.0.0.1;Uid=minerbot-bot;Pwd=password", "names no database")]
        // MySql.Data can't read these. For the first, an unquoted semicolon in its password, its message would quote the
        // rest of the password.
        [InlineData("Server=127.0.0.1;Database=bot_db;Uid=minerbot-bot;Pwd=secret;password", "can't be read")]
        [InlineData("Server=127.0.0.1;Database=bot_db;Uid=minerbot-bot;Use Performance Monitor=true", "can't be read")]
        public async Task WithoutBotConnectionNothingIsRecorded(string? connection, string problem)
        {
            var unset = Writer(connection);

            await unset.StartAsync(TestContext.Current.CancellationToken);
            await unset.ExecuteTask!.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            await unset.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.Equal(0, data.Calls);
            Assert.Contains($"ConnectionStrings:BotConnection {problem}, so the bot's status and history aren't recorded", Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning).Message);
            Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("password") || entry.Message.Contains("minerbot-bot"));
        }

        // Registered as Program.cs registers it: a BotConnection that can't be read costs the recording, never the bot's
        // start, which would otherwise fail after the bot logged in to Discord and Steam, and again at every restart
        [Theory]
        [InlineData("Server=127.0.0.1;Database=bot_db;Uid=minerbot-bot;Pwd=secret;password")]
        [InlineData("Server=127.0.0.1;Database=bot_db;Uid=minerbot-bot;Use Performance Monitor=true")]
        public async Task BotStartsWithABotConnectionThatCantBeRead(string connection)
        {
            var builder = Host.CreateApplicationBuilder();

            builder.Configuration["ConnectionStrings:BotConnection"] = connection;
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<ILogger<StatusWriter>>(logger);
            builder.Services.AddSingleton<BotRecorder>();
            builder.Services.AddHostedService<StatusWriter>();
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddDataAccessServices(builder.Configuration).AddBotStatusAccess();

            using var host = builder.Build();

            await host.StartAsync(TestContext.Current.CancellationToken).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            await host.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.True(logger.Logged(LogLevel.Warning, "ConnectionStrings:BotConnection can't be read, so the bot's status and history aren't recorded"));
            Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("password") || entry.Message.Contains("minerbot-bot"));
        }

        [Fact]
        public async Task VersionIsLoggedAtTheStart()
        {
            var stamped = new StatusWriter(new BotRecorder(clock, BotRecorderTests.Settings("c28e305")), data, BotRecorderTests.Settings(), clock, logger);

            await stamped.StartAsync(TestContext.Current.CancellationToken);
            await stamped.ExecuteTask!.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.True(logger.Logged(LogLevel.Information, "MinerBot's version is c28e305"));
        }

        // An exception that left the loop would stop the whole bot. A clock that fails stands in for anything that could.
        [Fact]
        public async Task FailureInTheLoopNeverEndsIt()
        {
            var flaky = new FlakyClock { Failures = 3 };
            var looping = Writer(Connection, flaky);

            await looping.StartAsync(TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => data.Events.Count > 0, "The writer never wrote after the failures");

            Assert.False(looping.ExecuteTask!.IsCompleted);
            await looping.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            // Once, however often it repeats within a minute
            Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("The status writer failed (InvalidOperationException)"));
        }

        sealed class FlakyClock : TimeProvider
        {
            int failures;

            public int Failures { set => failures = value; }

            public override DateTimeOffset GetUtcNow() =>
                Interlocked.Decrement(ref failures) >= 0 ? throw new InvalidOperationException("The clock failed") : Noon;
        }
    }
}
