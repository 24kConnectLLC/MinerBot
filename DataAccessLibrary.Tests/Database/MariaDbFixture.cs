using Dapper;
using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Blacklist;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Discord;
using DataAccessLibrary.Migrations;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MySql.Data.MySqlClient;
using System.Diagnostics;
using System.Security.Cryptography;
using Testcontainers.MariaDb;

namespace DataAccessLibrary.Tests.Database
{
    // One MariaDB 11.4 in Docker for the whole run, with a website database and MinerBot's own, as on the server but with
    // made-up names. The website's tables are built from docs/schema/wordpress-tables.sql, and MinerBot's database is
    // migrated. The tests use a login with the bot's rights: everything in MinerBot's database, and only reading and
    // changing rows in the three website tables.
    // Starting never throws, because a fixture that throws fails every test instead of letting them skip, so each test
    // calls Require first.
    public sealed class MariaDbFixture : IAsyncLifetime
    {
        public const string SiteDatabase = "website_db";
        public const string BotDatabase = "minerbot_db";

        public const string ArmorTable = SiteDatabase + ".ihvph_armor_styles";
        public const string BlacklistTable = SiteDatabase + ".ihvph_blacklist";
        public const string GuildTable = SiteDatabase + ".ihvph_discord_servers";
        public const string AuditTable = BotDatabase + ".audit_log";
        public const string ReviewTable = BotDatabase + ".armor_reviews";
        public const string SessionTable = BotDatabase + ".dashboard_sessions";

        // CI sets this, so there the database tests fail instead of skipping
        internal const string RequiredSetting = "MINERBOT_DB_TESTS";

        const string Image = "mariadb:11.4";
        const string TestLogin = "minerbot_test";

        static readonly string[] WebsiteTables = ["ihvph_armor_styles", "ihvph_blacklist", "ihvph_discord_servers"];

        static readonly TimeSpan StartLimit = TimeSpan.FromMinutes(3);
        static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(30);

        MariaDbContainer? container;
        string? dockerMissing;
        Exception? startFailure;
        string testPassword = "";

        public string RootConnection { get; private set; } = "";
        public string SiteConnection { get; private set; } = "";
        public string BotConnection { get; private set; } = "";

        // Armor, Blacklist and Guild go to the website's database, and Bot to MinerBot's
        public IDictionary<DBConnectionName, string> Connections { get; private set; } = new Dictionary<DBConnectionName, string>();
        public SQLConnectionFactory Factory { get; private set; } = new(new Dictionary<DBConnectionName, string>());
        public DatabaseNames Names { get; private set; } = new(new Dictionary<DBConnectionName, string>());

        public async ValueTask InitializeAsync()
        {
            try
            {
                await StartAsync().WaitAsync(StartLimit, TestContext.Current.CancellationToken);
            }
            catch (DockerUnavailableException e)
            {
                // The first line says what's wrong, and the rest is advice about Testcontainers' settings
                dockerMissing = e.Message.Split('\n')[0].Trim();
            }
            catch (Exception e)
            {
                startFailure = e;
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Testcontainers' own clean-up removes the container if this doesn't finish
            if (container != null)
                await container.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMinutes(1));
        }

        // The fixture when the database is up. Otherwise the test is skipped when Docker is missing, unless
        // MINERBOT_DB_TESTS is required, and fails for any other reason.
        public MariaDbFixture Require()
        {
            Require(dockerMissing, startFailure, Environment.GetEnvironmentVariable(RequiredSetting));
            return this;
        }

