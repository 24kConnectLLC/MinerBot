using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Blacklist;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Dashboard.Models;
using DataAccessLibrary.Discord;
using DataAccessLibrary.Health;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;

namespace DataAccessLibrary.Tests
{
    // How the connection strings become connections and database names. No database is needed.
    public class ConnectionSetupTests
    {
        // Made up, and never connected to
        const string Site = "Server=127.0.0.1;Port=3306;Database=site_db;Uid=user;Pwd=password;SslMode=Disabled";
        const string Bot = "Server=127.0.0.1;Port=3306;Database=bot_db;Uid=user;Pwd=password;SslMode=Disabled";

        static IConfiguration Settings(params (string Key, string Value)[] settings) =>
            new ConfigurationBuilder().AddInMemoryCollection(settings.Select(setting => KeyValuePair.Create<string, string?>(setting.Key, setting.Value))).Build();

        // What the dashboard's start-up check reports for a connection string, in words that never quote it
        [Theory]
        [InlineData(Site, null)]
        [InlineData("Server=127.0.0.1;Initial Catalog=site_db;Uid=user", null)]
        [InlineData("Server=127.0.0.1;Uid=user;Pwd=password", "names no database")]
        [InlineData("", "names no database")]
        [InlineData("Server=127.0.0.1;Database=site_db;SslMode=Requird", "can't be read")]
        [InlineData("Server=127.0.0.1;Database=site_db;Port=port", "can't be read")]
        [InlineData("Server=127.0.0.1;Database=site_db;Colour=blue", "can't be read")]
        [InlineData("Server=127.0.0.1;Database=site\\db", "names a database that MariaDB or MySql.Data can't use")]
        public void ProblemSaysWhatStopsAConnectionString(string connectionString, string? problem)
        {
            Assert.Equal(problem, DatabaseNames.Problem(connectionString));
        }

        // Every registration, the way the dashboard will make them
        static ServiceProvider Provider(IConfiguration settings, ISQLConnectionFactory? factory = null)
        {
            var services = new ServiceCollection()
                .AddDataAccessServices(settings)
                .AddArmorAccess()
                .AddBlacklistAccess()
                .AddGuildAccess()
                .AddArmorSubmissionAccess()
                .AddArmorReviewAccess()
                .AddAuditLogAccess()
                .AddDashboardSessionAccess()
                .AddDatabaseHealthAccess();

            if (factory != null)
                services.AddSingleton(factory);

            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        }

        [Fact]
        public void BotIsReadFromBotConnection()
        {
            using var provider = Provider(Settings(("ConnectionStrings:DefaultConnection", Site), ("ConnectionStrings:BotConnection", Bot)));
            var factory = provider.GetRequiredService<ISQLConnectionFactory>();
            var names = provider.GetRequiredService<DatabaseNames>();

            using var bot = factory.Create(DBConnectionName.Bot);
            using var armor = factory.Create(DBConnectionName.Armor);

            Assert.Equal("bot_db", bot.Database);
            Assert.Equal("site_db", armor.Database);
            Assert.Equal("`bot_db`", names.Of(DBConnectionName.Bot));
            Assert.Equal("`bot_db`", names.Bot);
            Assert.Equal("`site_db`", names.Of(DBConnectionName.Guild));
        }

        // The bot runs without BotConnection, so only a call that needs MinerBot's database may fail, and with a clear reason
        [Fact]
        public async Task MissingBotConnectionFailsOnlyWhenUsed()
        {
            var factory = new NoDatabase();
            using var provider = Provider(Settings(("ConnectionStrings:DefaultConnection", Site)), factory);

            provider.GetRequiredService<IArmorData>();
            provider.GetRequiredService<IBlacklistData>();
            provider.GetRequiredService<IGuildData>();
            provider.GetRequiredService<IArmorSubmissionData>();
            var reviews = provider.GetRequiredService<IArmorReviewData>();
            var audit = provider.GetRequiredService<IAuditLogData>();
            var sessions = provider.GetRequiredService<IDashboardSessionData>();
            Assert.IsType<DatabaseHealthData>(provider.GetRequiredService<IDatabaseHealthData>());

            var calls = new Func<Task>[]
            {
                () => audit.ListAudit(new AuditQuery()),
                () => audit.AddAudit(new AuditEntry { at = DateTime.UtcNow, actor_role = AuditRole.Owner, action = "armor.approve", outcome = AuditOutcome.Ok }),
                () => reviews.ListReviews(1),
                () => reviews.AddReview(new ArmorReview { armor_id = 1, to_status = "Approved", reviewed_by = 1, reviewed_at = DateTime.UtcNow }),
                () => sessions.GetSession(new string('k', 43), DateTime.UtcNow),
                () => sessions.CreateSession(new string('k', 43), new DashboardSession { user_name = "Test User", ticket = [1] }),
                () => sessions.DeleteOldestSessions(1, 5),
                () => sessions.RunInTransaction(_ => Task.FromResult(0))
            };

            foreach (var call in calls)
            {
                var missing = await Assert.ThrowsAsync<InvalidOperationException>(call);
                Assert.Contains("BotConnection", missing.Message);
            }

            Assert.Equal(0, factory.Created);
        }

        [Fact]
        public void BotConnectionWithoutADatabaseFailsClearly()
        {
            var names = new DatabaseNames(new Dictionary<DBConnectionName, string> { [DBConnectionName.Bot] = "Server=127.0.0.1;Uid=user" });

            var missing = Assert.Throws<InvalidOperationException>(() => names.Bot);
            Assert.Contains("BotConnection names no database", missing.Message);
        }

