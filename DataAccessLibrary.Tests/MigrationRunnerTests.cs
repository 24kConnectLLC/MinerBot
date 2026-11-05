using DataAccessLibrary.Migrations;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using System.Diagnostics;

namespace DataAccessLibrary.Tests
{
    // Each test migrates a database of its own, so none of them sees another's tables or lock
    [Collection("Database")]
    public class MigrationRunnerTests(MariaDbFixture fixture)
    {
        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(60);

        static readonly MigrationOptions ShortLockWait = new() { LockWait = TimeSpan.FromSeconds(1) };

        static Migration Test(int version, string sql) => new(version, $"{version:0000}_test", sql);

        static Task<MigrationResult> RunAsync(string connection, ListLogger logger, IReadOnlyList<Migration> migrations, MigrationOptions? options = null) =>
            MigrationRunner.RunAsync(connection, logger, migrations, options ?? new MigrationOptions(), TestContext.Current.CancellationToken);

        static Task<MigrationResult> MigrateAsync(string connection, ListLogger logger) =>
            DatabaseMigrations.MigrateAsync(connection, logger, TestContext.Current.CancellationToken);

        // Dapper fills a tuple by the columns' order
        Task<List<(int Version, string Name, string Checksum)>> RecordedAsync(string database) =>
            fixture.RootQueryAsync<(int Version, string Name, string Checksum)>(
                $"SELECT `version`, `name`, `checksum` FROM {DatabaseNames.QuoteIdentifier(database)}.schema_version ORDER BY `version`");

        async Task<List<string>> TablesAsync(string database) =>
            await fixture.RootQueryAsync<string>("SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = @database ORDER BY TABLE_NAME", new { database });

        async Task<bool> LockIsFreeAsync(string database) =>
            (await fixture.RootQueryAsync<long>("SELECT IS_FREE_LOCK(@name)", new { name = MigrationRunner.LockName(database) })).Single() == 1;

        [Fact]
        public async Task EmptyDatabaseGetsEveryMigration()
        {
            var db = fixture.Require();
            var (database, connection) = await db.CreateDatabaseAsync("migrate_empty");
            var logger = new ListLogger();
            var migrations = MigrationScript.Embedded();

            var result = await MigrateAsync(connection, logger);

            Assert.Equal(migrations.Select(migration => migration.Name), result.Applied);
            Assert.Equal(migrations[^1].Version, result.Version);
            Assert.Equal(migrations.Select(migration => (migration.Version, migration.Name, migration.Checksum)), await RecordedAsync(database));

            var tables = await db.RootQueryAsync<(string, string, string)>(
                "SELECT TABLE_NAME, ENGINE, TABLE_COLLATION FROM information_schema.TABLES WHERE TABLE_SCHEMA = @database ORDER BY TABLE_NAME", new { database });

            Assert.Equal(["armor_reviews", "audit_log", "dashboard_sessions", "schema_version"], tables.Select(table => table.Item1));
            Assert.All(tables, table =>
            {
                Assert.Equal("InnoDB", table.Item2);
                Assert.Equal("utf8mb4_unicode_520_ci", table.Item3);
            });

            Assert.True(logger.Logged(LogLevel.Information, "Applied migration 0001_core"));
        }

        [Fact]
        public async Task SecondRunChangesNothing()
        {
            var db = fixture.Require();
            var (database, connection) = await db.CreateDatabaseAsync("migrate_twice");
            await MigrateAsync(connection, new ListLogger());

            var quoted = DatabaseNames.QuoteIdentifier(database);
            var rowsBefore = await db.RootQueryAsync<(int, string, DateTime)>($"SELECT `version`, `checksum`, applied_at FROM {quoted}.schema_version ORDER BY `version`");
            var tablesBefore = await CreateStatementsAsync(quoted, await TablesAsync(database));

            var logger = new ListLogger();
            var result = await MigrateAsync(connection, logger);

            Assert.Empty(result.Applied);
            Assert.Equal(MigrationScript.Embedded()[^1].Version, result.Version);
            Assert.Equal(rowsBefore, await db.RootQueryAsync<(int, string, DateTime)>($"SELECT `version`, `checksum`, applied_at FROM {quoted}.schema_version ORDER BY `version`"));
            Assert.Equal(tablesBefore, await CreateStatementsAsync(quoted, await TablesAsync(database)));
            Assert.True(logger.Logged(LogLevel.Information, "The database is up to date at version 1"));
        }

