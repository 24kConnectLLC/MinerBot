using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Blacklist;
using DataAccessLibrary.Blacklist.Models;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Dashboard.Models;
using DataAccessLibrary.Discord;
using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Health;
using DataAccessLibrary.Migrations;
using DataAccessLibrary.Tests.Database;
using MinerBot.Shared.Armor;
using MySql.Data.MySqlClient;

namespace DataAccessLibrary.Tests
{
    // deploy/dashboard-rights.sql, applied as root statement by statement, as deploy.sh applies it, to the web dashboard's
    // login: everything today's dashboard does works, and nothing else does. The login is made fresh for each test with a
    // password of its own, and dropped afterwards with every right it had, because the other database tests share these
    // databases. A pull request that adds a right changes the list here on purpose.
    [Collection("Database")]
    public partial class DashboardRightsTests(MariaDbFixture fixture)
    {
        const string Login = "minerbot-dashboard";

        // % rather than the server's localhost and 127.0.0.1, because the tests reach MariaDB through a mapped port
        const string Grantee = "'" + Login + "'@'%'";

        const string Site = MariaDbFixture.SiteDatabase;
        const string Bot = MariaDbFixture.BotDatabase;

        // How MariaDB refuses: no right in the database (1044), no FILE right for an outfile (1045), another login's
        // session (1095), no right on the table (1142) or the column (1143), and no global right (1227)
        static readonly int[] Refusals = [1044, 1045, 1095, 1142, 1143, 1227];

        static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(30);
        static readonly DateTime Noon = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        const ulong UserId = 1000000000000000001;

        // Exactly what the login may do, as rights-list.sql lists it, sorted. Written out, so a new right changes this too.
        static readonly string[] Expected =
        [
            "column SELECT website_db ihvph_discord_servers guild_id",
            "global USAGE",
            "limits 15 20.000000 0 0 0",
            "table DELETE minerbot_db dashboard_sessions",
            "table INSERT minerbot_db audit_log",
            "table INSERT minerbot_db dashboard_sessions",
            "table SELECT minerbot_db dashboard_sessions",
            "table UPDATE minerbot_db dashboard_sessions"
        ];

        static CancellationToken Token => TestContext.Current.CancellationToken;

        static string DeployFile(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "deploy", name));

        // With this run's names, as deploy.sh renders it
        static string Rendered(string sql) => sql
            .Replace("{{SITE_DB}}", Site)
            .Replace("{{BOT_DB}}", Bot)
            .Replace("{{GRANTEES}}", Grantee);

        // The login as setup-dashboard.sh makes it, with no rights, then the file applied. Dropped afterwards whatever
        // happened, which takes every right it had with it.
        async Task WithLoginAsync(Func<MariaDbFixture, string, Task> test)
        {
            var db = fixture.Require();
            var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

            await db.RootExecuteAsync($"DROP USER IF EXISTS {Grantee}");
            await db.RootExecuteAsync($"CREATE USER {Grantee} IDENTIFIED BY '{password}'");

            try
            {
                await ApplyAsync(db);
                await test(db, password);
            }
            finally
            {
                await db.RootExecuteAsync($"DROP USER IF EXISTS {Grantee}");
            }
        }

        static async Task ApplyAsync(MariaDbFixture db)
        {
            foreach (var statement in MigrationScript.Split(Rendered(DeployFile("dashboard-rights.sql"))))
                await db.RootExecuteAsync(statement);
        }

        // rights-list.sql for the login, as deploy.sh compares two logins, sorted the same way
        static async Task<List<string>> RightsAsync(MariaDbFixture db)
        {
            var rights = await db.RootQueryAsync<string>(DeployFile("rights-list.sql").Replace("{{WHO}}", $"''{Login}''@''%''"));
            return [.. rights.Order(StringComparer.Ordinal)];
        }

        // The login's own connection, never pooled, so nothing of it stays open once the login is dropped. Its user
        // variables are allowed, so MySql.Data leaves an @ in a statement alone.
        static string ConnectionAs(MariaDbFixture db, string password, string database) =>
            new MySqlConnectionStringBuilder(db.ConnectionAs(Login, password, database)) { Pooling = false, AllowUserVariables = true }.ConnectionString;

