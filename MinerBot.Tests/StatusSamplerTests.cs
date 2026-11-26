using System.Diagnostics;
using DataAccessLibrary.Status;
using Microsoft.Extensions.Logging;
using MinerBot_2._0.Status;
using SteamKit2;
using SteamWebAPIAccess;

namespace MinerBot.Tests
{
    // Discord as the status sampler reads it, set by the test
    internal sealed class FakeDiscordStatus : IDiscordStatus
    {
        public static readonly DiscordReading Up = new(DiscordState.Connected, true, 40, 3);
        public static readonly DiscordReading Down = new(DiscordState.Disconnected, false, null, null);

        readonly object gate = new();
        DiscordReading reading = Down;
        int failures;

        public DiscordReading Reading
        {
            set
            {
                lock (gate)
                    reading = value;
            }
        }

        // How many reads fail before they work again
        public int Failures
        {
            set
            {
                lock (gate)
                    failures = value;
            }
        }

        public DiscordReading Read()
        {
            lock (gate)
                return failures-- > 0 ? throw new InvalidOperationException("Discord can't be read") : reading;
        }
    }

    // The heartbeat, the samples and the state changes, which never wait on the database. The ticks are called one by one
    // here, on a clock the test moves, except where the loop itself is what's tested.
    public class StatusSamplerTests : IDisposable
    {
        static readonly DateTimeOffset Noon = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

        // A folder of the test's own, because every test process shares the temporary folder
        readonly string folder = Directory.CreateTempSubdirectory("MinerBotSamplerTests").FullName;
        readonly FakeClock clock = new() { UtcNow = Noon.AddSeconds(1) };
        readonly FakeSteam steam = new();
        readonly FakeDiscordStatus discord = new();
        readonly RecordingLogger<StatusSampler> logger = new();
        readonly BotRecorder recorder;
        readonly StatusSampler sampler;

        string HeartbeatPath => Path.Combine(folder, "minerbot-heartbeat");

        public StatusSamplerTests()
        {
            recorder = new BotRecorder(clock, BotRecorderTests.Settings());
            sampler = new StatusSampler(recorder, discord, steam, clock, logger) { Interval = TimeSpan.FromMilliseconds(20), HeartbeatPath = HeartbeatPath };
        }

        public void Dispose() => Directory.Delete(folder, recursive: true);

        // Null while the sampler is replacing it, which Windows doesn't let a reader see through
        string? Heartbeat()
        {
            try
            {
                return File.ReadAllText(HeartbeatPath);
            }
            catch (IOException)
            {
                return null;
            }
        }

