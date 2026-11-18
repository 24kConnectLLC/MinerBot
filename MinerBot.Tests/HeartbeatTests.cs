using System.Text.RegularExpressions;
using MinerBot_2._0.Status;

namespace MinerBot.Tests
{
    // The heartbeat file, what Docker's health check answers from it, and how the bot's start and image use it
    public class HeartbeatTests : IDisposable
    {
        static readonly DateTimeOffset Noon = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

        // A folder of the test's own, because every test process shares the temporary folder
        readonly string folder = Directory.CreateTempSubdirectory("MinerBotHeartbeatTests").FullName;

        string FilePath => Path.Combine(folder, "minerbot-heartbeat");

        public void Dispose() => Directory.Delete(folder, recursive: true);

        static string BotSource(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "bot-source", name)).Replace("\r\n", "\n");

        [Fact]
        public void NoFileIsUnhealthy()
        {
            Assert.Equal((1, "Unhealthy: there's no heartbeat file, so the bot hasn't been up on Discord since it started"), Heartbeat.Check(FilePath, Noon));
            Assert.Equal(1, Heartbeat.Check(Path.Combine(folder, "missing", "minerbot-heartbeat"), Noon).ExitCode);
        }

        // At most a minute old, or a minute ahead
        [Theory]
        [InlineData(0, "Healthy: the heartbeat is 0 s old")]
        [InlineData(5, "Healthy: the heartbeat is 5 s old")]
        [InlineData(60, "Healthy: the heartbeat is 60 s old")]
        [InlineData(-60, "Healthy: the heartbeat is 0 s old")]
        public void FreshHeartbeatIsHealthy(int age, string line)
        {
            Heartbeat.Write(FilePath, Noon, 42);

            Assert.Equal((0, line), Heartbeat.Check(FilePath, Noon.AddSeconds(age)));
        }

        [Theory]
        [InlineData(61, "Unhealthy: the heartbeat is 61 s old, so the bot isn't up on Discord")]
        [InlineData(3600, "Unhealthy: the heartbeat is 3600 s old, so the bot isn't up on Discord")]
        [InlineData(-61, "Unhealthy: the heartbeat is 61 s ahead of the clock")]
        public void OldOrFutureHeartbeatIsUnhealthy(int age, string line)
        {
            Heartbeat.Write(FilePath, Noon, 42);

            Assert.Equal((1, line), Heartbeat.Check(FilePath, Noon.AddSeconds(age)));
        }

        [Theory]
        [InlineData("")]
        [InlineData("\n")]
        [InlineData("not a time")]
        [InlineData("2026-11-14 12:00:00 42")]
        [InlineData("12:00:00")]
        public void UnreadableHeartbeatIsUnhealthy(string text)
        {
            File.WriteAllText(FilePath, text);

            Assert.Equal((1, "Unhealthy: the heartbeat file holds no time it can read"), Heartbeat.Check(FilePath, Noon));
        }

        [Fact]
        public void HeartbeatThatCantBeOpenedIsUnhealthy()
        {
            Directory.CreateDirectory(FilePath);

            var (exitCode, line) = Heartbeat.Check(FilePath, Noon);

            Assert.Equal(1, exitCode);
            Assert.Matches(@"^Unhealthy: the heartbeat file can't be read \(\w+Exception\)$", line);
        }

        // The time it was written and the run, nothing else, and no temporary file left behind
        [Fact]
        public void HeartbeatHoldsItsTimeAndRun()
        {
            Heartbeat.Write(FilePath, Noon.ToOffset(TimeSpan.FromHours(-5)), 18446744073709551615);
            Heartbeat.Write(FilePath, Noon.AddSeconds(5), 18446744073709551615);

            Assert.Equal("2026-11-14T12:00:05.0000000+00:00 18446744073709551615\n", File.ReadAllText(FilePath));
            Assert.False(File.Exists(FilePath + ".tmp"));
        }

