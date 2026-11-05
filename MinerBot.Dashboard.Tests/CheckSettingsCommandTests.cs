using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // "check-settings", which deploy.sh and setup-dashboard.sh run before they change anything: the same check a start
    // makes, every section's included, and then an exit, without listening, connecting or making a sign-in key. The built
    // program, run as the container runs it.
    public class CheckSettingsCommandTests
    {
        const string Command = "check-settings";

        // The good settings with a test's changes, in a keys folder of its own
        static async Task<CommandModeProcessTests.Run> CheckAsync(Action<Dictionary<string, string>> change)
        {
            var keys = DashboardFactory.NewKeysFolder();

            try
            {
                var settings = CommandModeProcessTests.GoodSettings(keys);
                change(settings);
                return await CommandModeProcessTests.RunAsync(settings, Command);
            }
            finally
            {
                DashboardFactory.DeleteFolder(keys);
            }
        }

        [Fact]
        public async Task ValidSettingsExitZero()
        {
            var run = await CheckAsync(_ => { });

            Assert.True(run.ExitCode == 0, $"check-settings exited {run.ExitCode}: {run.Output} {run.Errors}");
            Assert.Equal("The dashboard's settings are fine.", run.Output.Trim());
            Assert.Equal("", run.Errors.Trim());
        }

        // The Access check's settings are part of it
        [Fact]
        public async Task ValidSettingsWithAccessOnExitZero()
        {
            var run = await CheckAsync(settings =>
            {
                settings["CloudflareAccess__TeamDomain"] = "made-up-team.cloudflareaccess.com";
                settings["CloudflareAccess__Audience"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            });

            Assert.True(run.ExitCode == 0, $"check-settings exited {run.ExitCode}: {run.Output} {run.Errors}");
            Assert.Equal("The dashboard's settings are fine.", run.Output.Trim());
        }

        // Every section's problems, each on a line that names its key and never its value, and exit code 1
        [Fact]
        public async Task InvalidSettingsExitOneNamingTheKeysNotTheValues()
        {
            const string proxyKey = "made-up-proxy-key-$host-0123456789abcdef0123456789";
            const string clientSecret = "made-up secret-for-the-check";
            const string team = "https://Made-Up-Team.cloudflareaccess.com";

            var run = await CheckAsync(settings =>
            {
                settings["Dashboard__ProxyKey"] = proxyKey;
                settings["Discord__ClientSecret"] = clientSecret;
                settings["CloudflareAccess__TeamDomain"] = team;
            });

            Assert.Equal(1, run.ExitCode);
            Assert.StartsWith("The dashboard didn't start, because of its settings:", run.Errors);

            var lines = run.Errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(line => line.StartsWith("- ", StringComparison.Ordinal)).ToList();

            Assert.Equal(4, lines.Count);
            Assert.Contains(lines, line => line.StartsWith("- Dashboard:ProxyKey may only hold", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.StartsWith("- Discord:ClientSecret has white space", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.StartsWith("- CloudflareAccess:TeamDomain and CloudflareAccess:Audience go together", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.StartsWith("- CloudflareAccess:TeamDomain must be just the team's host name", StringComparison.Ordinal));

            foreach (var value in new[] { proxyKey, clientSecret, team, "Made-Up-Team" })
                Assert.DoesNotContain(value, run.Errors + run.Output);

            Assert.DoesNotContain("fine", run.Output);
            Assert.DoesNotContain("   at ", run.Errors);
        }

        // A start would fail on a port that's taken. The check never binds it, never connects to the databases, and makes
        // no sign-in key: the keys folder is left empty after the check's own write test.
        [Fact]
        public async Task NeverBindsThePortConnectsOrMakesAKey()
        {
            using var taken = new TcpListener(IPAddress.Loopback, 0);
            taken.Start();
            var port = ((IPEndPoint)taken.LocalEndpoint).Port;

            using var database = new TcpListener(IPAddress.Loopback, 0);
            database.Start();
            var databasePort = ((IPEndPoint)database.LocalEndpoint).Port;

            var keys = DashboardFactory.NewKeysFolder();

            try
            {
                var settings = CommandModeProcessTests.GoodSettings(keys);
                settings["Dashboard__Port"] = port.ToString(CultureInfo.InvariantCulture);
                settings["ConnectionStrings__DefaultConnection"] = $"Server=127.0.0.1;Port={databasePort};Database=check_site;Uid=check_login;Pwd=made-up;SslMode=Disabled";
                settings["ConnectionStrings__BotConnection"] = $"Server=127.0.0.1;Port={databasePort};Database=check_bot;Uid=check_login;Pwd=made-up;SslMode=Disabled";

                var run = await CommandModeProcessTests.RunAsync(settings, Command);

                Assert.True(run.ExitCode == 0, $"check-settings exited {run.ExitCode}: {run.Output} {run.Errors}");
                Assert.Equal("The dashboard's settings are fine.", run.Output.Trim());
                Assert.False(database.Pending(), "check-settings connected to the database");
                Assert.True(Directory.Exists(keys));
                Assert.Empty(Directory.EnumerateFileSystemEntries(keys));
            }
            finally
            {
                DashboardFactory.DeleteFolder(keys);
            }
        }
    }
}
