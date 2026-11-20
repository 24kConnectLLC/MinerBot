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
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Guilds.Models;
using DataAccessLibrary.Health;
using DataAccessLibrary.Migrations;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using DataAccessLibrary.Tests.Database;
using MinerBot.Shared.Armor;
using MinerBot.Shared.Requests;
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
            "column INSERT minerbot_db bot_commands actor_id",
            "column INSERT minerbot_db bot_commands actor_name",
            "column INSERT minerbot_db bot_commands actor_role",
            "column INSERT minerbot_db bot_commands argument",
            "column INSERT minerbot_db bot_commands channel_id",
            "column INSERT minerbot_db bot_commands created_at",
            "column INSERT minerbot_db bot_commands expires_at",
            "column INSERT minerbot_db bot_commands guild_id",
            "column INSERT minerbot_db bot_commands kind",
            "column INSERT minerbot_db bot_commands request_key",
            "column INSERT minerbot_db bot_commands signature",
            "column SELECT website_db ihvph_discord_servers guild_id",
            "column UPDATE website_db ihvph_armor_styles Status",
            "column UPDATE website_db ihvph_blacklist expire_date",
            "column UPDATE website_db ihvph_blacklist notes",
            "column UPDATE website_db ihvph_blacklist reason",
            "column UPDATE website_db ihvph_blacklist username",
            "global USAGE",
            "limits 15 20.000000 0 0 0",
            "table DELETE minerbot_db dashboard_sessions",
            "table DELETE website_db ihvph_blacklist",
            "table INSERT minerbot_db armor_reviews",
            "table INSERT minerbot_db audit_log",
            "table INSERT minerbot_db dashboard_sessions",
            "table INSERT website_db ihvph_blacklist",
            "table SELECT minerbot_db armor_reviews",
            "table SELECT minerbot_db audit_log",
            "table SELECT minerbot_db bot_commands",
            "table SELECT minerbot_db bot_events",
            "table SELECT minerbot_db bot_settings",
            "table SELECT minerbot_db bot_status",
            "table SELECT minerbot_db dashboard_sessions",
            "table SELECT minerbot_db guild_channels",
            "table SELECT minerbot_db guild_daily",
            "table SELECT minerbot_db guild_events",
            "table SELECT minerbot_db guilds",
            "table SELECT minerbot_db job_runs",
            "table SELECT minerbot_db job_status",
            "table SELECT minerbot_db uptime_daily",
            "table SELECT minerbot_db uptime_samples",
            "table SELECT website_db ihvph_armor_styles",
            "table SELECT website_db ihvph_blacklist",
            "table SELECT website_db ihvph_discord_servers",
            "table UPDATE minerbot_db dashboard_sessions"
        ];

        static CancellationToken Token => TestContext.Current.CancellationToken;

        static string DeployFile(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "deploy", name));

        // With this run's names, as deploy.sh renders it
        static string Rendered(string sql) => sql
            .Replace("{{SITE_DB}}", Site)
            .Replace("{{BOT_DB}}", Bot)
            .Replace("{{GRANTEES}}", Grantee);

        // A made-up request-signing key, the 32 bytes 00 01 ... 1f
        static readonly byte[] SigningKey = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

        // A request's columns and values, written by hand, for the statements the login is refused
        const string RequestColumns = "request_key, kind, actor_id, actor_name, actor_role, created_at, expires_at, signature";
        const string RequestValues = "REPEAT('a', 32), 'posts.pause', 1, 'x', 'owner', NOW(3), NOW(3), REPEAT('a', 32)";

        async Task WithLoginAsync(Func<MariaDbFixture, string, Task> test)
        {
            var db = fixture.Require();
            await AsDashboardAsync(db, password => test(db, password));
        }

        // The login as setup-dashboard.sh makes it, with no rights, then the file applied. Dropped afterwards whatever
        // happened, which takes every right it had with it. Another class's test uses it for a row only this login could
        // write.
        internal static async Task AsDashboardAsync(MariaDbFixture db, Func<string, Task> test)
        {
            var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

            await db.RootExecuteAsync($"DROP USER IF EXISTS {Grantee}");
            await db.RootExecuteAsync($"CREATE USER {Grantee} IDENTIFIED BY '{password}'");

            try
            {
                await ApplyAsync(db);
                await test(password);
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
        internal static string ConnectionAs(MariaDbFixture db, string password, string database) =>
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

        static AuditEntry Audit(string action, string targetType = "session", string? targetId = null) => new()
        {
            at = Noon,
            actor_id = UserId,
            actor_name = "Test Admin",
            actor_role = AuditRole.Admin,
            action = action,
            outcome = AuditOutcome.Ok,
            target_type = targetType,
            target_id = targetId,
            ip = "203.0.113.7"
        };

        static BlacklistModel Player(uint accountNumber, string reason = "Testing") => new()
        {
            steam_id = accountNumber,
            username = "Test Player",
            reason = reason,
            expire_date = new DateTime(2027, 1, 2),
            notes = "Made up"
        };

        static ArmorSubmission Armor(long id, string status, string name) => new()
        {
            ID = id,
            CreatedAt = Noon,
            Status = status,
            Author = "Test Author",
            ArmorName = name,
            ArmorScreenshot = "https://murderminershub.com/wp-content/uploads/ArmorPreviews/Test-Armor.png"
        };

        static ArmorReview Review(long armorId, string? from, string to) => new()
        {
            armor_id = armorId,
            from_status = from,
            to_status = to,
            reason = "Testing",
            reviewed_by = UserId,
            reviewer_name = "Test Admin",
            reviewed_at = Noon
        };

        [Fact]
        public async Task RightsAreExactlyTheList()
        {
            await WithLoginAsync(async (db, _) => Assert.Equal(Expected, await RightsAsync(db)));
        }

        // Every repository call the dashboard makes today, under its login: the sessions, the audit rows, on MinerBot's own
        // connection and inside a transaction on the website's, and the audit page's reads of them, armor review's reads and
        // each kind of its transactions, the blacklist's reads and each kind of its changes, the status and servers pages'
        // reads of what the bot writes and of the website's settings, and the readiness check of both databases
        [Fact]
        public async Task EverythingTodaysDashboardDoesWorks()
        {
            await WithLoginAsync(async (db, password) =>
            {
                await db.ClearAsync(MariaDbFixture.SessionTable, MariaDbFixture.AuditTable, MariaDbFixture.ArmorTable, MariaDbFixture.ReviewTable,
                    MariaDbFixture.BlacklistTable, MariaDbFixture.StatusTable, MariaDbFixture.EventTable, MariaDbFixture.JobRunTable, MariaDbFixture.JobStatusTable,
                    MariaDbFixture.SampleTable, MariaDbFixture.DailyTable, MariaDbFixture.BotGuildTable, MariaDbFixture.BotChannelTable, MariaDbFixture.GuildEventTable,
                    MariaDbFixture.GuildDailyTable, MariaDbFixture.GuildTable, MariaDbFixture.RequestTable, MariaDbFixture.SettingsTable);
                await db.AddArmorAsync(Armor(1, ArmorStatus.Pending, "Test Armor 1"));
                await db.AddArmorAsync(Armor(2, ArmorStatus.Approved, "Test Armor 2"));

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

                // Armor review's reads: the list, the search, the review page and its history
                var submissions = new ArmorSubmissionData(factory);
                var reviews = new ArmorReviewData(factory, names);

                Assert.Equal(1, (await submissions.ListByStatus(ArmorStatus.Pending, 0, 25)).Total);
                Assert.Equal(2, (await submissions.ListByStatus(null, 0, 25)).Total);
                Assert.Single((await submissions.Search("Armor 2", ArmorStatus.Approved, 0, 25)).Rows);
                Assert.NotNull(await submissions.GetSubmission(1));
                Assert.False(await submissions.IsApprovedNameTaken("Test Armor 1", 1));
                Assert.Empty(await reviews.ListReviews(1));

                // An approval as the review page makes it: the name check first, the locked read, the status, then the review
                // and audit rows in MinerBot's database, all in one transaction on the website's connection
                Assert.True(await submissions.RunInTransaction(async transaction =>
                {
                    Assert.False(await submissions.IsApprovedNameTaken("Test Armor 1", 1, transaction));
                    Assert.NotNull(await submissions.GetSubmission(1, transaction));
                    Assert.True(await submissions.ChangeStatus(1, ArmorStatus.Pending, ArmorStatus.Approved, transaction));
                    await reviews.AddReview(Review(1, ArmorStatus.Pending, ArmorStatus.Approved), transaction);
                    await audit.AddAudit(Audit("armor.approve"), transaction);
                    return true;
                }));

                // The other actions, which read and lock only their own row
                Assert.True(await submissions.RunInTransaction(async transaction =>
                {
                    Assert.NotNull(await submissions.GetSubmission(2, transaction));
                    Assert.True(await submissions.ChangeStatus(2, ArmorStatus.Approved, ArmorStatus.NotApproved, transaction));
                    await reviews.AddReview(Review(2, ArmorStatus.Approved, ArmorStatus.NotApproved), transaction);
                    await audit.AddAudit(Audit("armor.takedown"), transaction);
                    return true;
                }));

                Assert.Single(await reviews.ListReviews(1));
                Assert.Equal(ArmorStatus.Approved, (await submissions.GetSubmission(1))?.Status);
                Assert.Equal(ArmorStatus.NotApproved, (await submissions.GetSubmission(2))?.Status);

                // The blacklist's reads: the list and its search, an entry, and the rows no account number can be, which only
                // something else can type in
                var blacklist = new BlacklistData(factory);
                await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username) VALUES (5000000000, 'Test Typo')");

                Assert.Equal(0, (await blacklist.SearchBlacklist(null, null, 0, 25)).Total);
                Assert.Empty((await blacklist.SearchBlacklist(12345, "Test", 0, 25)).Rows);
                Assert.Null(await blacklist.GetBannedPlayer(12345));
                Assert.Empty(await blacklist.GetBlacklist());
                Assert.Equal([5000000000m], await blacklist.ListOutOfRangeSteamIds());
                Assert.Equal("Test Typo", Assert.Single(await blacklist.ListOutOfRangeBans()).username);
                Assert.NotNull(await blacklist.GetOutOfRangeBan(5000000000));

                // An add, an edit and a removal as the blacklist's pages make them: the locked read, the write, and the audit
                // row in MinerBot's database, each in one transaction on the website's connection
                Assert.True(await blacklist.RunInTransaction(async transaction =>
                {
                    Assert.Null(await blacklist.GetBannedPlayer(12345, transaction));
                    await blacklist.InsertPlayerBan(Player(12345), transaction);
                    await audit.AddAudit(Audit("blacklist.add", "blacklist", "12345"), transaction);
                    return true;
                }));

                Assert.True(await blacklist.RunInTransaction(async transaction =>
                {
                    Assert.NotNull(await blacklist.GetBannedPlayer(12345, transaction));
                    Assert.True(await blacklist.UpdatePlayerBan(Player(12345, "Edited"), transaction));
                    await audit.AddAudit(Audit("blacklist.edit", "blacklist", "12345"), transaction);
                    return true;
                }));

                Assert.Equal("Edited", (await blacklist.GetBannedPlayer(12345))?.reason);

                Assert.True(await blacklist.RunInTransaction(async transaction =>
                {
                    Assert.NotNull(await blacklist.GetBannedPlayer(12345, transaction));
                    Assert.True(await blacklist.RemovePlayerBan(12345, transaction));
                    await audit.AddAudit(Audit("blacklist.remove", "blacklist", "12345"), transaction);
                    return true;
                }));

                // The row no account number can be, with a locked read of its own
                Assert.True(await blacklist.RunInTransaction(async transaction =>
                {
                    Assert.NotNull(await blacklist.GetOutOfRangeBan(5000000000, transaction));
                    Assert.True(await blacklist.RemoveOutOfRangeSteamId(5000000000, transaction));
                    await audit.AddAudit(Audit("blacklist.remove", "blacklist", "5000000000"), transaction);
                    return true;
                }));

                Assert.Empty(await blacklist.ListOutOfRangeSteamIds());

                // The audit page's reads, with and without the visitors' addresses, an entry's history, and the latest change
                // of each entry on a page of the list
                Assert.Equal(8, (await audit.ListAudit(new AuditQuery())).Total);
                Assert.Null((await audit.ListAudit(new AuditQuery())).Rows[0].ip);
                Assert.Equal("203.0.113.7", (await audit.ListAudit(new AuditQuery { IncludeIp = true })).Rows[0].ip);
                Assert.Equal(3, (await audit.ListAudit(new AuditQuery { TargetType = "blacklist", TargetId = "12345" })).Total);
                Assert.Equal("blacklist.remove", Assert.Single(await audit.LatestChanges("blacklist", ["12345", "54321"])).action);

                // The status pages' reads, of rows the bot's own code writes with the bot's rights
                var day = Noon.Date;
                await db.Status().SaveStatus(new BotStatus { run_id = 1, started_at = Noon, heartbeat_at = Noon, discord_state = DiscordState.Connected, steam_state = SteamState.LoggedOn });
                await db.Status().AddEvents([new BotEvent { run_id = 1, seq = 1, at = Noon, kind = "discord.connected", level = EventLevel.Info },
                    new BotEvent { run_id = 1, seq = 2, at = Noon.AddSeconds(1), kind = "discord.disconnected", level = EventLevel.Warning, detail = "GatewayReconnectException" }]);
                await db.Status().SaveJobStatus([new JobStatus { job = "ArmorJob.AddColorArrays", schedule = "0 * * * *", updated_at = Noon }]);
                await db.Status().SaveJobRuns([new JobRun { run_id = 1, seq = 1, job = "ArmorJob.AddColorArrays", started_at = Noon, result = JobResult.Ok }]);
                await db.Status().SaveSamples([new UptimeSample { minute = day.AddDays(-1), discord_up = true, steam_up = true }]);
                await db.Retention().TotalUptimeDays(day.AddDays(-1), day);
                await db.Status().SaveSamples([new UptimeSample { minute = day, discord_up = true, steam_up = false }]);

                var statusReads = new BotStatusReads(factory, names);
                Assert.NotNull(await statusReads.GetStatus());
                Assert.Equal(2, (await statusReads.ListEvents(new EventQuery { IncludeRoutine = true })).Total);
                Assert.Equal(1, (await statusReads.ListEvents(new EventQuery())).Total);
                var connected = await statusReads.LatestEvent("discord.connected");
                Assert.NotNull(await statusReads.FirstEventAfter("discord.disconnected", connected));
                Assert.Single(await statusReads.CountEvents(["discord.connected"], day.AddDays(-30), day.AddDays(-7)));
                Assert.Single(await statusReads.ListJobs());
                Assert.Single(await statusReads.ListJobRuns(["ArmorJob.AddColorArrays"], 50));
                Assert.Single(await statusReads.ListUptimeDays(day.AddDays(-89), day));
                Assert.Single(await statusReads.CountSampleDays(day, day.AddDays(1)));
                Assert.Equal(1, (await statusReads.CountSamples(day, day.AddDays(1))).discord_minutes);
                Assert.Equal(day.AddDays(-1), (await statusReads.FirstRecord()).first_day);

                // The servers pages' reads, of the bot's copy and of the website's settings
                await db.BotGuilds().SaveGuild(new BotGuild { guild_id = 1000000000000000005, name = "Test Server", available = true },
                    [new BotGuildChannel { channel_id = 2, name = "general", type = GuildChannelType.Text, can_view = true }], Noon, 1, 1);
                await db.BotGuilds().SaveMemberCount(1000000000000000005, 10, day, Noon);
                await db.Guilds().SaveGuild(new Guild { guild_id = 1000000000000000005, armor_channel_id = 2 });

                var guildReads = new BotGuildReads(factory, names);
                var settings = new GuildSettingsReads(factory);
                Assert.Single(await guildReads.ListGuilds());
                Assert.NotNull(await guildReads.GetGuild(1000000000000000005));
                Assert.Single(await guildReads.ListChannels(1000000000000000005));
                Assert.Single(await guildReads.FindChannels([2, 3]));
                Assert.Single(await guildReads.ListGuildEvents(1000000000000000005, 50));
                Assert.Single(await guildReads.ListMemberDays(1000000000000000005, day.AddDays(-89), day.AddDays(1)));
                Assert.Single(await guildReads.MemberTrends(day.AddDays(-30)));
                Assert.Equal(2UL, Assert.Single(await settings.ListSettings()).armor_channel_id);
                Assert.NotNull(await settings.GetSettings(1000000000000000005));

                // A request to the bot, call for call as the dashboard's BotRequests writes one: the signed request and its audit
                // row in one transaction on MinerBot's own connection, with the new request's ID from LAST_INSERT_ID(). Then the
                // request command's reads of it by its key, while it waits for the bot, and by its ID, and of the bot's settings,
                // before and after the bot writes them. An audit row on its own, as BotRequests writes for a commit the database
                // didn't confirm, and the read of the bot's status after a restart, are above.
                var queue = new BotRequestQueue(factory, names);
                var pause = RequestFields.New(RequestFields.NewKey(), RequestKinds.Find(RequestKinds.PostsPause)!, UserId, "Test Owner", RequestRoles.Owner,
                    null, null, "Testing", Noon);

                var requestId = await queue.RunInTransaction(async transaction =>
                {
                    var added = await queue.AddRequest(pause, RequestSigning.Sign(SigningKey, pause), transaction);
                    await audit.AddAudit(Audit(RequestKinds.PostsPause, "request", added.ToString(CultureInfo.InvariantCulture)), transaction);
                    return added;
                });

                var request = await queue.GetRequest(requestId);
                Assert.NotNull(request);
                Assert.Null(request.state);
                Assert.True(RequestSigning.Verify(SigningKey, request.Fields(), request.signature));
                Assert.Equal(requestId, (await queue.GetRequestByKey(pause.RequestKey))?.id);
                Assert.Null(await queue.GetSettings());
                await db.Requests().SavePause(true, "Testing", Noon, UserId, requestId, Noon);
                Assert.True((await queue.GetSettings())?.posts_paused);

                var health = new DatabaseHealthData(factory);
                Assert.True(await health.Answers(DBConnectionName.Armor, TimeSpan.FromSeconds(5)).WaitAsync(CallLimit, Token));
                Assert.True(await health.Answers(DBConnectionName.Bot, TimeSpan.FromSeconds(5)).WaitAsync(CallLimit, Token));

                Assert.Equal(["armor.approve", "armor.takedown", "auth.refused", "blacklist.add", "blacklist.edit", "blacklist.remove", "blacklist.remove", "posts.pause",
                    "session.start"],
                    (await db.RootQueryAsync<string>($"SELECT action FROM {MariaDbFixture.AuditTable}")).Order(StringComparer.Ordinal));
                Assert.Equal(0, (await db.RootQueryAsync<long>($"SELECT COUNT(*) FROM {MariaDbFixture.BlacklistTable}")).Single());
                Assert.Equal(1, (await db.RootQueryAsync<long>($"SELECT COUNT(*) FROM {MariaDbFixture.SessionTable}")).Single());
                Assert.Equal(2, (await db.RootQueryAsync<long>($"SELECT COUNT(*) FROM {MariaDbFixture.ReviewTable}")).Single());
            });
        }

        // Everything else is refused, checked by MariaDB's error number. {root} is root's own session, for KILL.
        public static TheoryData<string, string> Forbidden => new()
        {
            // This login adds to the audit log and reads it back, but never changes or deletes a row. The bot's own login,
            // which the cPanel account owns, can still change it.
            { "change the audit log", $"UPDATE {Bot}.audit_log SET detail = 'changed' WHERE 1 = 0" },
            { "delete from the audit log", $"DELETE FROM {Bot}.audit_log WHERE 1 = 0" },
            { "truncate the audit log", $"TRUNCATE TABLE {Bot}.audit_log" },
            // MinerBot's other tables. Armor review's history is only ever added to.
            { "change armor_reviews", $"UPDATE {Bot}.armor_reviews SET reason = 'changed' WHERE 1 = 0" },
            { "delete from armor_reviews", $"DELETE FROM {Bot}.armor_reviews WHERE 1 = 0" },
            { "read schema_version", $"SELECT * FROM {Bot}.schema_version" },
            { "add to schema_version", $"INSERT INTO {Bot}.schema_version (version, name, checksum, applied_at) VALUES (9999, 'x', 'x', NOW(3))" },
            { "change schema_version", $"UPDATE {Bot}.schema_version SET name = 'changed' WHERE 1 = 0" },
            { "delete from schema_version", $"DELETE FROM {Bot}.schema_version WHERE 1 = 0" },
            // The bot's status and history, which only the bot writes, and which the status pages only read
            { "add to bot_status", $"INSERT INTO {Bot}.bot_status (id, run_id, started_at, heartbeat_at, discord_state, steam_state) VALUES (2, 1, NOW(3), NOW(3), 'connected', 'offline')" },
            { "change bot_status", $"UPDATE {Bot}.bot_status SET version = NULL WHERE 1 = 0" },
            { "delete from bot_status", $"DELETE FROM {Bot}.bot_status WHERE 1 = 0" },
            { "add to bot_events", $"INSERT INTO {Bot}.bot_events (run_id, seq, at, kind, level) VALUES (1, 1, NOW(3), 'bot.started', 'info')" },
            { "change bot_events", $"UPDATE {Bot}.bot_events SET detail = NULL WHERE 1 = 0" },
            { "delete from bot_events", $"DELETE FROM {Bot}.bot_events WHERE 1 = 0" },
            { "add to job_status", $"INSERT INTO {Bot}.job_status (job, schedule, updated_at) VALUES ('X.Y', '0 * * * *', NOW(3))" },
            { "change job_status", $"UPDATE {Bot}.job_status SET last_detail = NULL WHERE 1 = 0" },
            { "delete from job_status", $"DELETE FROM {Bot}.job_status WHERE 1 = 0" },
            { "add to job_runs", $"INSERT INTO {Bot}.job_runs (run_id, seq, job, started_at, result) VALUES (1, 1, 'X.Y', NOW(3), 'ok')" },
            { "change job_runs", $"UPDATE {Bot}.job_runs SET detail = NULL WHERE 1 = 0" },
            { "delete from job_runs", $"DELETE FROM {Bot}.job_runs WHERE 1 = 0" },
            { "add to uptime_samples", $"INSERT INTO {Bot}.uptime_samples (minute, discord_up, steam_up) VALUES (NOW(), 1, 1)" },
            { "change uptime_samples", $"UPDATE {Bot}.uptime_samples SET discord_up = 1 WHERE 1 = 0" },
            { "delete from uptime_samples", $"DELETE FROM {Bot}.uptime_samples WHERE 1 = 0" },
            { "add to uptime_daily", $"INSERT INTO {Bot}.uptime_daily (day, sampled_minutes, discord_minutes, steam_minutes) VALUES (CURDATE(), 1, 1, 1)" },
            { "change uptime_daily", $"UPDATE {Bot}.uptime_daily SET discord_minutes = 1 WHERE 1 = 0" },
            { "delete from uptime_daily", $"DELETE FROM {Bot}.uptime_daily WHERE 1 = 0" },
            // The bot's copy of its servers, which only the bot writes, and which the servers pages only read
            { "add to guilds", $"INSERT INTO {Bot}.guilds (guild_id, name, first_seen_at, available, bot_administrator, bot_permissions, updated_at) VALUES (1, 'x', NOW(3), 1, 0, 0, NOW(3))" },
            { "change guilds", $"UPDATE {Bot}.guilds SET name = 'changed' WHERE 1 = 0" },
            { "delete from guilds", $"DELETE FROM {Bot}.guilds WHERE 1 = 0" },
            { "truncate guilds", $"TRUNCATE TABLE {Bot}.guilds" },
            { "add to guild_channels", $"INSERT INTO {Bot}.guild_channels (channel_id, guild_id, name, type, `position`, can_view, can_send, can_embed, updated_at) VALUES (1, 1, 'x', 'text', 0, 1, 1, 1, NOW(3))" },
            { "change guild_channels", $"UPDATE {Bot}.guild_channels SET can_send = 1 WHERE 1 = 0" },
            { "delete from guild_channels", $"DELETE FROM {Bot}.guild_channels WHERE 1 = 0" },
            { "add to guild_events", $"INSERT INTO {Bot}.guild_events (run_id, seq, guild_id, at, kind) VALUES (1, 1, 1, NOW(3), 'joined')" },
            { "change guild_events", $"UPDATE {Bot}.guild_events SET detail = NULL WHERE 1 = 0" },
            { "delete from guild_events", $"DELETE FROM {Bot}.guild_events WHERE 1 = 0" },
            { "add to guild_daily", $"INSERT INTO {Bot}.guild_daily (day, guild_id, member_count) VALUES (CURDATE(), 1, 1)" },
            { "change guild_daily", $"UPDATE {Bot}.guild_daily SET member_count = 1 WHERE 1 = 0" },
            { "delete from guild_daily", $"DELETE FROM {Bot}.guild_daily WHERE 1 = 0" },
            // The requests to the bot, which this login adds by naming only the columns the dashboard fills in, and reads back,
            // but never changes or deletes: no request starts done, taken by a run, at a time that would use up a limit, or with
            // an ID of its own. The bot's settings only change through requests.
            { "change a request", $"UPDATE {Bot}.bot_commands SET state = 'done' WHERE 1 = 0" },
            { "delete a request", $"DELETE FROM {Bot}.bot_commands WHERE 1 = 0" },
            { "truncate the requests", $"TRUNCATE TABLE {Bot}.bot_commands" },
            { "replace a request", $"REPLACE INTO {Bot}.bot_commands ({RequestColumns}) VALUES ({RequestValues})" },
            { "add a request or change the one there", $"INSERT INTO {Bot}.bot_commands ({RequestColumns}) VALUES ({RequestValues}) ON DUPLICATE KEY UPDATE kind = kind" },
            { "add a request with its state", $"INSERT INTO {Bot}.bot_commands ({RequestColumns}, state) VALUES ({RequestValues}, 'done')" },
            { "add a request with its result", $"INSERT INTO {Bot}.bot_commands ({RequestColumns}, result) VALUES ({RequestValues}, 'Done')" },
            { "add a request taken at a time", $"INSERT INTO {Bot}.bot_commands ({RequestColumns}, claimed_at) VALUES ({RequestValues}, NOW(3))" },
            { "add a request with its ID", $"INSERT INTO {Bot}.bot_commands (id, {RequestColumns}) VALUES (9999, {RequestValues})" },
            { "add the bot's settings", $"INSERT INTO {Bot}.bot_settings (id, posts_paused, updated_at) VALUES (1, 1, NOW(3))" },
            { "change the bot's settings", $"UPDATE {Bot}.bot_settings SET posts_paused = 1 WHERE 1 = 0" },
            { "delete the bot's settings", $"DELETE FROM {Bot}.bot_settings WHERE 1 = 0" },
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
            // The website's tables: armor is read and only its status changed, the blacklist is read, added to and deleted
            // from and four of its columns changed, but never an entry's account, the servers' settings are read, and
            // nothing else is read or written
            { "add armor", $"INSERT INTO {Site}.ihvph_armor_styles (Author) VALUES ('x')" },
            { "change an armor's name", $"UPDATE {Site}.ihvph_armor_styles SET ArmorName = 'x' WHERE 1 = 0" },
            { "change an armor's ID", $"UPDATE {Site}.ihvph_armor_styles SET ID = ID WHERE 1 = 0" },
            { "change an armor's save string", $"UPDATE {Site}.ihvph_armor_styles SET ColorArray = NULL WHERE 1 = 0" },
            { "delete armor", $"DELETE FROM {Site}.ihvph_armor_styles WHERE 1 = 0" },
            { "change a ban's account number", $"UPDATE {Site}.ihvph_blacklist SET steam_id = steam_id WHERE 1 = 0" },
            { "truncate the blacklist", $"TRUNCATE TABLE {Site}.ihvph_blacklist" },
            { "alter the blacklist", $"ALTER TABLE {Site}.ihvph_blacklist ADD COLUMN rights_check INT" },
            { "lock the blacklist", $"LOCK TABLES {Site}.ihvph_blacklist WRITE" },
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

        // The repositories later pull requests use are refused, which records what the server channels page and later ones
        // must add to the file: the servers' settings are read since the status pages, but never saved or deleted
        [Fact]
        public async Task RepositoriesNotUsedYetAreRefused()
        {
            await WithLoginAsync(async (db, password) =>
            {
                var factory = new SQLConnectionFactory(Connections(db, password));

                var calls = new (string What, Func<Task> Call)[]
                {
                    ("GuildData.SaveGuild", () => new GuildData(factory).SaveGuild(new Guild { guild_id = 1000000000000000005, armor_channel_id = 1 })),
                    ("GuildData.DeleteGuild", () => new GuildData(factory).DeleteGuild(1000000000000000005))
                };

                foreach (var (what, call) in calls)
                {
                    var refused = await Assert.ThrowsAsync<MySqlException>(call);
                    Assert.True(Refusals.Contains(refused.Number), $"{what} failed with {refused.Number}, which isn't a refusal");
                }
            });
        }

        // The bot's own writes of its status, history, servers and requests, each through the bot's own code, are refused:
        // the dashboard reads them and nothing more. The servers' save and leave, and the requests' sweep, start with a locked
        // read, which SELECT allows, and their transactions roll back at the first write.
        [Fact]
        public async Task TheBotsWritesAreRefused()
        {
            await WithLoginAsync(async (db, password) =>
            {
                await db.ClearAsync(MariaDbFixture.StatusTable, MariaDbFixture.EventTable, MariaDbFixture.JobRunTable, MariaDbFixture.JobStatusTable,
                    MariaDbFixture.SampleTable, MariaDbFixture.DailyTable, MariaDbFixture.BotGuildTable, MariaDbFixture.BotChannelTable, MariaDbFixture.GuildEventTable,
                    MariaDbFixture.GuildDailyTable, MariaDbFixture.RequestTable, MariaDbFixture.SettingsTable);

                // A request that waits, and one an earlier run took before Noon, which each write would change if it could
                await db.RootExecuteAsync($@"INSERT INTO {MariaDbFixture.RequestTable} (id, {RequestColumns}, state, run_id, claimed_at)
                                             VALUES (1, {RequestValues}, NULL, NULL, NULL), (2, REPEAT('b', 32), 'posts.pause', 1, 'x', 'owner', @old, @old,
                                             REPEAT('a', 32), 'running', 7, @old)", new { old = Noon.AddHours(-1) });

                var connections = Connections(db, password);
                var factory = new SQLConnectionFactory(connections);
                var names = new DatabaseNames(connections);
                var status = new BotStatusData(factory, names);
                var guilds = new BotGuildData(factory, names);
                var retention = new RetentionData(factory, names);
                var requests = new BotRequestData(factory, names);
                var day = Noon.Date;

                var calls = new (string What, Func<Task> Call)[]
                {
                    ("BotStatusData.SaveStatus", () => status.SaveStatus(new BotStatus { run_id = 1, started_at = Noon, heartbeat_at = Noon, discord_state = DiscordState.Connected, steam_state = SteamState.LoggedOn })),
                    ("BotStatusData.AddEvents", () => status.AddEvents([new BotEvent { run_id = 1, seq = 1, at = Noon, kind = "bot.started", level = EventLevel.Info }])),
                    ("BotStatusData.SaveJobRuns", () => status.SaveJobRuns([new JobRun { run_id = 1, seq = 1, job = "ArmorJob.AddColorArrays", started_at = Noon, result = JobResult.Ok }])),
                    ("BotStatusData.SaveJobStatus", () => status.SaveJobStatus([new JobStatus { job = "ArmorJob.AddColorArrays", schedule = "0 * * * *", updated_at = Noon }])),
                    ("BotStatusData.DeleteOtherJobs", () => status.DeleteOtherJobs(["ArmorJob.AddColorArrays"])),
                    ("BotStatusData.SaveSamples", () => status.SaveSamples([new UptimeSample { minute = day }])),
                    ("BotGuildData.SaveGuild", () => guilds.SaveGuild(new BotGuild { guild_id = 1, name = "Test Server" }, [], Noon, 1, 1)),
                    ("BotGuildData.LeaveGuild", () => guilds.LeaveGuild(1, Noon, 1, 2, null)),
                    ("BotGuildData.MarkUnavailable", () => guilds.MarkUnavailable([1], Noon)),
                    ("BotGuildData.SaveMemberCount", () => guilds.SaveMemberCount(1, 10, day, Noon)),
                    ("RetentionData.TotalUptimeDays", () => retention.TotalUptimeDays(day.AddDays(-1), day)),
                    ("RetentionData.DeleteEvents", () => retention.DeleteEvents(Noon, 1)),
                    ("RetentionData.DeleteSamples", () => retention.DeleteSamples(day, 1)),
                    ("RetentionData.DeleteRequests", () => retention.DeleteRequests(Noon, 10)),
                    ("BotRequestData.Decide", () => requests.Decide(1, 1, RequestStates.Refused, "Refused", Noon)),
                    ("BotRequestData.Claim", () => requests.Claim(1, 1, Noon)),
                    ("BotRequestData.Finish", () => requests.Finish(2, 7, RequestStates.Done, "Done", 1, Noon)),
                    ("BotRequestData.SetDm", () => requests.SetDm(2, RequestDmStates.Sent)),
                    ("BotRequestData.SweepOtherRuns", () => requests.SweepOtherRuns(1, Noon, "Unknown", Noon)),
                    ("BotRequestData.SavePause", () => requests.SavePause(true, "Testing", Noon, UserId, 1, Noon)),
                    ("BotRequestData.SaveStatus", () => requests.SaveStatus("Testing", Noon, UserId, 1, Noon))
                };

                foreach (var (what, call) in calls)
                {
                    var refused = await Assert.ThrowsAsync<MySqlException>(call);
                    Assert.True(Refusals.Contains(refused.Number), $"{what} failed with {refused.Number}, which isn't a refusal");
                }

                foreach (var table in new[] { MariaDbFixture.StatusTable, MariaDbFixture.EventTable, MariaDbFixture.JobRunTable, MariaDbFixture.JobStatusTable,
                    MariaDbFixture.SampleTable, MariaDbFixture.DailyTable, MariaDbFixture.BotGuildTable, MariaDbFixture.GuildEventTable, MariaDbFixture.GuildDailyTable,
                    MariaDbFixture.SettingsTable })
                    Assert.Equal(0, (await db.RootQueryAsync<long>($"SELECT COUNT(*) FROM {table}")).Single());

                // Both requests are as they were
                Assert.Equal([(1UL, (string?)null, (string?)null), (2UL, "running", (string?)null)],
                    await db.RootQueryAsync<(ulong, string?, string?)>($"SELECT id, state, dm_state FROM {MariaDbFixture.RequestTable} ORDER BY id"));
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
                // A right the file doesn't give, since one it gives, such as SELECT on armor_reviews, would prove nothing
                await db.RootExecuteAsync($"GRANT DELETE ON {Bot}.armor_reviews TO {Grantee}");
                await db.RootExecuteAsync($"GRANT ALL PRIVILEGES ON {Bot}.* TO {Grantee}");
                await db.RootExecuteAsync($"GRANT SELECT (armor_channel_id) ON {Site}.ihvph_discord_servers TO {Grantee}");
                await db.RootExecuteAsync($"GRANT INSERT ON {Bot}.bot_status TO {Grantee}");
                await db.RootExecuteAsync($"ALTER USER {Grantee} WITH MAX_USER_CONNECTIONS 100 MAX_STATEMENT_TIME 0");

                var added = await RightsAsync(db);
                Assert.Contains("table DELETE minerbot_db armor_reviews", added);
                Assert.Contains("schema SELECT minerbot_db", added);
                Assert.Contains("column SELECT website_db ihvph_discord_servers armor_channel_id", added);
                Assert.Contains("table INSERT minerbot_db bot_status", added);
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

            Assert.Equal(18, tables.Count);

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
