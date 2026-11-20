using System.Diagnostics;
using System.Security.Cryptography;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MinerBot.Dashboard.Hosting;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Requests;
using MySql.Data.MySqlClient;

namespace MinerBot.Dashboard.Tests
{
    // The commands the deploy, the container and request.sh run with the same program, without the web host
    public class CommandModeTests
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static IConfiguration Settings(params (string Key, string? Value)[] settings) =>
            new ConfigurationBuilder().AddInMemoryCollection(settings.Select(setting => KeyValuePair.Create(setting.Key, setting.Value))).Build();

        // Fails if WebApplicationFactory's own arguments, or the host's, could start one
        [Theory]
        [InlineData(new[] { "migrate" }, "migrate")]
        [InlineData(new[] { "healthcheck" }, "healthcheck")]
        [InlineData(new[] { "migrate", "--ConnectionStrings:BotConnection=x" }, "migrate")]
        [InlineData(new[] { "--environment=Production" }, null)]
        [InlineData(new[] { "Migrate" }, null)]
        [InlineData(new[] { "HEALTHCHECK" }, null)]
        [InlineData(new[] { "--contentRoot=x", "healthcheck" }, null)]
        [InlineData(new[] { "migrate-now" }, null)]
        [InlineData(new string[0], null)]
        [InlineData(new[] { "request", "caches.clear" }, "request")]
        [InlineData(new[] { "Request", "caches.clear" }, null)]
        [InlineData(new[] { "--environment=Production", "request" }, null)]
        // Program.cs runs this one itself, with the web host's registrations
        [InlineData(new[] { "check-settings" }, null)]
        public void FindTakesOnlyTheFirstWord(string[] args, string? command)
        {
            Assert.Equal(command, DashboardCommands.Find(args));
        }

        // Made up, as setup-dashboard.sh makes the key
        const string RequestKey = "5d2c7b0e9f8a1b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4";

        // Every setting the request command reads, made up, with a database nothing listens on: each case below stops before
        // it would connect
        static Dictionary<string, string?> RequestSettings(DeadDatabase dead) => new()
        {
            ["ConnectionStrings:BotConnection"] = dead.BotConnection,
            ["Dashboard:RequestSigningKey"] = RequestKey,
            ["Dashboard:Owners"] = "1000000000000000001,1000000000000000002",
            ["MINERBOT_REQUEST_TEXT"] = "Testing"
        };

        static async Task<(int Exit, string Output)> RequestAsync(Dictionary<string, string?> settings, params string[] args)
        {
            var output = new StringWriter();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var exit = await RequestCommand.RunAsync(configuration, args, output, NullLoggerFactory.Instance, new FakeClock(), new RequestWaits(), Token)
                .WaitAsync(TimeSpan.FromSeconds(30), Token);
            return (exit, output.ToString());
        }

        static void AssertNoSecret(string output, DeadDatabase dead)
        {
            Assert.DoesNotContain(RequestKey[..16], output);
            Assert.DoesNotContain(dead.Password, output);
            Assert.DoesNotContain(dead.Login, output);
        }

        // Only the kind first, then --guild and --channel with a Discord ID each, once: anything else stops it with exit
        // code 2 and the usage, before it reads a setting or connects. Nothing typed is repeated.
        [Theory]
        [InlineData("no kind")]
        [InlineData("an option first")]
        [InlineData("an unknown kind")]
        [InlineData("a kind in capitals")]
        [InlineData("a short --guild")]
        [InlineData("a --guild that isn't a number")]
        [InlineData("a --guild without its ID")]
        [InlineData("--guild twice")]
        [InlineData("--channel twice")]
        [InlineData("an option it doesn't know")]
        [InlineData("a word after the kind")]
        public async Task RequestWithABadArgumentExitsTwo(string which)
        {
            var dead = new DeadDatabase();
            string[] args = which switch
            {
                "no kind" => [],
                "an option first" => ["--guild", "1000000000000000005", "guild.refresh"],
                "an unknown kind" => ["bot.shutdown"],
                "a kind in capitals" => ["CACHES.CLEAR"],
                "a short --guild" => ["guild.refresh", "--guild", "1234"],
                "a --guild that isn't a number" => ["guild.refresh", "--guild", "1000000000000000005x"],
                "a --guild without its ID" => ["guild.refresh", "--guild"],
                "--guild twice" => ["guild.refresh", "--guild", "1000000000000000005", "--guild", "1000000000000000006"],
                "--channel twice" => ["guild.test_message", "--channel", "1000000000000000005", "--channel", "1000000000000000006"],
                "an option it doesn't know" => ["caches.clear", "--text", "Testing"],
                _ => ["caches.clear", "now"]
            };

            var (exit, output) = await RequestAsync(RequestSettings(dead), args);

            Assert.Equal(2, exit);
            Assert.Contains(RequestCommand.Usage, output);
            Assert.DoesNotContain("1000000000000000005x", output);
            Assert.DoesNotContain("bot.shutdown", output);
            AssertNoSecret(output, dead);
        }