        // Apart from the fixture, so a test can check it without stopping Docker
        internal static void Require(string? dockerMissing, Exception? startFailure, string? setting)
        {
            if (dockerMissing == null && startFailure == null)
                return;

            if (string.Equals(setting, "required", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{RequiredSetting} is required, but the database couldn't start: {dockerMissing ?? startFailure?.Message}", startFailure);

            if (dockerMissing != null)
                Assert.Skip($"Docker isn't available, so the database tests are skipped: {dockerMissing.TrimEnd('.')}. Set {RequiredSetting}=required to make this a failure.");

            throw new InvalidOperationException("The test database couldn't start", startFailure);
        }

        public ArmorData Armor(IMemoryCache cache) => new(Factory, cache);
        public ArmorSubmissionData Submissions() => new(Factory);
        public ArmorReviewData Reviews() => new(Factory, Names);
        public AuditLogData Audit() => new(Factory, Names);
        public DashboardSessionData Sessions() => new(Factory, Names);
        public BlacklistData Blacklist() => new(Factory);
        public GuildData Guilds() => new(Factory);

        // Each test starts from empty tables. TRUNCATE also starts AUTO_INCREMENT again, which DELETE doesn't, so an ID
        // that a test wrote past a 32-bit int can't reach the next test's new rows.
        public async Task ClearAsync(params string[] tables)
        {
            foreach (var table in tables)
                await RootExecuteAsync($"TRUNCATE TABLE {table}");
        }

        // How many transactions wait for a lock on the table, once that many do or the limit passes. Reading InnoDB's
        // transactions needs PROCESS, which only root has here. MariaDB refreshes what INNODB_TRX shows only after 100 ms
        // without a read of it, so every read comes 250 ms after the last one, and can't show what an earlier test saw.
        public async Task<long> LockWaitsAsync(string table, long count, TimeSpan limit)
        {
            var waiting = Stopwatch.StartNew();
            long found;

            do
            {
                await Task.Delay(250, TestContext.Current.CancellationToken);

                found = (await RootQueryAsync<long>(
                    "SELECT COUNT(*) FROM information_schema.INNODB_TRX WHERE trx_state = 'LOCK WAIT' AND trx_query LIKE @pattern",
                    new { pattern = $"%{table}%" })).Single();
            }
            while (found < count && waiting.Elapsed < limit);

            return found;
        }

        public Task RootExecuteAsync(string sql, object? parameters = null) =>
            WithRoot(connection => connection.ExecuteAsync(Command(sql, parameters)));

        public async Task<List<T>> RootQueryAsync<T>(string sql, object? parameters = null) =>
            (await WithRoot(connection => connection.QueryAsync<T>(Command(sql, parameters)))).ToList();

        // A row as the website saves one, with every column given
        public Task AddArmorAsync(ArmorSubmission armor) => RootExecuteAsync(
            $@"INSERT INTO {ArmorTable} (ID, CreatedAt, UserID, Status, Author, ArmorName, ArmorDescription, ArmorScreenshot, ArmorChoice, ArmorSkin,
               Helmet, Visor, Torso, Shoulders, Biceps, Forearms, Hands, Pelvis, Thighs, Legs, Boots, Shields, Other, ColorArray)
               VALUES (@ID, @CreatedAt, @UserID, @Status, @Author, @ArmorName, @ArmorDescription, @ArmorScreenshot, @ArmorChoice, @ArmorSkin,
               @Helmet, @Visor, @Torso, @Shoulders, @Biceps, @Forearms, @Hands, @Pelvis, @Thighs, @Legs, @Boots, @Shields, @Other, @ColorArray)",
            armor);

        // A database of its own for a test that needs an empty one, which the test login may do anything in
        public async Task<(string Name, string Connection)> CreateDatabaseAsync(string prefix)
        {
            var name = $"{prefix}_{RandomHex(4)}";
            await CreateDatabaseNamedAsync(name);
            return (name, LoginConnection(name));
        }

        public async Task CreateDatabaseNamedAsync(string name)
        {
            var quoted = DatabaseNames.QuoteIdentifier(name);

            await RootExecuteAsync($"CREATE DATABASE {quoted}");
            await RootExecuteAsync($"GRANT ALL PRIVILEGES ON {quoted}.* TO '{TestLogin}'@'%'");
        }

        public string LoginConnection(string database) => Connection(TestLogin, testPassword, database);

        // Without CharSet, MySql.Data uses the server's default character set, which is latin1 here and on the live server
        public string LoginConnectionWithoutCharacterSet(string database) => Connection(TestLogin, testPassword, database, utf8mb4: false);

        // Another login's connection, with utf8mb4, such as the dashboard's in DashboardRightsTests
        public string ConnectionAs(string user, string password, string database) => Connection(user, password, database);

        async Task StartAsync()
        {
            // Made up for this run, so no password is written down anywhere
            var rootPassword = RandomHex(16);
            testPassword = RandomHex(16);

            // The live server's default character set is latin1, MariaDB's own default. The image's config would make it
            // utf8mb4, and Testcontainers happens to replace that config, so the server is told outright, and
            // CharacterSetTests checks it. skip-name-resolve is the image's, so connecting looks up no names.
            container = new MariaDbBuilder(Image)
                .WithUsername("root")
                .WithPassword(rootPassword)
                .WithCommand("--character-set-server=latin1", "--collation-server=latin1_swedish_ci", "--skip-name-resolve")
                .Build();
            await container.StartAsync(TestContext.Current.CancellationToken);

            // MySql.Data would read the @ in 'minerbot_test'@'%' as a parameter it wasn't given
            RootConnection = new MySqlConnectionStringBuilder(Connection("root", rootPassword, "")) { AllowUserVariables = true }.ConnectionString;

            await RootExecuteAsync($"CREATE DATABASE {SiteDatabase}");
            await RootExecuteAsync($"CREATE DATABASE {BotDatabase}");

            var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "schema", "wordpress-tables.sql"), TestContext.Current.CancellationToken);