        // The connections the dashboard has: the website's database for Armor, Blacklist and Guild, MinerBot's for Bot
        static Dictionary<DBConnectionName, string> Connections(MariaDbFixture db, string password)
        {
            var site = ConnectionAs(db, password, Site);

            return new Dictionary<DBConnectionName, string>
            {
                [DBConnectionName.Armor] = site,
                [DBConnectionName.Blacklist] = site,
                [DBConnectionName.Guild] = site,
                [DBConnectionName.Bot] = ConnectionAs(db, password, Bot)
            };
        }

        static async Task RunAsLoginAsync(MariaDbFixture db, string password, string sql)
        {
            await using var connection = new MySqlConnection(ConnectionAs(db, password, Bot));
            await connection.OpenAsync(Token);
            await using var command = new MySqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(Token).WaitAsync(CallLimit, Token);
        }

        static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        static DashboardSession Session(DateTime? expiresAt = null) => new()
        {
            user_id = UserId,
            user_name = "Test Admin",
            created_at = Noon,
            last_seen_at = Noon,
            expires_at = expiresAt ?? Noon.AddHours(12),
            ip = "203.0.113.7",
            user_agent = "Test Browser/1.0",
            ticket = [1, 2, 3]
        };

        static AuditEntry Audit(string action) => new()
        {
            at = Noon,
            actor_id = UserId,
            actor_name = "Test Admin",
            actor_role = AuditRole.Admin,
            action = action,
            outcome = AuditOutcome.Ok,
            target_type = "session"
        };

        [Fact]
        public async Task RightsAreExactlyTheList()
        {
            await WithLoginAsync(async (db, _) => Assert.Equal(Expected, await RightsAsync(db)));
        }

        // Every repository call the dashboard makes today, under its login: the sessions, the audit rows, on MinerBot's own
        // connection and inside a transaction on the website's, and the readiness check of both databases
        [Fact]
        public async Task EverythingTodaysDashboardDoesWorks()
        {
            await WithLoginAsync(async (db, password) =>
            {
                await db.ClearAsync(MariaDbFixture.SessionTable, MariaDbFixture.AuditTable);

                var connections = Connections(db, password);
                var factory = new SQLConnectionFactory(connections);
                var names = new DatabaseNames(connections);
                var sessions = new DashboardSessionData(factory, names);
                var audit = new AuditLogData(factory, names);
                var (key, second, third) = (NewKey(), NewKey(), NewKey());

                await sessions.CreateSession(key, Session());
                Assert.NotNull(await sessions.GetSession(key, Noon));
                Assert.True(await sessions.TouchSession(key, Noon.AddMinutes(1)));
                Assert.True(await sessions.RenewSession(key, [4, 5, 6]));

                // The cap's DELETE reads the table again in its subquery
                await sessions.CreateSession(second, Session());
                await sessions.CreateSession(third, Session());
                Assert.Equal(1, await sessions.DeleteOldestSessions(UserId, 2));
                Assert.Equal(1, await sessions.DeleteOtherSessions(UserId, key));
                Assert.True(await sessions.DeleteSession(key));

                await sessions.CreateSession(NewKey(), Session(expiresAt: Noon.AddMinutes(-1)));
                Assert.Equal(1, await sessions.DeleteExpiredSessions(Noon, Noon.AddHours(-1)));

                // A sign-in: the session, the cap and the audit row commit together on MinerBot's own connection
                Assert.True(await sessions.RunInTransaction(async transaction =>
                {
                    await sessions.CreateSession(NewKey(), Session(), transaction);
                    await sessions.DeleteOldestSessions(UserId, 5, transaction);
                    await audit.AddAudit(Audit("session.start"), transaction);
                    return true;
                }));

                await audit.AddAudit(Audit("auth.refused"));

                // An audit row inside a transaction on the website's connection, as a change to a website table writes one
                Assert.True(await new ArmorSubmissionData(factory).RunInTransaction(async transaction =>
                {
                    await audit.AddAudit(Audit("armor.approve"), transaction);
                    return true;
                }));

                var health = new DatabaseHealthData(factory);
                Assert.True(await health.Answers(DBConnectionName.Armor, TimeSpan.FromSeconds(5)).WaitAsync(CallLimit, Token));
                Assert.True(await health.Answers(DBConnectionName.Bot, TimeSpan.FromSeconds(5)).WaitAsync(CallLimit, Token));

                Assert.Equal(["armor.approve", "auth.refused", "session.start"],
                    (await db.RootQueryAsync<string>($"SELECT action FROM {MariaDbFixture.AuditTable}")).Order(StringComparer.Ordinal));
                Assert.Equal(1, (await db.RootQueryAsync<long>($"SELECT COUNT(*) FROM {MariaDbFixture.SessionTable}")).Single());
            });
        }