        async Task<List<string>> CreateStatementsAsync(string quotedDatabase, List<string> tables)
        {
            var statements = new List<string>();

            foreach (var table in tables)
            {
                var row = await fixture.RootQueryAsync<(string, string)>($"SHOW CREATE TABLE {quotedDatabase}.{DatabaseNames.QuoteIdentifier(table)}");
                statements.Add(row.Single().Item2);
            }

            return statements;
        }

        [Fact]
        public async Task ChangedAppliedFileIsRefused()
        {
            var db = fixture.Require();
            var (database, connection) = await db.CreateDatabaseAsync("migrate_changed");
            await RunAsync(connection, new ListLogger(), [Test(1, "CREATE TABLE IF NOT EXISTS first_table (id INT NOT NULL);")]);

            var changed = Test(1, "CREATE TABLE IF NOT EXISTS first_table (id BIGINT NOT NULL);");
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RunAsync(connection, new ListLogger(), [changed, Test(2, "CREATE TABLE IF NOT EXISTS second_table (id INT NOT NULL);")]));

            Assert.Contains("0001_test", refused.Message);
            Assert.DoesNotContain("second_table", await TablesAsync(database));
            Assert.Equal([1], (await RecordedAsync(database)).Select(row => row.Version));
            Assert.True(await LockIsFreeAsync(database));
        }