        // A setting it needs that's missing or wrong stops it with exit code 2, naming the setting and never its value, before
        // anything is sent. Without a key, requests to the bot are off.
        [Theory]
        [InlineData("Dashboard:RequestSigningKey", null, "Requests to the bot are off: the dashboard has no request-signing key. Run ./setup-dashboard.sh --no-questions.")]
        [InlineData("Dashboard:RequestSigningKey", "", "Requests to the bot are off: the dashboard has no request-signing key. Run ./setup-dashboard.sh --no-questions.")]
        [InlineData("Dashboard:RequestSigningKey", "5D2C7B0E9F8A1B3C4D5E6F708192A3B4C5D6E7F8091A2B3C4D5E6F708192A3B4", "Dashboard:RequestSigningKey must be 64 hexadecimal digits in small letters")]
        [InlineData("Dashboard:RequestSigningKey", "5d2c7b0e9f8a1b3c4d5e6f708192a3b4", "Dashboard:RequestSigningKey must be 64 hexadecimal digits in small letters")]
        [InlineData("ConnectionStrings:BotConnection", null, "ConnectionStrings:BotConnection isn't set")]
        [InlineData("ConnectionStrings:BotConnection", "Server=127.0.0.1;Uid=made_up_login;Pwd=made-up-password-1d4f", "ConnectionStrings:BotConnection names no database")]
        [InlineData("Dashboard:Owners", null, "Dashboard:Owners has no owner's Discord user ID")]
        [InlineData("Dashboard:Owners", "test_owner", "Dashboard:Owners has no owner's Discord user ID")]
        public async Task RequestWithoutAUsableSettingExitsTwo(string key, string? value, string said)
        {
            var dead = new DeadDatabase();
            var settings = RequestSettings(dead);

            if (value == null)
                settings.Remove(key);
            else
                settings[key] = value;

            var (exit, output) = await RequestAsync(settings, "caches.clear");

            Assert.Equal(2, exit);
            Assert.StartsWith(said, output);
            AssertNoSecret(output, dead);
            Assert.DoesNotContain("made-up-password-1d4f", output);
            Assert.DoesNotContain("5D2C7B0E9F8A1B3C", output);
        }

        // Its settings come from the environment alone, never its command line, which the process list shows: the settings
        // given there are refused as options, and none of them is repeated
        [Fact]
        public async Task RequestReadsNoSettingFromItsCommandLine()
        {
            var dead = new DeadDatabase();

            var (exit, output) = await RequestAsync(new(), "caches.clear", "--ConnectionStrings:BotConnection=" + dead.BotConnection,
                "--Dashboard:RequestSigningKey=" + RequestKey, "--Dashboard:Owners=1000000000000000001");

            Assert.Equal(2, exit);
            Assert.Contains("Only --guild <server ID> and --channel <channel ID> go after the kind, each once.", output);
            AssertNoSecret(output, dead);

            (exit, output) = await RequestAsync(new(), "caches.clear");
            Assert.Equal(2, exit);
            Assert.StartsWith("ConnectionStrings:BotConnection isn't set", output);
        }