        // Everything else is refused, checked by MariaDB's error number. {root} is root's own session, for KILL.
        public static TheoryData<string, string> Forbidden => new()
        {
            // The audit log is only ever added to, and read with the audit page
            { "read the audit log", $"SELECT * FROM {Bot}.audit_log" },
            { "change the audit log", $"UPDATE {Bot}.audit_log SET detail = 'changed' WHERE 1 = 0" },
            { "delete from the audit log", $"DELETE FROM {Bot}.audit_log WHERE 1 = 0" },
            { "truncate the audit log", $"TRUNCATE TABLE {Bot}.audit_log" },
            // MinerBot's other tables
            { "read armor_reviews", $"SELECT * FROM {Bot}.armor_reviews" },
            { "add to armor_reviews", $"INSERT INTO {Bot}.armor_reviews (armor_id, to_status, reviewed_by, reviewed_at) VALUES (1, 'Approved', 1, NOW(3))" },
            { "change armor_reviews", $"UPDATE {Bot}.armor_reviews SET reason = 'changed' WHERE 1 = 0" },
            { "delete from armor_reviews", $"DELETE FROM {Bot}.armor_reviews WHERE 1 = 0" },
            { "read schema_version", $"SELECT * FROM {Bot}.schema_version" },
            { "add to schema_version", $"INSERT INTO {Bot}.schema_version (version, name, checksum, applied_at) VALUES (9999, 'x', 'x', NOW(3))" },
            { "change schema_version", $"UPDATE {Bot}.schema_version SET name = 'changed' WHERE 1 = 0" },
            { "delete from schema_version", $"DELETE FROM {Bot}.schema_version WHERE 1 = 0" },
            // Structure, in both databases
            { "create a table in MinerBot's database", $"CREATE TABLE {Bot}.rights_check (id INT)" },
            { "create a table in the website's database", $"CREATE TABLE {Site}.rights_check (id INT)" },
            { "alter a table in MinerBot's database", $"ALTER TABLE {Bot}.dashboard_sessions ADD COLUMN rights_check INT" },
            { "alter a table in the website's database", $"ALTER TABLE {Site}.ihvph_discord_servers ADD COLUMN rights_check INT" },
            { "drop a table in MinerBot's database", $"DROP TABLE {Bot}.rights_check_missing" },
            { "drop a table in the website's database", $"DROP TABLE {Site}.rights_check_missing" },
            { "index a table in MinerBot's database", $"CREATE INDEX rights_check ON {Bot}.dashboard_sessions (user_name)" },
            { "index a table in the website's database", $"CREATE INDEX rights_check ON {Site}.ihvph_discord_servers (map_channel_id)" },
            { "create a view in MinerBot's database", $"CREATE VIEW {Bot}.rights_check AS SELECT 1" },
            { "create a view in the website's database", $"CREATE VIEW {Site}.rights_check AS SELECT 1" },
            { "create a trigger in MinerBot's database", $"CREATE TRIGGER {Bot}.rights_check BEFORE INSERT ON {Bot}.dashboard_sessions FOR EACH ROW SET NEW.user_agent = NEW.user_agent" },
            { "create a trigger in the website's database", $"CREATE TRIGGER {Site}.rights_check BEFORE INSERT ON {Site}.ihvph_discord_servers FOR EACH ROW SET NEW.map_channel_id = NEW.map_channel_id" },
            { "create a procedure in MinerBot's database", $"CREATE PROCEDURE {Bot}.rights_check() SELECT 1" },
            { "create a procedure in the website's database", $"CREATE PROCEDURE {Site}.rights_check() SELECT 1" },
            { "create a temporary table in MinerBot's database", $"CREATE TEMPORARY TABLE {Bot}.rights_check (id INT)" },
            { "create a temporary table in the website's database", $"CREATE TEMPORARY TABLE {Site}.rights_check (id INT)" },
            // The website's tables: nothing is read but the one column, and nothing is written
            { "read the armor", $"SELECT * FROM {Site}.ihvph_armor_styles" },
            { "read the blacklist", $"SELECT * FROM {Site}.ihvph_blacklist" },
            { "read another column of the servers", $"SELECT armor_channel_id FROM {Site}.ihvph_discord_servers" },
            { "read every column of the servers", $"SELECT * FROM {Site}.ihvph_discord_servers" },
            { "add armor", $"INSERT INTO {Site}.ihvph_armor_styles (Author) VALUES ('x')" },
            { "change armor", $"UPDATE {Site}.ihvph_armor_styles SET Status = 'Approved' WHERE 1 = 0" },
            { "delete armor", $"DELETE FROM {Site}.ihvph_armor_styles WHERE 1 = 0" },
            { "add to the blacklist", $"INSERT INTO {Site}.ihvph_blacklist (steam_id) VALUES (1)" },
            { "change the blacklist", $"UPDATE {Site}.ihvph_blacklist SET notes = 'changed' WHERE 1 = 0" },
            { "delete from the blacklist", $"DELETE FROM {Site}.ihvph_blacklist WHERE 1 = 0" },
            { "add a server", $"INSERT INTO {Site}.ihvph_discord_servers (guild_id, armor_channel_id, map_channel_id, lobby_channel_id, announcements_channel_id) VALUES (1, 1, 1, 1, 1)" },
            { "change a server's channel", $"UPDATE {Site}.ihvph_discord_servers SET armor_channel_id = 1 WHERE 1 = 0" },
            { "change a server's ID", $"UPDATE {Site}.ihvph_discord_servers SET guild_id = guild_id WHERE 1 = 0" },
            { "delete a server", $"DELETE FROM {Site}.ihvph_discord_servers WHERE 1 = 0" },
            // The server itself
            { "create a database", "CREATE DATABASE rightscheck" },
            { "create a login", "CREATE USER 'rights-check-new'@'%'" },
            { "grant a right", $"GRANT SELECT ON {Bot}.dashboard_sessions TO {Grantee}" },
            { "write a file", "SELECT 1 INTO OUTFILE '/tmp/rights-check'" },
            { "lock a table", $"LOCK TABLES {Bot}.dashboard_sessions READ" },
            { "change a global setting", "SET GLOBAL max_connections = 151" },
            { "end another session", "KILL {root}" },
            { "read the logins", "SELECT * FROM mysql.user" },
            { "read the logins' rights", "SELECT * FROM mysql.global_priv" }
        };

