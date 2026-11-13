using System.Diagnostics;
using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Blacklist.Models;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Dashboard.Models;
using DataAccessLibrary.Tests.Database;
using MinerBot.Shared.Armor;
using MySql.Data.MySqlClient;

namespace DataAccessLibrary.Tests
{
    // A change to a website table, with its review and audit rows in MinerBot's database, all on the website's connection
    [Collection("Database")]
    public class TransactionTests(MariaDbFixture fixture)
    {
        const long ArmorId = 501;

        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

        async Task<MariaDbFixture> StartAsync()
        {
            var db = fixture.Require();

            await db.ClearAsync(MariaDbFixture.ArmorTable, MariaDbFixture.BlacklistTable, MariaDbFixture.ReviewTable, MariaDbFixture.AuditTable);
            await db.AddArmorAsync(new ArmorSubmission
            {
                ID = ArmorId,
                CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5),
                Status = ArmorStatus.Pending,
                Author = "Test Author",
                ArmorName = "Test Armor 5"
            });

            return db;
        }

        static ArmorReview Review() => new()
        {
            armor_id = ArmorId,
            from_status = ArmorStatus.Pending,
            to_status = ArmorStatus.Approved,
            reason = "Looks good",
            reviewed_by = 1000000000000000001,
            reviewer_name = "Test Admin",
            reviewed_at = DateTime.UtcNow
        };

        static AuditEntry Audit() => new()
        {
            at = DateTime.UtcNow,
            actor_id = 1000000000000000001,
            actor_name = "Test Admin",
            actor_role = AuditRole.Admin,
            action = "armor.approve",
            outcome = AuditOutcome.Ok,
            target_type = "armor",
            target_id = ArmorId.ToString()
        };

        static BlacklistModel Player() => new() { steam_id = 12345, username = "Test Player", reason = "Testing", notes = "Made up" };

        async Task<string?> StatusAsync() =>
            (await fixture.RootQueryAsync<string?>($"SELECT Status FROM {MariaDbFixture.ArmorTable} WHERE ID = @ArmorId", new { ArmorId })).Single();

        async Task<long> CountAsync(string table) => (await fixture.RootQueryAsync<long>($"SELECT COUNT(*) FROM {table}")).Single();