        // A checksum that no longer matches the file is refused through the public entry point too
        [Fact]
        public async Task ChangedChecksumIsRefused()
        {
            var db = fixture.Require();
            var (database, connection) = await db.CreateDatabaseAsync("migrate_checksum");
            await MigrateAsync(connection, new ListLogger());

            await db.RootExecuteAsync($"UPDATE {DatabaseNames.QuoteIdentifier(database)}.schema_version SET `checksum` = REPEAT('0', 64) WHERE `version` = 1");

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => MigrateAsync(connection, new ListLogger()));
            Assert.Contains("0001_core", refused.Message);
        }

        // MariaDB commits each CREATE TABLE as it goes, so the first table stays, and the file has to run again from the top.
        // The log says which statements had run, because a fix to them would never reach the database.
        [Fact]
        public async Task FailedFileIsNotRecorded()
        {
            var db = fixture.Require();
            var (database, connection) = await db.CreateDatabaseAsync("migrate_failed");
            var logger = new ListLogger();

            await Assert.ThrowsAsync<MySqlException>(() => RunAsync(connection, logger,
                [Test(1, "CREATE TABLE IF NOT EXISTS good_table (id INT NOT NULL);\nTHIS IS NOT SQL;")]));

            Assert.Contains("good_table", await TablesAsync(database));
            Assert.Empty(await RecordedAsync(database));
            Assert.True(logger.Logged(LogLevel.Error, "Migration 0001_test stopped at statement 2 of 2, after the 1 before it had run"));
            Assert.True(logger.Logged(LogLevel.Error, "change only statement 2 and later ones"));

            var first = new ListLogger();
            await Assert.ThrowsAsync<MySqlException>(() => RunAsync(connection, first, [Test(2, "THIS IS NOT SQL;")]));
            Assert.True(first.Logged(LogLevel.Error, "Migration 0002_test stopped at its first statement, so nothing in it had run"));

            var corrected = Test(1, "CREATE TABLE IF NOT EXISTS good_table (id INT NOT NULL);\nCREATE TABLE IF NOT EXISTS other_table (id INT NOT NULL);");
            var result = await RunAsync(connection, new ListLogger(), [corrected]);

            Assert.Equal(["0001_test"], result.Applied);
            Assert.Equal([(1, "0001_test", corrected.Checksum)], await RecordedAsync(database));
            Assert.Contains("other_table", await TablesAsync(database));
        }

        // A runner that read the versions before it held the lock would apply a file twice and hit a duplicate key
        [Fact]
        public async Task TwoRunnersAtOnce()
        {
            var db = fixture.Require();
            var (database, connection) = await db.CreateDatabaseAsync("migrate_race");
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<MigrationResult> RunWhenReleased()
            {
                await start.Task;
                return await MigrateAsync(connection, new ListLogger());
            }

            var first = Task.Run(RunWhenReleased, TestContext.Current.CancellationToken);
            var second = Task.Run(RunWhenReleased, TestContext.Current.CancellationToken);
            start.SetResult();

            var results = await Task.WhenAll(first, second).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            var migrations = MigrationScript.Embedded();
            Assert.Equal(migrations.Select(migration => migration.Version), (await RecordedAsync(database)).Select(row => row.Version));
            Assert.Equal(migrations.Count, results.Sum(result => result.Applied.Count));
            Assert.All(results, result => Assert.Equal(migrations[^1].Version, result.Version));
        }

        [Fact]
        public async Task HeldLockMakesARunnerWait()
        {
            var db = fixture.Require();
            var (database, connection) = await db.CreateDatabaseAsync("migrate_locked");

            await using var holder = new MySqlConnection(db.RootConnection);
            await holder.OpenAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(1, await ScalarAsync(holder, "SELECT GET_LOCK(@name, 0)", MigrationRunner.LockName(database)));

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(connection, new ListLogger(), MigrationScript.Embedded(), ShortLockWait));
            Assert.StartsWith("Another migration run holds the lock", refused.Message);
            Assert.Empty(await TablesAsync(database));

            var waiting = MigrateAsync(connection, new ListLogger());
            await Task.Delay(500, TestContext.Current.CancellationToken);
            Assert.False(waiting.IsCompleted);

            await ScalarAsync(holder, "SELECT RELEASE_LOCK(@name)", MigrationRunner.LockName(database));

            var result = await waiting.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            Assert.Equal(MigrationScript.Embedded().Select(migration => migration.Name), result.Applied);
        }

        static async Task<long> ScalarAsync(MySqlConnection connection, string sql, string name)
        {
            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@name", name);

            return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).WaitAsync(WaitLimit, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task LockIsReleasedAfterARun()
        {
            var db = fixture.Require();
            var (database, connection) = await db.CreateDatabaseAsync("migrate_release");

            await RunAsync(connection, new ListLogger(), [Test(1, "CREATE TABLE IF NOT EXISTS first_table (id INT NOT NULL);")]);
            Assert.True(await LockIsFreeAsync(database));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RunAsync(connection, new ListLogger(), [Test(1, "CREATE TABLE IF NOT EXISTS first_table (id BIGINT NOT NULL);")]));
            Assert.True(await LockIsFreeAsync(database));
        }

        // Its own limit, not the 15 seconds of an ordinary call
        [Fact]
        public async Task RunnerHasItsOwnLimit()
        {
            var db = fixture.Require();
            var (_, connection) = await db.CreateDatabaseAsync("migrate_slow");
            var watch = Stopwatch.StartNew();

            var timeout = await Assert.ThrowsAsync<TimeoutException>(() =>
                RunAsync(connection, new ListLogger(), [Test(1, "DO SLEEP(5);")], new MigrationOptions { RunLimit = TimeSpan.FromSeconds(1) }));

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"The limit fired after {watch.Elapsed}");
            Assert.StartsWith("The migrations didn't finish within 1 seconds", timeout.Message);
            Assert.True(new MigrationOptions().RunLimit > TimeSpan.FromSeconds(15));
        }

        // MySql.Data doesn't end an async statement at its CommandTimeout, so the server has to. An ALTER behind another
        // session's open transaction would otherwise wait for a day after the runner gave up, with every new query on that
        // table queued behind it.
        [Fact]
        public async Task StatementWaitingForALockIsEndedByTheServer()
        {
            var db = fixture.Require();
            var (database, connection) = await db.CreateDatabaseAsync("migrate_lockwait");
            var create = Test(1, "CREATE TABLE IF NOT EXISTS busy_table (id INT NOT NULL);");
            await RunAsync(connection, new ListLogger(), [create]);

            // A transaction that has read the table keeps its metadata lock until it ends
            await using var holder = new MySqlConnection(db.RootConnection);
            await holder.OpenAsync(TestContext.Current.CancellationToken).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            await RawAsync(holder, "START TRANSACTION");
            await RawAsync(holder, $"SELECT COUNT(*) FROM {DatabaseNames.QuoteIdentifier(database)}.busy_table");

            var options = new MigrationOptions { StatementLimit = TimeSpan.FromSeconds(2), RunLimit = TimeSpan.FromSeconds(30) };
            var watch = Stopwatch.StartNew();

            var stopped = await Assert.ThrowsAsync<MySqlException>(() => RunAsync(connection, new ListLogger(),
                [create, Test(2, "ALTER TABLE busy_table ADD COLUMN IF NOT EXISTS note INT NULL;")], options));

            Assert.Equal((int)MySqlErrorCode.LockWaitTimeout, stopped.Number);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"The server ended the statement after {watch.Elapsed}");
            Assert.Empty(await db.RootQueryAsync<long>("SELECT ID FROM information_schema.PROCESSLIST WHERE INFO LIKE 'ALTER TABLE busy_table%'"));
            Assert.Equal([1], (await RecordedAsync(database)).Select(row => row.Version));

            await RawAsync(holder, "COMMIT");
        }

        // The statement limit only ends lock waits. MySql.Data counts an answer that comes after its own timeout as a
        // timeout and then waits for a reply that never comes, so a statement that runs longer than the limit is still
        // waited for, applied and recorded.
        [Fact]
        public async Task SlowStatementIsWaitedFor()
        {
            var db = fixture.Require();
            var (database, connection) = await db.CreateDatabaseAsync("migrate_slowstatement");
            var options = new MigrationOptions { StatementLimit = TimeSpan.FromSeconds(1), RunLimit = TimeSpan.FromSeconds(20) };

            var result = await RunAsync(connection, new ListLogger(), [Test(1, "DO SLEEP(2);")], options);

            Assert.Equal(["0001_test"], result.Applied);
            Assert.Equal([1], (await RecordedAsync(database)).Select(row => row.Version));
        }

        // A statement keeps running when the run is given up on, so the runner ends its session on the server, which stops
        // the statement and lets go of the lock now rather than when the statement would have ended
        [Fact]
        public async Task GivenUpRunLetsGoOfTheLock()
        {
            var db = fixture.Require();
            var (database, connection) = await db.CreateDatabaseAsync("migrate_givenup");

            await Assert.ThrowsAsync<TimeoutException>(() =>
                RunAsync(connection, new ListLogger(), [Test(1, "DO SLEEP(30);")], new MigrationOptions { RunLimit = TimeSpan.FromSeconds(1) }));

            // The server ends a killed session a moment after the kill
            var waiting = Stopwatch.StartNew();
            while (!await LockIsFreeAsync(database) && waiting.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(100, TestContext.Current.CancellationToken);

            Assert.True(await LockIsFreeAsync(database), $"The lock was still held {waiting.Elapsed} after the run was given up on");
            Assert.Empty(await db.RootQueryAsync<long>("SELECT ID FROM information_schema.PROCESSLIST WHERE INFO LIKE 'DO SLEEP(30)%'"));
            Assert.Empty(await RecordedAsync(database));
        }

        // A file that breaks the one-statement-per-line rule fails on its first run, here, instead of running as one batch
        // whose second statement no check looked at
        [Theory]
        [InlineData("CREATE TABLE IF NOT EXISTS first_table (id INT NOT NULL); CREATE TABLE IF NOT EXISTS second_table (id INT NOT NULL);")]
        [InlineData("CREATE TABLE IF NOT EXISTS first_table (id INT NOT NULL); -- the first table\nCREATE TABLE IF NOT EXISTS second_table (id INT NOT NULL);")]
        public async Task TwoStatementsInOneCommandAreRefused(string sql)
        {
            var db = fixture.Require();
            var (database, connection) = await db.CreateDatabaseAsync("migrate_batch");

            await Assert.ThrowsAsync<MySqlException>(() => RunAsync(connection, new ListLogger(), [Test(1, sql)]));

            Assert.Equal(["schema_version"], await TablesAsync(database));
            Assert.Empty(await RecordedAsync(database));
        }

        // The live server's default character set is latin1, so a connection string naming none would send a migration's
        // emoji as question marks, and the recorded file would never run again to put them right
        [Fact]
        public async Task MigrationTextKeepsEveryCharacter()
        {
            const string Seed = "Test Miner \U0001F600​";

            var db = fixture.Require();
            var (database, _) = await db.CreateDatabaseAsync("migrate_charset");

            await RunAsync(db.LoginConnectionWithoutCharacterSet(database), new ListLogger(),
            [
                Test(1, $"CREATE TABLE IF NOT EXISTS seeds (name VARCHAR(30) NOT NULL) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;\nINSERT IGNORE INTO seeds (name) VALUES ('{Seed}');")
            ]);

            Assert.Equal([Seed], await db.RootQueryAsync<string>($"SELECT name FROM {DatabaseNames.QuoteIdentifier(database)}.seeds"));
        }

        static async Task RawAsync(MySqlConnection connection, string sql)
        {
            await using var command = new MySqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
        }

        // A build older than the database, as after a rollback, applies nothing and keeps working
        [Fact]
        public async Task NewerDatabaseIsLeftAlone()
        {
            var db = fixture.Require();
            var (database, connection) = await db.CreateDatabaseAsync("migrate_newer");
            await RunAsync(connection, new ListLogger(),
                [Test(1, "CREATE TABLE IF NOT EXISTS first_table (id INT NOT NULL);"), Test(2, "CREATE TABLE IF NOT EXISTS second_table (id INT NOT NULL);")]);

            var logger = new ListLogger();
            var result = await RunAsync(connection, logger, [Test(1, "CREATE TABLE IF NOT EXISTS first_table (id INT NOT NULL);")]);

            Assert.Empty(result.Applied);
            Assert.Equal(2, result.Version);
            Assert.True(logger.Logged(LogLevel.Warning, "newer than this build's 1"));
        }

        // A file that never ran can't slip in under one that did
        [Fact]
        public async Task SkippedVersionIsRefused()
        {
            var db = fixture.Require();
            var (_, connection) = await db.CreateDatabaseAsync("migrate_gap");
            await RunAsync(connection, new ListLogger(), [Test(2, "CREATE TABLE IF NOT EXISTS second_table (id INT NOT NULL);")]);

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(connection, new ListLogger(),
                [Test(1, "CREATE TABLE IF NOT EXISTS first_table (id INT NOT NULL);"), Test(2, "CREATE TABLE IF NOT EXISTS second_table (id INT NOT NULL);")]));

            Assert.Contains("0001_test never ran", refused.Message);
        }

        [Fact]
        public async Task ConnectionWithoutADatabaseIsRefused()
        {
            var db = fixture.Require();
            var connection = new MySqlConnectionStringBuilder(db.BotConnection) { Database = "" }.ConnectionString;

            await Assert.ThrowsAsync<ArgumentException>(() => MigrateAsync(connection, new ListLogger()));
        }
    }
}
