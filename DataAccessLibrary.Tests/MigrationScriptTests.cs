using DataAccessLibrary.Migrations;
using System.Text.RegularExpressions;

namespace DataAccessLibrary.Tests
{
    // How migration files are read and split. No database is needed.
    public class MigrationScriptTests
    {
        static string WebsiteSchema() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schema", "wordpress-tables.sql"));

        [Fact]
        public void SemicolonAtTheEndOfALineEndsAStatement()
        {
            var statements = MigrationScript.Split("""
                -- A comment that ends in a semicolon;
                CREATE TABLE IF NOT EXISTS a (
                  -- Another one;
                  note VARCHAR(10) NOT NULL DEFAULT 'x;y',
                  id INT NOT NULL
                );

                INSERT IGNORE INTO a (id) VALUES (1);
                """);

            Assert.Equal(
                ["CREATE TABLE IF NOT EXISTS a (\n  note VARCHAR(10) NOT NULL DEFAULT 'x;y',\n  id INT NOT NULL\n)", "INSERT IGNORE INTO a (id) VALUES (1)"],
                statements);
        }

        [Fact]
        public void WindowsLineEndsSplitTheSame()
        {
            const string sql = "CREATE TABLE IF NOT EXISTS a (\n  id INT NOT NULL\n);\n\nCREATE TABLE IF NOT EXISTS b (id INT);\n";

            Assert.Equal(MigrationScript.Split(sql), MigrationScript.Split(sql.Replace("\n", "\r\n")));
            Assert.Equal(2, MigrationScript.Split(sql.Replace("\n", "\r\n")).Count);
        }

        [Theory]
        [InlineData("CREATE TABLE IF NOT EXISTS a (id INT)")]
        [InlineData("CREATE TABLE IF NOT EXISTS a (id INT);\nCREATE TABLE IF NOT EXISTS b (id INT)")]
        [InlineData("CREATE TABLE IF NOT EXISTS a (id INT); -- a comment after it")]
        [InlineData("")]
        [InlineData("-- Only a comment;\n\n")]
        [InlineData(";")]
        public void SqlThatDoesNotEndRightIsRefused(string sql)
        {
            Assert.Throws<FormatException>(() => MigrationScript.Split(sql));
        }

        // Taken over the text, so a Windows checkout and the Linux image agree
        [Fact]
        public void ChecksumIgnoresLineEndsAndAByteOrderMark()
        {
            const string sql = "CREATE TABLE IF NOT EXISTS a (\n  id INT NOT NULL\n);\n";
            var checksum = MigrationScript.Checksum(sql);

            Assert.Matches("^[0-9a-f]{64}$", checksum);
            Assert.Equal(checksum, MigrationScript.Checksum(sql.Replace("\n", "\r\n")));
            Assert.Equal(checksum, MigrationScript.Checksum((char)0xFEFF + sql));
            Assert.NotEqual(checksum, MigrationScript.Checksum(sql.Replace("INT", "INU")));
            Assert.NotEqual(checksum, MigrationScript.Checksum(sql + " "));
        }

        // The runner applies everything newer than the database's newest, so a gap or a repeat would skip a file
        [Fact]
        public void EmbeddedMigrationsRunFromOneWithoutGaps()
        {
            var migrations = MigrationScript.Embedded();

            Assert.NotEmpty(migrations);
            Assert.Equal(Enumerable.Range(1, migrations.Count), migrations.Select(migration => migration.Version));
            Assert.All(migrations, migration => Assert.Matches($"^{migration.Version:0000}_[a-z0-9_]+$", migration.Name));
        }

        // Every file a database may have run, with its checksum. A database that ran a file refuses it once it changes,
        // even in a comment, and that would only show when a deploy stopped, so here it fails the build. A new migration
        // joins the list in the pull request that adds it. Change a checksum here only for a file that has never run
        // anywhere, or for one that stopped partway and is fixed from the statement that stopped onwards.
        static readonly Dictionary<string, string> Checksums = new()
        {
            ["0001_core"] = "71cf66af93eaa830b8ceebda61d5341971cbc4c82fbf37107149334574454aed"
        };

        [Fact]
        public void MigrationsNeverChangeOnceWritten()
        {
            var migrations = MigrationScript.Embedded();

            Assert.All(migrations, migration =>
            {
                Assert.True(Checksums.TryGetValue(migration.Name, out var checksum), $"Add {migration.Name} to the list of checksums: {migration.Checksum}");
                Assert.True(checksum == migration.Checksum,
                    $"{migration.Name} changed, and a database that ran it would refuse it. Put it back as it was and add a new migration. Its checksum is now {migration.Checksum}");
            });

            Assert.Equal(Checksums.Keys.Order(), migrations.Select(migration => migration.Name).Order());
        }

