using System.Diagnostics;
using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Blacklist.Models;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Dashboard.Models;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Guilds.Models;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using DataAccessLibrary.Tests.Database;
using MinerBot.Shared.Armor;
using MinerBot.Shared.Requests;
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

            // Every table the end checks, because the other classes leave their rows behind, and the order the classes run
            // in changes with the folder the tests run from
            await db.ClearAsync(MariaDbFixture.SessionTable, MariaDbFixture.StatusTable, MariaDbFixture.EventTable, MariaDbFixture.JobRunTable,
                MariaDbFixture.JobStatusTable, MariaDbFixture.SampleTable, MariaDbFixture.DailyTable, MariaDbFixture.BotGuildTable, MariaDbFixture.BotChannelTable,
                MariaDbFixture.GuildEventTable, MariaDbFixture.GuildDailyTable, MariaDbFixture.RequestTable, MariaDbFixture.SettingsTable);
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

            // The bot's status and history, and its clean-up
            var status = new BotStatusData(factory, names);
            var retention = new RetentionData(factory, names);
            var day = Noon.Date;

            await status.SaveStatus(new BotStatus { run_id = 1, started_at = Noon, heartbeat_at = Noon, discord_state = DiscordState.Connected, steam_state = SteamState.LoggedOn });
            await status.AddEvents([new BotEvent { run_id = 1, seq = 1, at = Noon, kind = "bot.started", level = EventLevel.Info }]);
            await status.SaveJobRuns([new JobRun { run_id = 1, seq = 1, job = "ArmorJob.AddColorArrays", started_at = Noon, result = JobResult.Ok }]);
            await status.SaveJobStatus([new JobStatus { job = "ArmorJob.AddColorArrays", schedule = "0 * * * *", updated_at = Noon },
                new JobStatus { job = "OldJob.Gone", schedule = "0 * * * *", updated_at = Noon }]);
            await status.SaveSamples([new UptimeSample { minute = day.AddDays(-1), discord_up = true, steam_up = true }]);

            Assert.NotNull(await status.GetStatus());

            // And the dashboard's reads of them, both statements of a page and both halves of the record's start included
            var statusReads = new BotStatusReads(factory, names);
            Assert.Equal(1UL, (await statusReads.GetStatus())?.run_id);
            var events = await statusReads.ListEvents(new EventQuery());
            Assert.Equal(1, events.Total);
            Assert.Single(events.Rows);
            Assert.NotNull(await statusReads.LatestEvent("bot.started"));
            Assert.NotNull(await statusReads.FirstEventAfter("bot.started", null));
            Assert.Single(await statusReads.CountEvents(["bot.started"], Noon.AddDays(-30), Noon.AddDays(-7)));
            Assert.Equal(2, (await statusReads.ListJobs()).Count);
            Assert.Single(await statusReads.ListJobRuns(["ArmorJob.AddColorArrays"], 50));
            Assert.Single(await statusReads.CountSampleDays(day.AddDays(-1), day));
            Assert.Equal(1, (await statusReads.CountSamples(day.AddDays(-1), day)).sampled_minutes);
            Assert.Equal(day.AddDays(-1), (await statusReads.FirstRecord()).first_minute);

            Assert.Equal(1, await status.DeleteOtherJobs(["ArmorJob.AddColorArrays"]));
            Assert.Equal(1, await retention.TotalUptimeDays(day.AddDays(-1), day));
            Assert.Single(await statusReads.ListUptimeDays(day.AddDays(-1), day));
            Assert.Equal(day.AddDays(-1), (await statusReads.FirstRecord()).first_day);
            Assert.Equal(1, await retention.DeleteSamples(day, 10));
            Assert.Equal(1, await retention.DeleteEvents(Noon.AddSeconds(1), 10));
            Assert.Equal(1, await retention.DeleteJobRuns(Noon.AddSeconds(1), 10));
            var withAddress = Audit();
            withAddress.ip = "203.0.113.7";
            await audit.AddAudit(withAddress);
            await sessions.CreateSession(SessionKey, Session());

            Assert.Equal(1, await retention.ClearAuditAddresses(Noon.AddYears(1), 10));
            Assert.Equal(3, await retention.DeleteAuditRows(Noon.AddYears(1), 10));
            Assert.Equal(1, await retention.DeleteSessions(Noon.AddYears(1), 10));

            // The bot's copy of its servers
            var guilds = new BotGuildData(factory, names);

            Assert.True(await guilds.SaveGuild(new BotGuild { guild_id = 1, name = "Test Server", available = true },
                [new BotGuildChannel { channel_id = 2, name = "general", type = GuildChannelType.Text }], Noon, 1, 1));
            Assert.Equal([1UL], await guilds.ListGuildsIn());
            Assert.Equal(1, await guilds.MarkUnavailable([1], Noon));
            await guilds.SaveMemberCount(1, 10, day, Noon);
            Assert.Equal(1, await CountAsync($"{quoted}.guild_channels"));

            // And the dashboard's reads of it, before the leave deletes the channels
            var guildReads = new BotGuildReads(factory, names);
            Assert.Equal("Test Server", Assert.Single(await guildReads.ListGuilds()).name);
            Assert.NotNull(await guildReads.GetGuild(1));
            Assert.Single(await guildReads.ListChannels(1));
            Assert.Single(await guildReads.FindChannels([2]));
            Assert.Single(await guildReads.ListGuildEvents(1, 50));
            Assert.Single(await guildReads.ListMemberDays(1, day, day.AddDays(1)));
            Assert.Single(await guildReads.MemberTrends(day));

            Assert.True(await guilds.LeaveGuild(1, Noon.AddSeconds(1), 1, 2, null));
            Assert.Equal(2, await retention.DeleteGuildEvents(Noon.AddSeconds(2), 10));

            // The requests to the bot: the dashboard's insert with its audit row and its reads, the bot's answer, its settings,
            // and the clean-up
            var queue = new BotRequestQueue(factory, names);
            var requests = new BotRequestData(factory, names);
            var refresh = RequestFields.New(RequestFields.NewKey(), RequestKinds.Find(RequestKinds.GuildRefresh)!, 1000000000000000001, "Test Admin",
                RequestRoles.Admin, 1, null, null, Noon);

            var requestId = await queue.RunInTransaction(async transaction =>
            {
                var added = await queue.AddRequest(refresh, new byte[RequestSigning.SignatureLength], transaction);
                await audit.AddAudit(Audit(), transaction);
                return added;
            });

            Assert.Equal([requestId], await requests.ListOpenIds(1, 20, restartOnly: false));
            Assert.NotNull(await requests.GetRequest(requestId));
            Assert.NotNull(await queue.GetRequest(requestId));
            Assert.Equal(requestId, (await queue.GetRequestByKey(refresh.RequestKey))?.id);
            Assert.True(await requests.Claim(requestId, 1, Noon.AddSeconds(1)));
            Assert.Equal(1, (await requests.CountClaims([RequestKinds.GuildRefresh], Noon, 1)).count);
            Assert.True(await requests.Finish(requestId, 1, RequestStates.Done, "Done", null, Noon.AddSeconds(2)));
            Assert.True(await requests.SetDm(requestId, RequestDmStates.Sent));
            Assert.False(await requests.Decide(requestId, 1, RequestStates.Refused, "Refused", Noon.AddSeconds(3)));
            Assert.Empty(await requests.SweepOtherRuns(2, Noon.AddSeconds(3), "Unknown", Noon.AddSeconds(3)));
            Assert.Empty(await requests.ListUntoldRestarts(2, Noon));
            await requests.SavePause(true, "Testing", Noon, 1, requestId, Noon);
            await requests.SaveStatus("Testing", Noon, 1, requestId, Noon);
            Assert.True((await requests.GetSettings())?.posts_paused);
            Assert.Equal("Testing", (await queue.GetSettings())?.status_text);
            Assert.Equal(1, await retention.DeleteRequests(Noon.AddSeconds(1), 10));

            foreach (var table in new[] { "bot_status", "job_status", "uptime_daily", "guilds", "guild_daily", "bot_settings" })
                Assert.Equal(1, await CountAsync($"{quoted}.{table}"));

            // Nothing reached the real database's tables
            foreach (var table in new[] { MariaDbFixture.AuditTable, MariaDbFixture.ReviewTable, MariaDbFixture.SessionTable, MariaDbFixture.StatusTable,
                MariaDbFixture.EventTable, MariaDbFixture.JobRunTable, MariaDbFixture.JobStatusTable, MariaDbFixture.SampleTable, MariaDbFixture.DailyTable,
                MariaDbFixture.BotGuildTable, MariaDbFixture.BotChannelTable, MariaDbFixture.GuildEventTable, MariaDbFixture.GuildDailyTable,
                MariaDbFixture.RequestTable, MariaDbFixture.SettingsTable })
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
