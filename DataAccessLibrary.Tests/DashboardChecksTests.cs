using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Tests.Database;
using MySql.Data.MySqlClient;

namespace DataAccessLibrary.Tests
{
    // The Sessions and security page's reads of the dashboard's own database login and of MinerBot's migrations, made as
    // that login with exactly the rights deploy/dashboard-rights.sql gives it, as DashboardLogin makes it
    [Collection("Database")]
    public class DashboardChecksTests(MariaDbFixture fixture)
    {
        const string Site = MariaDbFixture.SiteDatabase;
        const string Bot = MariaDbFixture.BotDatabase;

        // DashboardLogin's login, on % because the tests reach MariaDB through a mapped port
        const string Grantee = "'minerbot-dashboard'@'%'";

        // As the dashboard makes them, with both connection strings on the login
        static DashboardChecks Checks(MariaDbFixture db, string password)
        {
            var connections = new Dictionary<DBConnectionName, string>
            {
                [DBConnectionName.Armor] = DashboardLogin.ConnectionAs(db, password, Site),
                [DBConnectionName.Blacklist] = DashboardLogin.ConnectionAs(db, password, Site),
                [DBConnectionName.Guild] = DashboardLogin.ConnectionAs(db, password, Site),
                [DBConnectionName.Bot] = DashboardLogin.ConnectionAs(db, password, Bot)
            };

            return new DashboardChecks(new SQLConnectionFactory(connections), new DatabaseNames(connections));
        }

        // What the login reads of its own rights is what deploy lists for it, but for the limits, which it reads apart: the
        // list this version expects with this install's names, nothing missing and nothing more. Never through SHOW GRANTS.
        [Fact]
        public async Task OwnRightsAsTheDashboardsLoginAreExactlyTheList()
        {
            var db = fixture.Require();

            await DashboardLogin.AsDashboardAsync(db, async password =>
            {
                var rights = await Checks(db, password).ListOwnRights();

                Assert.Equal(DashboardRights.For(Site, Bot), rights.Expected);
                Assert.Equal(rights.Expected, rights.Lines);
                Assert.Empty(rights.Missing);
                Assert.Empty(rights.Extra);

                // deploy's own listing of the login, as root reads it, holds the same lines and its limits
                var listed = await db.RootQueryAsync<string>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "deploy", "rights-list.sql"))
                    .Replace("{{WHO}}", "''minerbot-dashboard''@''%''"));
                Assert.Equal(listed.Where(line => !line.StartsWith("limits ", StringComparison.Ordinal)).Order(StringComparer.Ordinal), rights.Lines);
            });
        }

        // A right added by hand is more than this version uses, and one taken away is missing, each named in deploy's words
        [Fact]
        public async Task ARightAddedOrTakenAwayByHandIsNamed()
        {
            var db = fixture.Require();

            await DashboardLogin.AsDashboardAsync(db, async password =>
            {
                await db.RootExecuteAsync($"GRANT DELETE ON {Bot}.armor_reviews TO {Grantee}");
                await db.RootExecuteAsync($"REVOKE SELECT ON {Bot}.bot_settings FROM {Grantee}");

                var rights = await Checks(db, password).ListOwnRights();

                Assert.Equal([$"table SELECT {Bot} bot_settings"], rights.Missing);
                Assert.Equal([$"table DELETE {Bot} armor_reviews"], rights.Extra);
            });
        }

        // The limits are the login's own, as its session has them: 15 connections and 20 seconds a statement after the
        // rights file, as root reads them for that login, and whatever ALTER USER sets later for a new connection
        [Fact]
        public async Task LimitsAreTheLoginsOwn()
        {
            var db = fixture.Require();

            await DashboardLogin.AsDashboardAsync(db, async password =>
            {
                var checks = Checks(db, password);
                var rights = await checks.ListOwnRights();
                var root = Assert.Single(await db.RootQueryAsync<(long Connections, decimal Seconds)>(
                    "SELECT CAST(max_user_connections AS SIGNED), max_statement_time FROM mysql.user WHERE User = 'minerbot-dashboard' AND Host = '%'"));

                Assert.Equal((15L, 20.000000m), root);
                Assert.Equal(((long?)root.Connections, (decimal?)root.Seconds), (rights.Connections, rights.StatementSeconds));
                Assert.Equal((DashboardRights.Connections, DashboardRights.StatementSeconds), ((int)rights.Connections!.Value, (int)rights.StatementSeconds!.Value));

                await db.RootExecuteAsync($"ALTER USER {Grantee} WITH MAX_USER_CONNECTIONS 7 MAX_STATEMENT_TIME 3.5");
                rights = await checks.ListOwnRights();

                Assert.Equal(((long?)7, (decimal?)3.5m), (rights.Connections, rights.StatementSeconds));
            });
        }

        // The migrations MinerBot's database has run, read as the login through its one right on schema_version, are the
        // ones built into this version, which the fixture ran. Without that right MariaDB refuses the read.
        [Fact]
        public async Task MigrationsReadAsTheDashboardsLoginMatchTheBuild()
        {
            var db = fixture.Require();

            await DashboardLogin.AsDashboardAsync(db, async password =>
            {
                var checks = Checks(db, password);
                var migrations = await checks.ListMigrations();

                Assert.Equal(DatabaseMigrations.Expected(), migrations);
                Assert.Equal(await db.RootQueryAsync<(int, string, string)>($"SELECT `version`, `name`, `checksum` FROM {Bot}.schema_version ORDER BY `version`"),
                    migrations.Select(migration => (migration.Version, migration.Name, migration.Checksum)));

                await db.RootExecuteAsync($"REVOKE SELECT ON {Bot}.schema_version FROM {Grantee}");
                var refused = await Assert.ThrowsAsync<MySqlException>(checks.ListMigrations);
                Assert.Equal(1142, refused.Number);
            });
        }

        // CURRENT_USER() names an account as user@host, and information_schema quotes each part. A host never holds an @, so
        // the last one splits them, and a user name may hold one.
        [Theory]
        [InlineData("minerbot-dashboard@127.0.0.1", "'minerbot-dashboard'@'127.0.0.1'")]
        [InlineData("minerbot-dashboard@localhost", "'minerbot-dashboard'@'localhost'")]
        [InlineData("minerbot-dashboard@%", "'minerbot-dashboard'@'%'")]
        [InlineData("odd@name@127.0.0.1", "'odd@name'@'127.0.0.1'")]
        public void TheGranteeIsTheAccountQuoted(string account, string grantee)
        {
            Assert.Equal(grantee, DashboardChecks.Grantee(account));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("minerbot-dashboard")]
        [InlineData("@localhost")]
        public void AnAccountWithoutAUserAndAHostIsRefused(string? account)
        {
            Assert.Throws<InvalidOperationException>(() => DashboardChecks.Grantee(account));
        }
    }
}
