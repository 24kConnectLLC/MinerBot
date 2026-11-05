using System.Diagnostics;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // The built program, run as the container runs it, with only the settings each test gives it. The inherited
    // dashboard, Discord, connection string and ASP.NET Core settings are removed, so nothing of the test's machine counts.
    public class CommandModeProcessTests
    {
        static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

        static readonly string[] Inherited = ["Dashboard__", "Discord__", "ConnectionStrings__", "ASPNETCORE_"];

        internal sealed record Run(int ExitCode, string Output, string Errors);

        internal static async Task<Run> RunAsync(IReadOnlyDictionary<string, string> environment, params string[] args)
        {
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory
            };

            // The test's output has the dashboard's own runtime and dependency files beside it, as WebApplicationFactory needs
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "MinerBot.Dashboard.dll"));

            foreach (var arg in args)
                start.ArgumentList.Add(arg);

            foreach (var name in start.Environment.Keys.Where(name => Inherited.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToList())
                start.Environment.Remove(name);

            foreach (var (name, value) in environment)
                start.Environment[name] = value;

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken);
            }
            finally
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }

            return new Run(process.ExitCode, await output.WaitAsync(Limit), await errors.WaitAsync(Limit));
        }

        // A web host would have stopped on its settings with its plain message instead, so this shows the command ran
        // without one
        [Fact]
        public async Task HealthCheckRunsWithoutTheWebHost()
        {
            var run = await RunAsync(new Dictionary<string, string> { ["Dashboard__Port"] = DeadDatabase.ClosedPort().ToString() }, "healthcheck");

            Assert.Equal(1, run.ExitCode);
            Assert.StartsWith("Unhealthy", run.Output);
            Assert.DoesNotContain("didn't start", run.Errors);
        }

        // The settings stop the dashboard before anything listens, with one plain line for each problem and no stack trace.
        // A port that isn't a number is one of them, rather than an exception as the settings are read. A null value leaves
        // the setting out.
        [Theory]
        [InlineData("Dashboard__ProxyKey", null, "- Dashboard:ProxyKey isn't set.")]
        [InlineData("Dashboard__Port", "5080x", "- Dashboard:Port must be a whole number from 1 to 65535.")]
        public async Task WrongSettingStopsTheDashboardWithAPlainMessage(string name, string? value, string line)
        {
            var keys = DashboardFactory.NewKeysFolder();

            try
            {
                var environment = new Dictionary<string, string>
                {
                    ["Dashboard__PublicOrigin"] = DashboardFactory.PublicOrigin,
                    ["Dashboard__ProxyKey"] = "made-up-proxy-key-for-the-process-test-0123456789abcdef",
                    ["Dashboard__Owners"] = TestUsers.Owner.IdText,
                    ["Dashboard__KeysFolder"] = keys,
                    ["Dashboard__Port"] = DeadDatabase.ClosedPort().ToString(),
                    ["Discord__ClientId"] = DashboardFactory.ClientId,
                    ["Discord__ClientSecret"] = "made-up-secret-for-the-process-test",
                    ["ConnectionStrings__DefaultConnection"] = new DeadDatabase().SiteConnection,
                    ["ConnectionStrings__BotConnection"] = new DeadDatabase().BotConnection
                };

                if (value == null)
                    environment.Remove(name);
                else
                    environment[name] = value;

                var run = await RunAsync(environment);

                Assert.Equal(1, run.ExitCode);
                Assert.StartsWith("The dashboard didn't start, because of its settings:", run.Errors);
                Assert.Contains(line, run.Errors);
                Assert.Single(run.Errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), errorLine => errorLine.StartsWith("- ", StringComparison.Ordinal));
                Assert.DoesNotContain("   at ", run.Errors);
                Assert.DoesNotContain("made-up-secret-for-the-process-test", run.Errors + run.Output);
                Assert.DoesNotContain("made-up-proxy-key-for-the-process-test", run.Errors + run.Output);
            }
            finally
            {
                DashboardFactory.DeleteFolder(keys);
            }
        }
    }

    [Collection("Database")]
    public class MigrateProcessTests(MariaDbFixture fixture)
    {
        [Fact]
        public async Task MigrateRunsWithOnlyTheBotConnection()
        {
            var db = fixture.Require();
            var (_, connection) = await db.CreateDatabaseAsync("dashboard_migrate_process");

            var run = await CommandModeProcessTests.RunAsync(new Dictionary<string, string> { ["ConnectionStrings__BotConnection"] = connection }, "migrate");

            Assert.True(run.ExitCode == 0, $"migrate exited {run.ExitCode}: {run.Output} {run.Errors}");
            Assert.Contains("is at version", run.Output);
        }
    }
}