            foreach (var statement in MigrationScript.Split(schema))
                await RootExecuteAsync($"USE {SiteDatabase}; {statement}");

            await RootExecuteAsync($"CREATE USER '{TestLogin}'@'%' IDENTIFIED BY '{testPassword}'");
            await RootExecuteAsync($"GRANT ALL PRIVILEGES ON {BotDatabase}.* TO '{TestLogin}'@'%'");

            foreach (var table in WebsiteTables)
                await RootExecuteAsync($"GRANT SELECT, INSERT, UPDATE, DELETE ON {SiteDatabase}.{table} TO '{TestLogin}'@'%'");

            SiteConnection = LoginConnection(SiteDatabase);
            BotConnection = LoginConnection(BotDatabase);

            Connections = new Dictionary<DBConnectionName, string>
            {
                [DBConnectionName.Armor] = SiteConnection,
                [DBConnectionName.Blacklist] = SiteConnection,
                [DBConnectionName.Guild] = SiteConnection,
                [DBConnectionName.Bot] = BotConnection
            };

            Factory = new SQLConnectionFactory(Connections);
            Names = new DatabaseNames(Connections);

            await DatabaseMigrations.MigrateAsync(BotConnection, NullLogger.Instance, TestContext.Current.CancellationToken);
        }

        // utf8mb4, as the example connection strings name it. MySql.Data otherwise sends text in the server's default
        // character set, latin1 here, which turns anything outside it into a question mark.
        string Connection(string user, string password, string database, bool utf8mb4 = true)
        {
            var connection = new MySqlConnectionStringBuilder
            {
                Server = container!.Hostname,
                Port = container.GetMappedPublicPort(MariaDbBuilder.MariaDbPort),
                UserID = user,
                Password = password,
                Database = database,
                SslMode = MySqlSslMode.Disabled
            };

            if (utf8mb4)
                connection.CharacterSet = "utf8mb4";

            return connection.ConnectionString;
        }

        async Task<T> WithRoot<T>(Func<MySqlConnection, Task<T>> call)
        {
            await using var connection = new MySqlConnection(RootConnection);
            return await call(connection).WaitAsync(CallLimit, TestContext.Current.CancellationToken);
        }

        static CommandDefinition Command(string sql, object? parameters) =>
            new(sql, parameters, cancellationToken: TestContext.Current.CancellationToken);

        static string RandomHex(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
    }
}