        // The test's own session, for holding and waiting for row locks
        static async Task<object?> RawAsync(MySqlConnection connection, string sql)
        {
            await using var command = new MySqlCommand(sql, connection);
            return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task CommitsTogether()
        {
            var db = await StartAsync();
            var armor = db.Submissions();

            var applied = await armor.RunInTransaction(async transaction =>
            {
                var changed = await armor.ChangeStatus(ArmorId, ArmorStatus.Pending, ArmorStatus.Approved, transaction);
                await db.Reviews().AddReview(Review(), transaction);
                await db.Audit().AddAudit(Audit(), transaction);
                return changed;
            });

            Assert.True(applied);
            Assert.Equal(ArmorStatus.Approved, await StatusAsync());
            Assert.Equal(1, await CountAsync(MariaDbFixture.ReviewTable));
            Assert.Equal(1, await CountAsync(MariaDbFixture.AuditTable));
        }

        [Fact]
        public async Task RollsBackWhenWorkThrows()
        {
            var db = await StartAsync();
            var armor = db.Submissions();

            await Assert.ThrowsAsync<InvalidOperationException>(() => armor.RunInTransaction<bool>(async transaction =>
            {
                await armor.ChangeStatus(ArmorId, ArmorStatus.Pending, ArmorStatus.Approved, transaction);
                await db.Reviews().AddReview(Review(), transaction);
                throw new InvalidOperationException("The work stopped halfway");
            }));

            Assert.Equal(ArmorStatus.Pending, await StatusAsync());
            Assert.Equal(0, await CountAsync(MariaDbFixture.ReviewTable));
        }

        // Fails if the review or audit write used a connection of its own, which would commit on its own
        [Fact]
        public async Task RollsBackWhenAStatementFails()
        {
            var db = await StartAsync();
            var armor = db.Submissions();
            await db.Blacklist().InsertPlayerBan(Player());

            var duplicate = await Assert.ThrowsAsync<MySqlException>(() => armor.RunInTransaction(async transaction =>
            {
                await armor.ChangeStatus(ArmorId, ArmorStatus.Pending, ArmorStatus.Approved, transaction);
                await db.Reviews().AddReview(Review(), transaction);
                await db.Audit().AddAudit(Audit(), transaction);
                await db.Blacklist().InsertPlayerBan(Player(), transaction);
                return true;
            }));

            Assert.Equal((int)MySqlErrorCode.DuplicateKeyEntry, duplicate.Number);
            Assert.Equal(ArmorStatus.Pending, await StatusAsync());
            Assert.Equal(0, await CountAsync(MariaDbFixture.ReviewTable));
            Assert.Equal(0, await CountAsync(MariaDbFixture.AuditTable));
            Assert.Equal(1, await CountAsync(MariaDbFixture.BlacklistTable));
        }

        // An approval as the review page runs it, name check and locked read included, is all or nothing: a statement that
        // fails after the status, review and audit writes undoes them all. Fails if any of them used a connection of its own.
        [Fact]
        public async Task ReviewWithItsLockedReadIsAllOrNothing()
        {
            var db = await StartAsync();
            var armor = db.Submissions();
            await db.Blacklist().InsertPlayerBan(Player());

            var duplicate = await Assert.ThrowsAsync<MySqlException>(() => armor.RunInTransaction(async transaction =>
            {
                Assert.False(await armor.IsApprovedNameTaken("Test Armor 5", ArmorId, transaction));
                Assert.Equal(ArmorStatus.Pending, (await armor.GetSubmission(ArmorId, transaction))?.Status);
                Assert.True(await armor.ChangeStatus(ArmorId, ArmorStatus.Pending, ArmorStatus.Approved, transaction));
                await db.Reviews().AddReview(Review(), transaction);
                await db.Audit().AddAudit(Audit(), transaction);

                // Inside the transaction, its own changes show
                Assert.Equal(ArmorStatus.Approved, (await armor.GetSubmission(ArmorId, transaction))?.Status);

                await db.Blacklist().InsertPlayerBan(Player(), transaction);
                return true;
            }));

            Assert.Equal((int)MySqlErrorCode.DuplicateKeyEntry, duplicate.Number);
            Assert.Equal(ArmorStatus.Pending, await StatusAsync());
            Assert.Equal(0, await CountAsync(MariaDbFixture.ReviewTable));
            Assert.Equal(0, await CountAsync(MariaDbFixture.AuditTable));

            // And the same work without the failing statement commits all of it
            Assert.True(await armor.RunInTransaction(async transaction =>
            {
                Assert.False(await armor.IsApprovedNameTaken("Test Armor 5", ArmorId, transaction));
                Assert.NotNull(await armor.GetSubmission(ArmorId, transaction));
                Assert.True(await armor.ChangeStatus(ArmorId, ArmorStatus.Pending, ArmorStatus.Approved, transaction));
                await db.Reviews().AddReview(Review(), transaction);
                await db.Audit().AddAudit(Audit(), transaction);
                return true;
            }));

            Assert.Equal(ArmorStatus.Approved, await StatusAsync());
            Assert.Equal(1, await CountAsync(MariaDbFixture.ReviewTable));
            Assert.Equal(1, await CountAsync(MariaDbFixture.AuditTable));
        }

        // Another install, or a test, can name MinerBot's database anything, even something that needs quoting. Fails if a
        // repository names the real database in its SQL instead of taking the name from the connection string.
        [Fact]
        public async Task NamesComeFromTheConnectionString()
        {
            var db = await StartAsync();
            await db.ClearAsync(MariaDbFixture.SessionTable);
            const string alternative = "minerbot-alt`db";
            const string quoted = "`minerbot-alt``db`";

            await db.CreateDatabaseNamedAsync(alternative);
            await DatabaseMigrations.MigrateAsync(db.LoginConnection(alternative), new ListLogger(), TestContext.Current.CancellationToken);

            var connections = new Dictionary<DBConnectionName, string>(db.Connections) { [DBConnectionName.Bot] = db.LoginConnection(alternative) };
            var factory = new SQLConnectionFactory(connections);
            var names = new DatabaseNames(connections);
            var audit = new AuditLogData(factory, names);
            var reviews = new ArmorReviewData(factory, names);
            var sessions = new DashboardSessionData(factory, names);
            var armor = db.Submissions();

            Assert.Equal(quoted, names.Bot);

            await armor.RunInTransaction(async transaction =>
            {
                await armor.ChangeStatus(ArmorId, ArmorStatus.Pending, ArmorStatus.Approved, transaction);
                await reviews.AddReview(Review(), transaction);
                await audit.AddAudit(Audit(), transaction);
                return true;
            });

            // And on the bot's own connection, outside a transaction
            await audit.AddAudit(Audit());
            await sessions.CreateSession(SessionKey, Session());
            Assert.True(await sessions.TouchSession(SessionKey, Noon.AddMinutes(1)));

            Assert.Equal(2, await CountAsync($"{quoted}.audit_log"));
            Assert.Equal(1, await CountAsync($"{quoted}.armor_reviews"));
            Assert.Equal(1, await CountAsync($"{quoted}.dashboard_sessions"));

            Assert.Equal(2, (await audit.ListAudit(new AuditQuery())).Total);
            Assert.Single(await reviews.ListReviews(ArmorId));
            Assert.NotNull(await sessions.GetSession(SessionKey, Noon));
            Assert.Equal(1, await sessions.DeleteOtherSessions(Session().user_id, new string('o', 43)));

            // Nothing reached the real database's tables
            foreach (var table in new[] { MariaDbFixture.AuditTable, MariaDbFixture.ReviewTable, MariaDbFixture.SessionTable })
                Assert.Equal(0, await CountAsync(table));
        }

        static readonly DateTime Noon = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

        static readonly string SessionKey = new('k', 43);

        static DashboardSession Session() => new()
        {
            user_id = 1000000000000000001,
            user_name = "Test Admin",
            created_at = Noon,
            last_seen_at = Noon,
            expires_at = Noon.AddHours(12),
            ticket = [1, 2, 3]
        };

        // A transaction has the same limit as any call, and one that went past it never commits
        [Fact]
        public async Task KeepsTheCallLimit()
        {
            var db = await StartAsync();
            var armor = db.Submissions();
            armor.CallLimit = TimeSpan.FromMilliseconds(500);
            var watch = Stopwatch.StartNew();

            await Assert.ThrowsAsync<TimeoutException>(() => armor.RunInTransaction(async transaction =>
            {
                await armor.ChangeStatus(ArmorId, ArmorStatus.Pending, ArmorStatus.Approved, transaction);
                await db.Reviews().AddReview(Review(), transaction);
                await db.Audit().AddAudit(Audit(), transaction);
                await transaction.Execute("DO SLEEP(3)", null);
                return true;
            }));

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2.5), $"The limit fired after {watch.Elapsed}");