        // A temporary file and then a move, so the health check never reads half a heartbeat: a write that fails leaves the
        // one before it whole, and fails with what the status sampler catches
        [Fact]
        public void FailedWriteLeavesTheHeartbeatBeforeIt()
        {
            Heartbeat.Write(FilePath, Noon, 42);
            Directory.CreateDirectory(FilePath + ".tmp");

            var failure = Record.Exception(() => Heartbeat.Write(FilePath, Noon.AddSeconds(5), 42));

            Assert.True(failure is IOException or UnauthorizedAccessException, $"The write failed with {failure?.GetType().Name ?? "nothing"}");
            Assert.Equal($"{Noon:O} 42\n", File.ReadAllText(FilePath));
        }

        // A container that Docker restarts keeps its /tmp, so the start deletes the run before's heartbeat, and nothing
        // about that can stop a start
        [Fact]
        public void StartDeletesTheHeartbeatOfTheRunBefore()
        {
            Heartbeat.Write(FilePath, Noon, 1);
            File.WriteAllText(FilePath + ".tmp", "half written");

            Heartbeat.DeleteOld(FilePath);

            Assert.False(File.Exists(FilePath));
            Assert.False(File.Exists(FilePath + ".tmp"));

            Heartbeat.DeleteOld(FilePath);
            Heartbeat.DeleteOld(Path.Combine(folder, "missing", "minerbot-heartbeat"));
        }

        [Fact]
        public void HeartbeatIsInTheTemporaryFolder()
        {
            Assert.Equal(Path.Combine(Path.GetTempPath(), "minerbot-heartbeat"), Heartbeat.DefaultPath);
        }

        // The health check comes before anything else in Program.cs, reads no settings and ends the program. The start
        // deletes the heartbeat before it builds the host. Program.cs can't run in a test, because it would reach Discord.
        [Fact]
        public void ProgramChecksHealthFirstAndClearsTheHeartbeatBeforeTheHost()
        {
            var source = BotSource("Program.cs");
            var check = source.IndexOf("if (args is [\"healthcheck\"])", StringComparison.Ordinal);
            var delete = source.IndexOf("Heartbeat.DeleteOld(Heartbeat.DefaultPath);", StringComparison.Ordinal);
            var builder = source.IndexOf("Host.CreateApplicationBuilder(args)", StringComparison.Ordinal);

            Assert.True(check > 0, "Program.cs has no health check");
            Assert.True(delete > check, "Program.cs doesn't delete the heartbeat after the health check");
            Assert.True(builder > delete, "Program.cs builds the host before it deletes the heartbeat");

            // Only usings and comments before the check
            Assert.All(source[..check].Split('\n'), line => Assert.Matches(@"^(using [\w.]+;|//.*|\s*)$", line));

            var branch = source[check..delete];
            Assert.Contains("Heartbeat.Check(Heartbeat.DefaultPath, DateTimeOffset.UtcNow)", branch);
            Assert.Matches(@"Environment\.ExitCode = exitCode;\s*return;\s*\}", branch);
            Assert.DoesNotContain("builder", branch);
            Assert.DoesNotContain("GetEnvironmentVariable", branch);

            // A Main that returns a value would ignore Environment.ExitCode, which the worker sets when it fails. The
            // compiled entry point says so whatever else the file holds, such as a lambda that returns a value.
            Assert.Equal(typeof(void), typeof(MinerBot_2._0.Worker).Assembly.EntryPoint!.ReturnType);
        }