        // The project builds in every file of the Migrations folder, so one named the wrong way is refused rather than
        // shipped and never run
        [Theory]
        [InlineData("Migrations/0002_Bot_Status.sql")]
        [InlineData("Migrations/0002-bot-status.sql")]
        [InlineData("Migrations/002_bot_status.sql")]
        [InlineData("Migrations/0002_bot_status.SQL")]
        [InlineData("Migrations/0002_bot_status.sql.txt")]
        [InlineData("Migrations/README.md")]
        public void MisnamedMigrationIsRefused(string resource)
        {
            var refused = Assert.Throws<InvalidOperationException>(() => MigrationScript.Load(
                ["Migrations/0001_core.sql", resource], _ => "CREATE TABLE IF NOT EXISTS a (id INT);"));

            Assert.StartsWith(resource, refused.Message);
        }

        [Fact]
        public void OnlyTheMigrationsFolderIsRead()
        {
            var migrations = MigrationScript.Load(
                ["Migrations/0002_second.sql", "DataAccessLibrary.Strings.resources", "Other/0003_third.sql", "Migrations/0001_first.sql"],
                resource => $"-- {resource}\nCREATE TABLE IF NOT EXISTS a (id INT);");

            Assert.Equal(["0001_first", "0002_second"], migrations.Select(migration => migration.Name));
            Assert.Equal("-- Migrations/0001_first.sql\nCREATE TABLE IF NOT EXISTS a (id INT);", migrations[0].Sql);
        }

        // Every file in the folder was built in and read as a migration
        [Fact]
        public void EveryEmbeddedFileIsAMigration()
        {
            var resources = typeof(MigrationScript).Assembly.GetManifestResourceNames().Where(resource => resource.StartsWith("Migrations/", StringComparison.Ordinal));

            Assert.Equal(resources.Order(), MigrationScript.Embedded().Select(migration => $"Migrations/{migration.Name}.sql").Order());
        }

        // MariaDB commits each statement that changes a table as it goes, so a file that stopped halfway runs again from the top
        [Fact]
        public void EveryEmbeddedStatementIsSafeToRunAgain()
        {
            var safe = new Regex(@"^(CREATE TABLE IF NOT EXISTS|CREATE INDEX IF NOT EXISTS|ALTER TABLE \S+ ADD COLUMN IF NOT EXISTS|ALTER TABLE \S+ ADD INDEX IF NOT EXISTS|INSERT IGNORE)\s");

            Assert.All(MigrationScript.Embedded().SelectMany(migration => migration.Statements), statement =>
                Assert.True(safe.IsMatch(statement), $"This statement isn't safe to run again: {statement[..Math.Min(80, statement.Length)]}"));
        }

        // The other tables of the plan come with the pull request that first uses them
        [Fact]
        public void FirstMigrationCreatesOnlyThePlannedTables()
        {
            var first = MigrationScript.Embedded()[0];

            Assert.Equal("0001_core", first.Name);
            Assert.Equal(["audit_log", "dashboard_sessions", "armor_reviews"], first.Statements.Select(TableName));
            Assert.All(first.Statements, statement =>
            {
                Assert.EndsWith("ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci", statement);
                Assert.DoesNotContain("TIMESTAMP", statement);
                Assert.DoesNotContain("CHAR(36)", statement);
            });
        }

        static string TableName(string statement) => Regex.Match(statement, @"^CREATE TABLE IF NOT EXISTS (\w+)").Groups[1].Value;

        // The database tests build the website's tables from this file
        [Fact]
        public void WebsiteSchemaFileSplitsIntoItsThreeTables()
        {
            var statements = MigrationScript.Split(WebsiteSchema());

            Assert.Equal(["ihvph_armor_styles", "ihvph_blacklist", "ihvph_discord_servers"],
                statements.Select(statement => Regex.Match(statement, @"^CREATE TABLE `(\w+)`").Groups[1].Value));
        }

        [Fact]
        public void LockNameFitsWhateverTheDatabase()
        {
            Assert.Equal("minerbot_migrations.minerbot_db", MigrationRunner.LockName("minerbot_db"));

            var first = MigrationRunner.LockName(new string('a', 64));
            var second = MigrationRunner.LockName(new string('a', 63) + "b");

            Assert.Equal(64, first.Length);
            Assert.StartsWith("minerbot_migrations.", first);
            Assert.NotEqual(first, second);
        }
    }
}