        [Theory]
        [MemberData(nameof(Forbidden))]
        public async Task EverythingElseIsRefused(string what, string sql)
        {
            await WithLoginAsync(async (db, password) =>
            {
                await using var root = new MySqlConnection(db.RootConnection);

                if (sql.Contains("{root}", StringComparison.Ordinal))
                {
                    await root.OpenAsync(Token);
                    await using var id = new MySqlCommand("SELECT CONNECTION_ID()", root);
                    sql = sql.Replace("{root}", Convert.ToString(await id.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture));
                }

                try
                {
                    var refused = await Assert.ThrowsAsync<MySqlException>(() => RunAsLoginAsync(db, password, sql));
                    Assert.True(Refusals.Contains(refused.Number), $"The login could {what}: it failed with {refused.Number}, which isn't a refusal");
                }
                finally
                {
                    // Only if a refusal ever failed: nothing it made is left for the other tests
                    foreach (var database in new[] { Bot, Site })
                    {
                        await db.RootExecuteAsync($"DROP TABLE IF EXISTS {database}.rights_check");
                        await db.RootExecuteAsync($"DROP VIEW IF EXISTS {database}.rights_check");
                        await db.RootExecuteAsync($"DROP TRIGGER IF EXISTS {database}.rights_check");
                        await db.RootExecuteAsync($"DROP PROCEDURE IF EXISTS {database}.rights_check");
                    }

                    await db.RootExecuteAsync("DROP DATABASE IF EXISTS rightscheck");
                    await db.RootExecuteAsync("DROP USER IF EXISTS 'rights-check-new'@'%'");
                }
            });
        }

