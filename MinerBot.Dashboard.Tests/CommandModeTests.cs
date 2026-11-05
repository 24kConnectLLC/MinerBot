using System.Diagnostics;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MinerBot.Dashboard.Hosting;
using MinerBot.Dashboard.Tests.Support;
using MySql.Data.MySqlClient;

namespace MinerBot.Dashboard.Tests
{
    // The two commands the deploy and the container run with the same program, without the web host
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
        public void FindTakesOnlyTheFirstWord(string[] args, string? command)
        {
            Assert.Equal(command, DashboardCommands.Find(args));
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
            Assert.Equal(["armor_reviews", "audit_log", "dashboard_sessions", "schema_version"], tables);
            Assert.Contains(logger.Entries, entry => entry.Message.Contains("is at version", StringComparison.Ordinal));

            Assert.Equal(0, await DashboardCommands.MigrateAsync(settings, logger, Token).WaitAsync(TimeSpan.FromMinutes(2), Token));

            var login = new MySqlConnectionStringBuilder(connection);
            Assert.All(logger.Entries, entry => Assert.DoesNotContain(login.Password, entry.Message));
        }
    }
}