        // Its own pool of two connections, whatever the connection string asks, so beside the running dashboard's two pools of
        // 6 it stays within the dashboard login's 15 even while MinerBot's database hangs
        [Fact]
        public void RequestKeepsAPoolOfTwo()
        {
            var dead = new DeadDatabase();
            var pooled = RequestCommand.Pooled(dead.BotConnection + ";Maximum Pool Size=50").GetConnectionString("BotConnection");

            Assert.Equal(2u, new MySqlConnectionStringBuilder(pooled).MaximumPoolSize);
            Assert.Equal(new MySqlConnectionStringBuilder(dead.BotConnection).Database, new MySqlConnectionStringBuilder(pooled).Database);
        }

        // A restart writes done, then waits for a notification run the schedule started meanwhile, which the bot's
        // JobHandler cuts off at 270 seconds, and only then stops. So the wait for the bot to be back covers that run, the
        // owner's message of up to 10 seconds before it, and a minute for the restart itself.
        [Fact]
        public void TheWaitAfterARestartOutlastsANotificationRun()
        {
            var limit = new RequestWaits().RestartLimit;
            var stops = TimeSpan.FromSeconds(270 + 10);

            Assert.True(limit >= stops + TimeSpan.FromMinutes(1), $"A restart can stop {stops} after its done, and the command waits {limit} in all");
        }