        // The repositories later pull requests use are refused, which records what PRs 4 and 5 must add to the file
        [Fact]
        public async Task RepositoriesNotUsedYetAreRefused()
        {
            await WithLoginAsync(async (db, password) =>
            {
                var connections = Connections(db, password);
                var factory = new SQLConnectionFactory(connections);
                var names = new DatabaseNames(connections);
                var review = new ArmorReview { armor_id = 1, from_status = ArmorStatus.Pending, to_status = ArmorStatus.Approved, reviewed_by = UserId, reviewed_at = Noon };

                var calls = new (string What, Func<Task> Call)[]
                {
                    ("AuditLogData.ListAudit", () => new AuditLogData(factory, names).ListAudit(new AuditQuery())),
                    ("ArmorReviewData.ListReviews", () => new ArmorReviewData(factory, names).ListReviews(1)),
                    ("ArmorReviewData.AddReview", () => new ArmorReviewData(factory, names).AddReview(review)),
                    ("ArmorSubmissionData.GetSubmission", () => new ArmorSubmissionData(factory).GetSubmission(1)),
                    ("ArmorSubmissionData.ListByStatus", () => new ArmorSubmissionData(factory).ListByStatus(ArmorStatus.Pending, 0, 10)),
                    ("ArmorSubmissionData.ChangeStatus", () => new ArmorSubmissionData(factory).ChangeStatus(1, ArmorStatus.Pending, ArmorStatus.Approved)),
                    ("BlacklistData.GetBlacklist", () => new BlacklistData(factory).GetBlacklist()),
                    ("BlacklistData.InsertPlayerBan", () => new BlacklistData(factory).InsertPlayerBan(new BlacklistModel { steam_id = 12345, username = "Test Player", reason = "Testing", notes = "Made up" })),
                    ("GuildData.GetAllGuilds", () => new GuildData(factory).GetAllGuilds()),
                    ("GuildData.SaveGuild", () => new GuildData(factory).SaveGuild(new Guild { guild_id = 1000000000000000005, armor_channel_id = 1 }))
                };

                foreach (var (what, call) in calls)
                {
                    var refused = await Assert.ThrowsAsync<MySqlException>(call);
                    Assert.True(Refusals.Contains(refused.Number), $"{what} failed with {refused.Number}, which isn't a refusal");
                }
            });
        }

        [Fact]
        public async Task ApplyingTwiceChangesNothing()
        {
            await WithLoginAsync(async (db, _) =>
            {
                var once = await RightsAsync(db);
                await ApplyAsync(db);

                Assert.Equal(once, await RightsAsync(db));
                Assert.Equal(Expected, once);
            });
        }