            // The given-up transaction holds the row until it ends, so this waits for that before reading
            await using var check = new MySqlConnection(db.RootConnection);
            await check.OpenAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await RawAsync(check, "SET SESSION innodb_lock_wait_timeout = 10");
            await RawAsync(check, "START TRANSACTION");
            var status = await RawAsync(check, $"SELECT Status FROM {MariaDbFixture.ArmorTable} WHERE ID = {ArmorId} FOR UPDATE");
            await RawAsync(check, "COMMIT");

            Assert.Equal(ArmorStatus.Pending, status);
            Assert.Equal(0, await CountAsync(MariaDbFixture.ReviewTable));
            Assert.Equal(0, await CountAsync(MariaDbFixture.AuditTable));
        }

        // A transaction that keeps losing deadlocks runs three times in all, and then the caller hears of it. InnoDB rolls
        // back each attempt, so none of them lands.
        [Fact]
        public async Task DeadlockedTransactionRunsThreeTimesAtMost()
        {
            var db = await StartAsync();
            var armor = db.Submissions();
            var runs = 0;

            var deadlock = await Assert.ThrowsAsync<MySqlException>(() => armor.RunInTransaction<bool>(async transaction =>
            {
                runs++;
                await armor.ChangeStatus(ArmorId, ArmorStatus.Pending, ArmorStatus.Approved, transaction);
                throw MySqlErrors.Deadlock();
            }));

            Assert.Equal((int)MySqlErrorCode.LockDeadlock, deadlock.Number);
            Assert.Equal(3, runs);
            Assert.Equal(ArmorStatus.Pending, await StatusAsync());
        }

        // Only a deadlock means the whole transaction was undone and can simply start again
        [Fact]
        public async Task OtherFailuresDontRunTheTransactionAgain()
        {
            var db = await StartAsync();
            var armor = db.Submissions();
            var runs = 0;

            await Assert.ThrowsAsync<MySqlException>(() => armor.RunInTransaction(async transaction =>
            {
                runs++;
                await transaction.Execute($"INSERT INTO {MariaDbFixture.ArmorTable} (ID, Author) VALUES ({ArmorId}, 'Test Author')", null);
                return true;
            }));

            Assert.Equal(1, runs);
        }

        // MySql.Data doesn't stop a statement that's running when the caller gives up, so the server limits each statement in
        // a transaction. One stuck behind a table lock, here a backup's or a migration's on MinerBot's audit table, fails
        // there and rolls back, letting go of the locks the name check took on the whole armor table. So the website can
        // still save a new armor while the audit table is still locked.
        [Fact]
        public async Task StuckTransactionLetsGoOfItsLocks()
        {
            var db = await StartAsync();
            var armor = db.Submissions();
            armor.CallLimit = TimeSpan.FromSeconds(2);

            await using var holder = new MySqlConnection(db.RootConnection);
            await holder.OpenAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await RawAsync(holder, $"LOCK TABLES {MariaDbFixture.AuditTable} WRITE");

            try
            {
                await Assert.ThrowsAnyAsync<Exception>(() => armor.RunInTransaction(async transaction =>
                {
                    await armor.IsApprovedNameTaken("Test Armor 5", ArmorId, transaction);
                    await armor.ChangeStatus(ArmorId, ArmorStatus.Pending, ArmorStatus.Approved, transaction);
                    await db.Audit().AddAudit(Audit(), transaction);
                    return true;
                }));

                // An armor after the last one goes in the gap the name check locked
                await using var website = new MySqlConnection(db.RootConnection);
                await website.OpenAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                await RawAsync(website, "SET SESSION innodb_lock_wait_timeout = 5");
                await RawAsync(website, $"INSERT INTO {MariaDbFixture.ArmorTable} (ID, Status, Author, ArmorName) VALUES ({ArmorId + 1}, 'Pending Approval', 'Test Author', 'Test Armor 6')");
            }
            finally
            {
                await RawAsync(holder, "UNLOCK TABLES");
            }

            Assert.Equal(ArmorStatus.Pending, await StatusAsync());
            Assert.Equal(0, await CountAsync(MariaDbFixture.AuditTable));
        }

        // A write outside a transaction that timed out keeps going, which is why the dashboard says it may or may not
        // have saved
        [Fact]
        public async Task PlainWriteThatTimedOutMayStillLand()
        {
            var db = await StartAsync();
            var armor = db.Submissions();
            armor.CallLimit = TimeSpan.FromMilliseconds(500);

            await using var holder = new MySqlConnection(db.RootConnection);
            await holder.OpenAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await RawAsync(holder, "START TRANSACTION");
            await RawAsync(holder, $"SELECT ID FROM {MariaDbFixture.ArmorTable} WHERE ID = {ArmorId} FOR UPDATE");

            await Assert.ThrowsAsync<TimeoutException>(() => armor.ChangeStatus(ArmorId, ArmorStatus.Pending, ArmorStatus.Approved));
            Assert.Equal(ArmorStatus.Pending, await StatusAsync());

            await RawAsync(holder, "COMMIT");

            var deadline = Stopwatch.StartNew();
            while (await StatusAsync() != ArmorStatus.Approved && deadline.Elapsed < WaitLimit)
                await Task.Delay(100, TestContext.Current.CancellationToken);

            Assert.Equal(ArmorStatus.Approved, await StatusAsync());
        }
    }
}