        // The list needs no setting: every kind, who may ask, its limit and its expiry, from the shared list
        [Fact]
        public async Task RequestListShowsEveryKind()
        {
            var (exit, output) = await RequestAsync(new(), "--list");

            Assert.Equal(0, exit);
            Assert.All(RequestKinds.All, kind => Assert.Contains($"  {kind.Word} ", output));
            Assert.Contains("Pause posts. The Owner. At most 10 times an hour, with posts.resume. Expires after 10 minutes. Needs --text <the reason>.", output);
            Assert.Contains("Send a test message. The Owner, an Admin or the server's own admins. At most 3 times every 10 minutes, for each server. Expires after 2 minutes. Needs --guild <server ID>, --channel <channel ID>.", output);
            Assert.Contains("Restart the bot. The Owner. At most once every 10 minutes. Expires after 2 minutes.", output);
            Assert.Equal(RequestKinds.All.Count + 1, output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        }

        [Fact]
        public async Task HealthyAnswerExitsZero()
        {
            await using var server = LoopbackServer.Answering(200, "OK", "Healthy");
            var output = new StringWriter();

            var exit = await DashboardCommands.HealthCheckAsync(Settings(("Dashboard:Port", server.Port.ToString())), output, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

            Assert.Equal(0, exit);
            Assert.Equal("Healthy", output.ToString().Trim());

            var request = await server.Request.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.StartsWith("GET /health/live HTTP/1.1\r\n", request);
            Assert.Contains("\r\nHost: localhost\r\n", request);
        }

        [Theory]
        [InlineData(503, "Service Unavailable", "Unhealthy")]
        [InlineData(200, "OK", "Unhealthy")]
        [InlineData(404, "Not Found", "")]
        public async Task AnyOtherAnswerExitsOne(int status, string reason, string body)
        {
            await using var server = LoopbackServer.Answering(status, reason, body);
            var output = new StringWriter();

            var exit = await DashboardCommands.HealthCheckAsync(Settings(("Dashboard:Port", server.Port.ToString())), output, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

            Assert.Equal(1, exit);
            Assert.StartsWith("Unhealthy", output.ToString());
        }

        [Fact]
        public async Task NothingListeningExitsOne()
        {
            var output = new StringWriter();

            var exit = await DashboardCommands.HealthCheckAsync(Settings(("Dashboard:Port", DeadDatabase.ClosedPort().ToString())), output, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

            Assert.Equal(1, exit);
            Assert.StartsWith("Unhealthy", output.ToString());
        }

        [Fact]
        public async Task ServerThatNeverAnswersExitsOneWithinItsLimit()
        {
            await using var server = LoopbackServer.Silent();
            var output = new StringWriter();
            var watch = Stopwatch.StartNew();

            var exit = await DashboardCommands.HealthCheckAsync(Settings(("Dashboard:Port", server.Port.ToString())), output, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

            Assert.Equal(1, exit);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The check took {watch.Elapsed}");
        }

        [Theory]
        [InlineData("Dashboard:Port", "port")]
        [InlineData("Dashboard:Port", "70000")]
        [InlineData("Dashboard:ListenAddress", "localhost")]
        public async Task BadAddressExitsOne(string key, string value)
        {
            var output = new StringWriter();

            Assert.Equal(1, await DashboardCommands.HealthCheckAsync(Settings((key, value)), output, Token).WaitAsync(TimeSpan.FromSeconds(30), Token));
            Assert.StartsWith("Unhealthy", output.ToString());
        }

        // With Dashboard:UnixSocket set, as compose.yml sets it on the server, the check asks through the socket, on Host
        // localhost like the address's
        [Fact]
        public async Task HealthyAnswerOnTheSocketExitsZero()
        {
            using var sockets = new TestSockets();
            var path = sockets.PathOf("dashboard.sock");
            await using var server = LoopbackServer.Answering(200, "OK", "Healthy", unixSocket: path);
            var output = new StringWriter();

            var exit = await DashboardCommands.HealthCheckAsync(Settings(("Dashboard:UnixSocket", path)), output, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

            Assert.Equal(0, exit);
            Assert.Equal("Healthy", output.ToString().Trim());

            var request = await server.Request.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.StartsWith("GET /health/live HTTP/1.1\r\n", request);
            Assert.Contains("\r\nHost: localhost\r\n", request);
        }

        // The socket replaces the address, so the port isn't asked, even when something there would say Healthy
        [Fact]
        public async Task SocketIsAskedInsteadOfThePort()
        {
            using var sockets = new TestSockets();
            await using var port = LoopbackServer.Answering(200, "OK", "Healthy");
            await using var socket = LoopbackServer.Answering(503, "Service Unavailable", "Unhealthy", unixSocket: sockets.PathOf("dashboard.sock"));
            var output = new StringWriter();

            var exit = await DashboardCommands.HealthCheckAsync(Settings(("Dashboard:Port", port.Port.ToString()), ("Dashboard:UnixSocket", sockets.PathOf("dashboard.sock"))), output, Token)
                .WaitAsync(TimeSpan.FromSeconds(30), Token);

            Assert.Equal(1, exit);
            Assert.Equal("Unhealthy: 503", output.ToString().Trim());
            Assert.False(port.Request.IsCompleted, "The check asked the port");
        }

        // No socket there, as before the dashboard has started or after it was killed
        [Fact]
        public async Task NothingOnTheSocketExitsOne()
        {
            using var sockets = new TestSockets();
            var output = new StringWriter();

            var exit = await DashboardCommands.HealthCheckAsync(Settings(("Dashboard:UnixSocket", sockets.PathOf("dashboard.sock"))), output, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

            Assert.Equal(1, exit);
            Assert.Equal("Unhealthy: HttpRequestException", output.ToString().Trim());
        }

        // A path the dashboard couldn't listen on is said as such, and nothing is asked
        [Theory]
        [InlineData("dashboard.sock")]
        [InlineData("relative/dashboard.sock")]
        public async Task BadSocketPathExitsOne(string path)
        {
            var output = new StringWriter();

            Assert.Equal(1, await DashboardCommands.HealthCheckAsync(Settings(("Dashboard:UnixSocket", path)), output, Token).WaitAsync(TimeSpan.FromSeconds(30), Token));
            Assert.Equal("Unhealthy: Dashboard:UnixSocket isn't valid", output.ToString().Trim());
        }

        // Empty is the setting turned off, so the port is asked as before
        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        public async Task EmptySocketAsksThePort(string empty)
        {
            await using var server = LoopbackServer.Answering(200, "OK", "Healthy");
            var output = new StringWriter();

            var exit = await DashboardCommands.HealthCheckAsync(Settings(("Dashboard:Port", server.Port.ToString()), ("Dashboard:UnixSocket", empty)), output, Token)
                .WaitAsync(TimeSpan.FromSeconds(30), Token);

            Assert.Equal(0, exit);
            Assert.Equal("Healthy", output.ToString().Trim());
        }

        [Fact]
        public async Task MigrateWithoutBotConnectionExitsOne()
        {
            var logger = new ListLogger();

            Assert.Equal(1, await DashboardCommands.MigrateAsync(Settings(), logger, Token).WaitAsync(TimeSpan.FromSeconds(30), Token));
            Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("ConnectionStrings:BotConnection", StringComparison.Ordinal));
        }

        // The driver's text names the login and the host, so only its type and SQL state are shown
        [Fact]
        public async Task MigrateWithNoDatabaseThereExitsOneWithoutTheLogin()
        {
            var dead = new DeadDatabase();
            var logger = new ListLogger();
            var watch = Stopwatch.StartNew();

            var exit = await DashboardCommands.MigrateAsync(Settings(("ConnectionStrings:BotConnection", dead.BotConnection)), logger, Token).WaitAsync(TimeSpan.FromSeconds(60), Token);

            Assert.Equal(1, exit);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"The run took {watch.Elapsed}");
            Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error);
            Assert.All(logger.Entries, entry =>
            {
                Assert.DoesNotContain(dead.Login, entry.Message);
                Assert.DoesNotContain(dead.Password, entry.Message);
                Assert.DoesNotContain(dead.BotDatabase, entry.Message);
            });
        }
    }

    [Collection("Database")]
    public class MigrateCommandTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        // A new database gets every table, and a second run finds nothing to do
        [Fact]
        public async Task MigrateBringsAnEmptyDatabaseUpToDate()
        {
            var db = fixture.Require();
            var (database, connection) = await db.CreateDatabaseAsync("dashboard_migrate");
            var settings = new ConfigurationBuilder().AddInMemoryCollection([KeyValuePair.Create<string, string?>("ConnectionStrings:BotConnection", connection)]).Build();
            var logger = new ListLogger();

            Assert.Equal(0, await DashboardCommands.MigrateAsync(settings, logger, Token).WaitAsync(TimeSpan.FromMinutes(2), Token));

            var tables = await db.RootQueryAsync<string>("SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = @database ORDER BY TABLE_NAME", new { database });
            Assert.Equal(["armor_reviews", "audit_log", "bot_commands", "bot_events", "bot_settings", "bot_status", "dashboard_sessions", "guilds", "guild_channels",
                "guild_daily", "guild_events", "job_runs", "job_status", "schema_version", "uptime_daily", "uptime_samples"], tables);
            Assert.Contains(logger.Entries, entry => entry.Message.Contains("is at version", StringComparison.Ordinal));

            Assert.Equal(0, await DashboardCommands.MigrateAsync(settings, logger, Token).WaitAsync(TimeSpan.FromMinutes(2), Token));

            var login = new MySqlConnectionStringBuilder(connection);
            Assert.All(logger.Entries, entry => Assert.DoesNotContain(login.Password, entry.Message));
        }

        // A server that refuses a login names it in its error, so only the error's type and SQL state are logged. Fails if
        // the driver's message is.
        [Fact]
        public async Task RefusedLoginIsLoggedWithoutTheDriversText()
        {
            var db = fixture.Require();
            var refused = new MySqlConnectionStringBuilder(db.BotConnection) { UserID = "made_up_login", Password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) };
            var settings = new ConfigurationBuilder().AddInMemoryCollection([KeyValuePair.Create<string, string?>("ConnectionStrings:BotConnection", refused.ConnectionString)]).Build();
            var logger = new ListLogger();

            Assert.Equal(1, await DashboardCommands.MigrateAsync(settings, logger, Token).WaitAsync(TimeSpan.FromMinutes(2), Token));

            Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("the database answered with MySqlException", StringComparison.Ordinal));
            Assert.All(logger.Entries, entry =>
            {
                Assert.DoesNotContain("made_up_login", entry.Message);
                Assert.DoesNotContain(refused.Password, entry.Message);
            });
        }
    }
}