        // The file is declarative: a right added by hand, on a table or a whole database, and a changed limit, are gone
        // after the next apply
        [Fact]
        public async Task RightsAddedByHandAreTakenAway()
        {
            await WithLoginAsync(async (db, _) =>
            {
                await db.RootExecuteAsync($"GRANT SELECT ON {Bot}.armor_reviews TO {Grantee}");
                await db.RootExecuteAsync($"GRANT ALL PRIVILEGES ON {Bot}.* TO {Grantee}");
                await db.RootExecuteAsync($"GRANT SELECT (armor_channel_id) ON {Site}.ihvph_discord_servers TO {Grantee}");
                await db.RootExecuteAsync($"ALTER USER {Grantee} WITH MAX_USER_CONNECTIONS 100 MAX_STATEMENT_TIME 0");

                var added = await RightsAsync(db);
                Assert.Contains("table SELECT minerbot_db armor_reviews", added);
                Assert.Contains("schema SELECT minerbot_db", added);
                Assert.Contains("column SELECT website_db ihvph_discord_servers armor_channel_id", added);
                Assert.Contains("limits 100 0.000000 0 0 0", added);

                await ApplyAsync(db);

                Assert.Equal(Expected, await RightsAsync(db));
            });
        }

        // MariaDB refuses a right on a table that doesn't exist, so every table and column the file names must be there
        // after the migrations and the website's schema
        [Fact]
        public async Task EveryTableTheFileNamesExists()
        {
            var db = fixture.Require();
            var file = DeployFile("dashboard-rights.sql");
            var tables = TableNames().Matches(file).Select(match => (Database: Database(match.Groups[1].Value), Table: match.Groups[2].Value)).Distinct().ToList();

            Assert.Equal(3, tables.Count);

            foreach (var (database, table) in tables)
            {
                var found = await db.RootQueryAsync<long>("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = @database AND TABLE_NAME = @table", new { database, table });
                Assert.True(found.Single() == 1, $"{database}.{table} doesn't exist");
            }

            foreach (Match match in ColumnRights().Matches(file))
            {
                var (database, table) = (Database(match.Groups[2].Value), match.Groups[3].Value);

                foreach (var column in match.Groups[1].Value.Split(',').Select(name => name.Trim().Trim('`')))
                {
                    var found = await db.RootQueryAsync<long>("SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = @database AND TABLE_NAME = @table AND COLUMN_NAME = @column", new { database, table, column });
                    Assert.True(found.Single() == 1, $"{database}.{table}.{column} doesn't exist");
                }
            }
        }

        static string Database(string placeholder) => placeholder switch
        {
            "SITE_DB" => Site,
            "BOT_DB" => Bot,
            _ => throw new ArgumentOutOfRangeException(nameof(placeholder), placeholder, "Not a placeholder the file may use")
        };

        // Nothing but the three placeholders deploy.sh fills, no password, and only rights: no statement can create a
        // login, change a password or run anything else as root
        [Fact]
        public void TheFileHasOnlyKnownPlaceholders()
        {
            var file = DeployFile("dashboard-rights.sql");
            var statements = MigrationScript.Split(file);

            Assert.Equal(["{{BOT_DB}}", "{{GRANTEES}}", "{{SITE_DB}}"], Placeholders().Matches(file).Select(match => match.Value).Distinct().Order(StringComparer.Ordinal));
            Assert.All(statements, statement => Assert.Matches(@"^(REVOKE|ALTER USER|GRANT) ", statement));
            Assert.All(statements, statement => Assert.DoesNotContain("IDENTIFIED", statement, StringComparison.OrdinalIgnoreCase));
            Assert.All(statements, statement => Assert.DoesNotContain("PASSWORD", statement, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("{{", Rendered(file));

            // The listing names the login only through its placeholder, and never asks for SHOW GRANTS, which prints the
            // password's hash
            var list = DeployFile("rights-list.sql");
            Assert.Equal(["{{WHO}}"], Placeholders().Matches(list).Select(match => match.Value).Distinct());
            Assert.DoesNotContain("SHOW GRANTS", list, StringComparison.OrdinalIgnoreCase);
        }

        [GeneratedRegex(@"`\{\{(SITE_DB|BOT_DB)\}\}`\.`([A-Za-z0-9_]+)`")]
        private static partial Regex TableNames();

        [GeneratedRegex(@"\(([^)]*)\) ON `\{\{(SITE_DB|BOT_DB)\}\}`\.`([A-Za-z0-9_]+)`")]
        private static partial Regex ColumnRights();

        [GeneratedRegex(@"\{\{[^}]*\}\}")]
        private static partial Regex Placeholders();
    }
}