        // The status and history's wiring, which no other test sees, because FakeDiscord wires its own: the status services
        // before the worker, so they stop after it; the stopping event; the status handler before the other handlers and the
        // login, because Discord.Net skips the Ready handlers after one that throws; and Steam's events before its sign-in
        [Fact]
        public void ProgramWiresTheStatusBeforeTheBotStarts()
        {
            var source = BotSource("Program.cs");

            int At(string line)
            {
                var at = source.IndexOf(line, StringComparison.Ordinal);
                Assert.True(at > 0, $"Program.cs has no {line}");
                return at;
            }

            var worker = At("builder.Services.AddHostedService<Worker>();");
            Assert.True(At("builder.Services.AddHostedService<StatusWriter>();") < worker, "The status writer is registered after the worker, so it stops first");
            Assert.True(At("builder.Services.AddHostedService<StatusSampler>();") < worker, "The status sampler is registered after the worker, so it stops first");
            At(".ApplicationStopping.Register(() => recorder.Record(BotEvents.BotStopping));");

            var status = At("host.Services.GetRequiredService<DiscordStatusHandler>().Initialize();");
            Assert.True(status < At("host.Services.GetRequiredService<InteractionHandler>().InitializeAsync();"), "The status handler comes after the interaction handler");
            Assert.True(status < At("host.Services.GetRequiredService<GuildEventHandler>().Initialize();"), "The status handler comes after the server handler");
            Assert.True(status < At("await client.LoginAsync("), "The status handler is attached after the login");

            // The servers' copy: its writer, and its watcher before the handlers that can throw, such as the server handler's
            // LeftGuild, which awaits the website's database
            At("builder.Services.AddSingleton<GuildWatcher>();");
            At(".AddBotGuildAccess()");
            Assert.True(At("builder.Services.AddHostedService<GuildWriter>();") < worker, "The server writer is registered after the worker, so it stops first");

            var watcher = At("host.Services.GetRequiredService<GuildWatcher>().Initialize();");
            Assert.True(watcher < At("host.Services.GetRequiredService<InteractionHandler>().InitializeAsync();"), "The server watcher comes after the interaction handler");
            Assert.True(watcher < At("host.Services.GetRequiredService<GuildEventHandler>().Initialize();"), "The server watcher comes after the server handler");
            Assert.True(watcher < At("await client.LoginAsync("), "The server watcher is attached after the login");

            Assert.True(At("steamApi.RecordEvent = recorder.Record;") < At("steamApi.AuthenticateClient();"), "Steam signs in before its events are recorded");
        }

        // The image checks itself with the heartbeat, and carries the commit it was built from
        [Fact]
        public void ImageChecksItsHealthAndKnowsItsCommit()
        {
            var dockerfile = BotSource("Dockerfile");
            var final = dockerfile[dockerfile.IndexOf("FROM base AS final", StringComparison.Ordinal)..];
            var copy = final.IndexOf("COPY --from=publish /app/publish .\n", StringComparison.Ordinal);
            var argument = final.IndexOf("ARG SOURCE_REVISION=\n", StringComparison.Ordinal);
            var setting = final.IndexOf($"ENV {BotVersion.Setting}=$SOURCE_REVISION\n", StringComparison.Ordinal);

            Assert.True(copy > 0 && argument > copy && setting > argument, "The commit isn't the last step of the image, after its files");
            Assert.Contains("HEALTHCHECK --interval=30s --timeout=10s --start-period=160s --start-interval=5s --retries=3 CMD [\"dotnet\", \"MinerBot 2.0.dll\", \"healthcheck\"]\n", final);
            Assert.Single(Regex.Matches(dockerfile, "^HEALTHCHECK ", RegexOptions.Multiline));
        }

        // Docker checks every 5 seconds only during the start period, and every 30 after it, so a bot first up on Discord
        // after the start period turns healthy up to 30 seconds later. The start period outlasts the deploy's wait for
        // healthy (ready_bot's limit in deploy/common.sh) by 10 seconds: the container starts before that wait does, and
        // the wait's last look can come a poll after its limit.
        [Fact]
        public void HealthIsCheckedEvery5SecondsForAsLongAsADeployWaits()
        {
            var startPeriod = Regex.Match(BotSource("Dockerfile"), @"^HEALTHCHECK .*--start-period=(\d+)s ", RegexOptions.Multiline);
            var limit = Regex.Match(BotSource("common.sh"), @"^ready_bot\(\) \{[^\n]*\n\s+local [^\n]*\blimit=(\d+) ", RegexOptions.Multiline);

            Assert.True(startPeriod.Success && limit.Success, "The health check's start period or ready_bot's limit wasn't found");
            Assert.True(int.Parse(startPeriod.Groups[1].Value) >= int.Parse(limit.Groups[1].Value) + 10,
                $"The health check's start period, {startPeriod.Groups[1].Value} s, ends before a deploy stops waiting for healthy, {limit.Groups[1].Value} s");
        }
    }
}