        [Theory]
        [InlineData("Server=127.0.0.1;Initial Catalog=x;Uid=user")]
        [InlineData("Server=127.0.0.1;DATABASE=x;Uid=user")]
        [InlineData("Server=127.0.0.1;database=x;Uid=user")]
        [InlineData("server=127.0.0.1; Database = x ;uid=user")]
        public void DatabaseNameIsReadWhateverTheKeySpelling(string connection)
        {
            var names = new DatabaseNames(new Dictionary<DBConnectionName, string> { [DBConnectionName.Bot] = connection });

            Assert.Equal("`x`", names.Bot);
        }

        [Theory]
        [InlineData("plain_name", "`plain_name`")]
        [InlineData("minerbot_db", "`minerbot_db`")]
        [InlineData("minerbot-alt", "`minerbot-alt`")]
        [InlineData("a`b", "`a``b`")]
        [InlineData("``", "``````")]
        [InlineData("x; DROP TABLE y", "`x; DROP TABLE y`")]
        public void NamesAreQuoted(string name, string quoted)
        {
            Assert.Equal(quoted, DatabaseNames.QuoteIdentifier(name));
        }

        [Theory]
        [InlineData("")]
        [InlineData("trailing ")]
        public void NamesMariaDbRefusesAreRefused(string name)
        {
            Assert.Throws<ArgumentException>(() => DatabaseNames.QuoteIdentifier(name));
        }

        // MariaDB takes these, but MySql.Data reads a backslash inside backticks as an escape, so it runs the name on past
        // its closing backtick and leaves the parameters after it for the server, which fills them with NULL
        [Theory]
        [InlineData("ends_in\\")]
        [InlineData("back\\`tick")]
        [InlineData("back\\slash")]
        public void NamesWithABackslashAreRefused(string name)
        {
            Assert.Throws<ArgumentException>(() => DatabaseNames.QuoteIdentifier(name));
            Assert.Throws<ArgumentException>(() => new DatabaseNames(new Dictionary<DBConnectionName, string> { [DBConnectionName.Bot] = $"Server=127.0.0.1;Database={name};Uid=user" }).Bot);
        }

        // The connection each repository opens, read without opening it
        static string CharacterSet(DbBaseConnection repository, ISQLConnectionFactory factory)
        {
            using var connection = repository.CreateConnection(factory);
            return new MySqlConnectionStringBuilder(connection.ConnectionString).CharacterSet;
        }

        // Every repository the dashboard uses sends utf8mb4 whatever the connection string says, so emoji and zero-width
        // characters reach the tables and name checks as they are. The bot's own two keep theirs, so the running bot is unchanged.
        [Theory]
        [InlineData("")]
        [InlineData(";CharSet=latin1")]
        [InlineData(";CharSet=utf8")]
        public void TheDashboardsRepositoriesSendUtf8mb4(string setting)
        {
            var connections = new Dictionary<DBConnectionName, string>
            {
                [DBConnectionName.Armor] = Site + setting,
                [DBConnectionName.Blacklist] = Site + setting,
                [DBConnectionName.Guild] = Site + setting,
                [DBConnectionName.Bot] = Bot + setting
            };
            var factory = new SQLConnectionFactory(connections);
            var names = new DatabaseNames(connections);
            using var cache = new MemoryCache(new MemoryCacheOptions());

            Assert.All(new DbBaseConnection[]
            {
                new ArmorSubmissionData(factory),
                new BlacklistData(factory),
                new ArmorReviewData(factory, names),
                new AuditLogData(factory, names),
                new DashboardSessionData(factory, names)
            }, repository => Assert.Equal("utf8mb4", CharacterSet(repository, factory)));

            var configured = new MySqlConnectionStringBuilder(Site + setting).CharacterSet;
            Assert.Equal(configured, CharacterSet(new ArmorData(factory, cache), factory));
            Assert.Equal(configured, CharacterSet(new GuildData(factory), factory));
        }

        // A string that already names utf8mb4 is used exactly as it is, so the repositories share one pool, and nothing else
        // in it changes when one is added
        [Fact]
        public void ConnectionStringThatNamesUtf8mb4IsLeftAlone()
        {
            const string named = Site + ";CharSet=UTF8MB4";
            var factory = new SQLConnectionFactory(new Dictionary<DBConnectionName, string> { [DBConnectionName.Armor] = named });

            using (var expected = new MySqlConnection(named))
            using (var connection = new ArmorSubmissionData(factory).CreateConnection(factory))
                Assert.Equal(expected.ConnectionString, connection.ConnectionString);

            var added = new SQLConnectionFactory(new Dictionary<DBConnectionName, string> { [DBConnectionName.Armor] = Site });
            using var changed = new ArmorSubmissionData(added).CreateConnection(added);
            var settings = new MySqlConnectionStringBuilder(changed.ConnectionString);

            Assert.Equal("site_db", settings.Database);
            Assert.Equal("password", settings.Password);
            Assert.Equal(MySqlSslMode.Disabled, settings.SslMode);
            Assert.Equal("utf8mb4", settings.CharacterSet);
        }

        // Kept out of the theory's data, which shows its values in the test's name
        [Fact]
        public void NameWithANulIsRefused()
        {
            Assert.Throws<ArgumentException>(() => DatabaseNames.QuoteIdentifier("nul" + (char)0 + "name"));
        }

        [Fact]
        public void NamesUpTo64CharactersAreAllowed()
        {
            Assert.Equal($"`{new string('a', 64)}`", DatabaseNames.QuoteIdentifier(new string('a', 64)));
            Assert.Throws<ArgumentException>(() => DatabaseNames.QuoteIdentifier(new string('a', 65)));
        }
    }
}