        static async Task WaitUntilAsync(Func<bool> condition, string failure)
        {
            var clock = Stopwatch.StartNew();

            while (!condition())
            {
                Assert.True(clock.Elapsed < WaitLimit, failure);
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        // A minute without a sample is a minute the bot didn't run, and one sampled while the gateway is connecting is down
        // on Discord
        [Fact]
        public void OneSampleForEachMinuteTheBotRan()
        {
            discord.Reading = FakeDiscordStatus.Up;
            steam.Status = new SteamStatus(SteamConnection.LoggedOn, Noon);

            sampler.Tick();

            clock.UtcNow = Noon.AddSeconds(6);
            sampler.Tick();

            clock.UtcNow = Noon.AddSeconds(59);
            discord.Reading = FakeDiscordStatus.Down;
            sampler.Tick();

            clock.UtcNow = Noon.AddMinutes(1).AddSeconds(4);
            sampler.Tick();

            clock.UtcNow = Noon.AddMinutes(3);
            discord.Reading = new DiscordReading(DiscordState.Connecting, false, null, null);
            steam.Status = new SteamStatus(SteamConnection.NeedsSignIn, Noon.AddMinutes(2));
            sampler.Tick();

            var samples = recorder.PeekSamples(10);

            Assert.Equal([(Noon.UtcDateTime, true, true), (Noon.UtcDateTime.AddMinutes(1), false, true), (Noon.UtcDateTime.AddMinutes(3), false, false)],
                samples.Select(sample => (sample.minute, sample.discord_up, sample.steam_up)));
            Assert.All(samples, sample => Assert.Equal(DateTimeKind.Utc, sample.minute.Kind));
        }

        // A run's first tick comes before the gateway connects, so the minute it starts in is sampled at its first tick that
        // reads Discord connected, with Steam as read then, never down from before the connection: a restart inside a minute
        // the run before sampled up keeps that minute up, and one across a minute's start records it up once the new run
        // connects in it. Every later minute is sampled at its first tick, as before.
        [Fact]
        public void StartMinuteIsSampledAtTheFirstConnectedTick()
        {
            steam.Status = new SteamStatus(SteamConnection.Offline, Noon);

            sampler.Tick();

            clock.UtcNow = Noon.AddSeconds(6);
            discord.Reading = new DiscordReading(DiscordState.Connecting, false, null, null);
            sampler.Tick();

            Assert.Empty(recorder.PeekSamples(10));

            clock.UtcNow = Noon.AddSeconds(11);
            discord.Reading = FakeDiscordStatus.Up;
            steam.Status = new SteamStatus(SteamConnection.LoggedOn, Noon.AddSeconds(10));
            sampler.Tick();

            clock.UtcNow = Noon.AddSeconds(16);
            discord.Reading = FakeDiscordStatus.Down;
            sampler.Tick();

            clock.UtcNow = Noon.AddMinutes(1).AddSeconds(1);
            sampler.Tick();

            Assert.Equal([(Noon.UtcDateTime, true, true), (Noon.UtcDateTime.AddMinutes(1), false, true)],
                recorder.PeekSamples(10).Select(sample => (sample.minute, sample.discord_up, sample.steam_up)));
        }

        // A start minute in which the gateway never connected is down, as its first tick read it, sampled at the next minute's
        // first tick, before that minute's own sample
        [Fact]
        public void StartMinuteWithoutAConnectionIsSampledDown()
        {
            steam.Status = new SteamStatus(SteamConnection.Offline, Noon);

            sampler.Tick();

            clock.UtcNow = Noon.AddSeconds(31);
            steam.Status = new SteamStatus(SteamConnection.LoggedOn, Noon.AddSeconds(30));
            sampler.Tick();

            Assert.Empty(recorder.PeekSamples(10));

            clock.UtcNow = Noon.AddMinutes(1).AddSeconds(1);
            discord.Reading = FakeDiscordStatus.Up;
            sampler.Tick();

            clock.UtcNow = Noon.AddMinutes(1).AddSeconds(6);
            sampler.Tick();

            Assert.Equal([(Noon.UtcDateTime, false, false), (Noon.UtcDateTime.AddMinutes(1), true, true)],
                recorder.PeekSamples(10).Select(sample => (sample.minute, sample.discord_up, sample.steam_up)));
        }

        // Docker's health check reads it: only while the bot is up on Discord, so a bot that lost Discord turns unhealthy
        [Fact]
        public void HeartbeatOnlyWhileDiscordIsUp()
        {
            discord.Reading = new DiscordReading(DiscordState.Connecting, false, null, null);
            sampler.Tick();

            Assert.False(File.Exists(HeartbeatPath));

            discord.Reading = FakeDiscordStatus.Up;
            sampler.Tick();

            Assert.Equal($"{Noon.AddSeconds(1):O} {recorder.RunId}\n", Heartbeat());
            Assert.Equal(0, MinerBot_2._0.Status.Heartbeat.Check(HeartbeatPath, clock.UtcNow).ExitCode);

            clock.UtcNow = Noon.AddSeconds(30);
            discord.Reading = FakeDiscordStatus.Down;
            sampler.Tick();

            Assert.Equal($"{Noon.AddSeconds(1):O} {recorder.RunId}\n", Heartbeat());
            Assert.Equal(1, MinerBot_2._0.Status.Heartbeat.Check(HeartbeatPath, Noon.AddSeconds(62)).ExitCode);
            Assert.False(File.Exists(HeartbeatPath + ".tmp"));
        }

        // Connected without a Ready since the start, as after a first session that dropped before its Ready and was resumed:
        // the minute counts as up on Discord, where the bot answers, but the heartbeat waits for a Ready
        [Fact]
        public void ConnectedBeforeAnyReadyIsSampledAsUpWithoutAHeartbeat()
        {
            discord.Reading = new DiscordReading(DiscordState.Connected, false, 40, 3);
            sampler.Tick();

            Assert.True(Assert.Single(recorder.PeekSamples(10)).discord_up);
            Assert.Equal(DiscordState.Connected, recorder.StatusToWrite().Row.discord_state);
            Assert.False(File.Exists(HeartbeatPath));
        }

        [Fact]
        public void StatusFollowsDiscordAndSteam()
        {
            discord.Reading = FakeDiscordStatus.Up;
            steam.Status = new SteamStatus(SteamConnection.NeedsSignIn, Noon.AddMinutes(-5), EResult.Revoked);

            sampler.Tick();

            var (row, _) = recorder.StatusToWrite();

            Assert.Equal((DiscordState.Connected, (int?)40, (int?)3, SteamState.NeedsSignIn, (string?)"Revoked", (DateTime?)Noon.AddMinutes(-5).UtcDateTime),
                (row.discord_state, row.discord_latency_ms, row.discord_guilds, row.steam_state, row.steam_last_result, row.steam_since));
        }

        // The writer is stuck on a database that never answers, while the sampler keeps the heartbeat fresh
        [Fact]
        public async Task HungDatabaseNeverHoldsUpTheHeartbeat()
        {
            var data = new FakeStatusData { Hold = new TaskCompletionSource().Task };
            var writer = new StatusWriter(recorder, data, BotRecorderTests.Settings(botConnection: "Server=127.0.0.1;Database=bot_db"), clock,
                new RecordingLogger<StatusWriter>()) { Interval = TimeSpan.FromMilliseconds(20), StopLimit = TimeSpan.FromMilliseconds(100) };
            discord.Reading = FakeDiscordStatus.Up;

            await writer.StartAsync(TestContext.Current.CancellationToken);
            await sampler.StartAsync(TestContext.Current.CancellationToken);

            try
            {
                await WaitUntilAsync(() => data.Calls > 0, "The writer never called the database");

                for (var minute = 1; minute <= 3; minute++)
                {
                    clock.UtcNow = Noon.AddMinutes(minute);
                    await WaitUntilAsync(() => Heartbeat()?.StartsWith($"{clock.UtcNow:O} ") == true, "The heartbeat stopped while the database hung");

                    // The tick queues the minute's sample just after it writes the heartbeat
                    await WaitUntilAsync(() => recorder.PeekSamples(10).Any(sample => sample.minute == clock.UtcNow.UtcDateTime), "The samples stopped while the database hung");
                }

                Assert.Equal(1, data.Calls);
                Assert.Equal(3, recorder.PeekSamples(10).Count(sample => sample.minute > Noon.UtcDateTime));
            }
            finally
            {
                await sampler.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
                await writer.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            }
        }

        // An exception that left the loop would stop the whole bot
        [Fact]
        public async Task FailureInTheLoopNeverEndsIt()
        {
            discord.Failures = 3;
            discord.Reading = FakeDiscordStatus.Up;

            await sampler.StartAsync(TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => Heartbeat() != null, "The heartbeat never came after the failures");

            Assert.False(sampler.ExecuteTask!.IsCompleted);
            await sampler.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            // Once, however often it repeats within a minute
            Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("The status sampler failed (InvalidOperationException)"));
        }
    }
}
