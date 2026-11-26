using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Blacklist;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Dashboard.Models;
using DataAccessLibrary.Discord;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Guilds.Models;
using DataAccessLibrary.Health;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MinerBot.Shared.Delivery;
using MinerBot.Shared.Requests;
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

        // What the dashboard's start-up check and the bot's status writer report for a connection string, in words that
        // never quote it. It never throws, because the bot asks as it starts.
        [Theory]
        [InlineData(Site, null)]
        [InlineData("Server=127.0.0.1;Initial Catalog=site_db;Uid=user", null)]
        [InlineData("Server=127.0.0.1;Database=site_db;Uid=user;Pwd='pass;word'", null)]
        [InlineData("Server=127.0.0.1;Uid=user;Pwd=password", "names no database")]
        [InlineData("", "names no database")]
        [InlineData("Server=127.0.0.1;Database=site_db;SslMode=Requird", "can't be read")]
        [InlineData("Server=127.0.0.1;Database=site_db;Port=port", "can't be read")]
        [InlineData("Server=127.0.0.1;Database=site_db;Colour=blue", "can't be read")]
        // MySql.Data throws a MySqlException for these three, whose message quotes where it stopped: for the first, the
        // rest of the password after its unquoted semicolon
        [InlineData("Server=127.0.0.1;Database=site_db;Uid=user;Pwd=pass;word", "can't be read")]
        [InlineData("Server=127.0.0.1;Database='site_db", "can't be read")]
        [InlineData("Server=127.0.0.1;Database=site_db;Uid", "can't be read")]
        // And a PlatformNotSupportedException for an option .NET doesn't have
        [InlineData("Server=127.0.0.1;Database=site_db;Use Performance Monitor=true", "can't be read")]
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
                .AddDatabaseHealthAccess()
                .AddBotStatusAccess()
                .AddRetentionAccess()
                .AddBotGuildAccess()
                .AddBotStatusReads()
                .AddBotGuildReads()
                .AddGuildSettingsReads()
                .AddBotRequestAccess()
                .AddBotRequestQueue()
                .AddDashboardChecks()
                .AddNotificationAccess()
                .AddPostingRetentionAccess()
                .AddGuildSwitchAccess()
                .AddDeliveryReads();

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

        // A request the bot would take as it stands, made at the time given
        static RequestFields Request(DateTime now) => RequestFields.New(RequestFields.NewKey(), RequestKinds.Find(RequestKinds.CachesClear)!, 1000000000000000001,
            "Test Admin", RequestRoles.Admin, null, null, null, now);

        // A transaction that has no connection, for a call that must stop before its first statement
        static DataTransaction NoTransaction() => new(null!, null!, 1, CancellationToken.None);

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
            var status = provider.GetRequiredService<IBotStatusData>();
            var retention = provider.GetRequiredService<IRetentionData>();
            var guilds = provider.GetRequiredService<IBotGuildData>();
            var statusReads = provider.GetRequiredService<IBotStatusReads>();
            var guildReads = provider.GetRequiredService<IBotGuildReads>();
            var requests = provider.GetRequiredService<IBotRequestData>();
            var queue = provider.GetRequiredService<IBotRequestQueue>();
            var reads = provider.GetRequiredService<IBotRequestReads>();
            var checks = provider.GetRequiredService<IDashboardChecks>();
            var store = provider.GetRequiredService<INotificationData>();
            var postingRetention = provider.GetRequiredService<IPostingRetentionData>();
            var switches = provider.GetRequiredService<IGuildSwitchData>();
            var deliveries = provider.GetRequiredService<IDeliveryReads>();
            provider.GetRequiredService<IGuildSettingsReads>();
            Assert.IsType<DatabaseHealthData>(provider.GetRequiredService<IDatabaseHealthData>());

            var now = DateTime.UtcNow;
            var day = now.Date;

            var calls = new Func<Task>[]
            {
                () => status.GetStatus(),
                () => status.SaveStatus(new BotStatus { started_at = now, heartbeat_at = now, discord_state = DiscordState.Connected, steam_state = SteamState.Offline }),
                () => status.AddEvents([new BotEvent { run_id = 1, seq = 1, at = now, kind = "bot.started", level = EventLevel.Info }]),
                () => status.SaveJobRuns([new JobRun { run_id = 1, seq = 1, job = "ArmorJob.AddColorArrays", started_at = now, result = JobResult.Ok }]),
                () => status.SaveJobStatus([new JobStatus { job = "ArmorJob.AddColorArrays", schedule = "0 * * * *", updated_at = now }]),
                () => status.DeleteOtherJobs(["ArmorJob.AddColorArrays"]),
                () => status.SaveSamples([new UptimeSample { minute = day }]),
                () => retention.DeleteAuditRows(now, 1),
                () => retention.ClearAuditAddresses(now, 1),
                () => retention.DeleteEvents(now, 1),
                () => retention.DeleteJobRuns(now, 1),
                () => retention.TotalUptimeDays(day.AddDays(-1), day),
                () => retention.DeleteSamples(day, 1),
                () => retention.DeleteSessions(now, 1),
                () => retention.DeleteGuildEvents(now, 1),
                () => guilds.ListGuildsIn(),
                () => guilds.SaveGuild(new BotGuild { guild_id = 1, name = "Test Server" }, [], now, 1, 1),
                () => guilds.LeaveGuild(1, now, 1, 1, null),
                () => guilds.MarkUnavailable([1], now),
                () => guilds.SaveMemberCount(1, 10, day, now),
                () => statusReads.GetStatus(),
                () => statusReads.ListEvents(new EventQuery()),
                () => statusReads.LatestEvent("discord.connected"),
                () => statusReads.LatestEventWith("request.rejected", ["signature didn't match"], now.AddDays(-1)),
                () => statusReads.FirstEventAfter("discord.disconnected", null),
                () => statusReads.CountEvents(["bot.started"], now.AddDays(-30), now.AddDays(-7)),
                () => statusReads.ListJobs(),
                () => statusReads.ListJobRuns(["ArmorJob.AddColorArrays"], 50),
                () => statusReads.ListUptimeDays(day.AddDays(-89), day),
                () => statusReads.CountSampleDays(day, now),
                () => statusReads.CountSamples(now.AddDays(-1), now),
                () => statusReads.FirstRecord(),
                () => guildReads.ListGuilds(),
                () => guildReads.GetGuild(1),
                () => guildReads.ListChannels(1),
                () => guildReads.FindChannels([1]),
                () => guildReads.ListGuildEvents(1, 50),
                () => guildReads.ListMemberDays(1, day.AddDays(-89), day.AddDays(1)),
                () => guildReads.MemberTrends(day.AddDays(-30)),
                () => audit.ListAudit(new AuditQuery()),
                () => audit.LatestChanges("blacklist", ["12345"]),
                () => audit.ListActionTimes(1, "switches.change", [AuditOutcome.Ok], now, 30),
                () => audit.AddAudit(new AuditEntry { at = DateTime.UtcNow, actor_role = AuditRole.Owner, action = "armor.approve", outcome = AuditOutcome.Ok }),
                () => reviews.ListReviews(1),
                () => reviews.AddReview(new ArmorReview { armor_id = 1, to_status = "Approved", reviewed_by = 1, reviewed_at = DateTime.UtcNow }),
                () => sessions.GetSession(new string('k', 43), DateTime.UtcNow),
                () => sessions.CreateSession(new string('k', 43), new DashboardSession { user_name = "Test User", ticket = [1] }),
                () => sessions.DeleteOldestSessions(1, 5),
                () => sessions.RunInTransaction(_ => Task.FromResult(0)),
                () => sessions.ListSessions(now, now.AddMinutes(-30)),
                // Each stops before its first statement, so a transaction with no connection is enough
                () => sessions.DeleteSessionByHash(new byte[DashboardSessionData.HashLength], NoTransaction()),
                () => sessions.DeleteSessionsBut(new string('k', 43), NoTransaction()),
                () => requests.ListOpenIds(1, 20, restartOnly: false),
                () => requests.GetRequest(1),
                () => requests.Decide(1, 1, RequestStates.Refused, "Refused", now),
                () => requests.Claim(1, 1, now),
                () => requests.Finish(1, 1, RequestStates.Done, "Done", null, now),
                () => requests.SetDm(1, RequestDmStates.Sent),
                () => requests.SweepOtherRuns(1, now, "Unknown", now),
                () => requests.CountClaims([RequestKinds.PostsPause, RequestKinds.PostsResume], now),
                () => requests.ListUntoldRestarts(1, now),
                () => requests.GetSettings(),
                () => requests.SavePause(true, "Testing", now, 1, 1, now),
                () => requests.SaveStatus(null, now, 1, 1, now),
                () => retention.DeleteRequests(now, 1),
                () => queue.GetRequest(1),
                () => queue.GetRequestByKey(new string('a', 32)),
                () => queue.GetSettings(),
                () => queue.RunInTransaction(_ => Task.FromResult(0)),
                // A request that passes every check gets as far as its SQL, which names the database
                () => queue.AddRequest(Request(now), new byte[RequestSigning.SignatureLength], NoTransaction()),
                () => reads.GetRequestView(1),
                () => reads.ListLatest([RequestKinds.CachesClear], 1),
                () => reads.ListClaimsSince(now.AddHours(-1)),
                () => reads.ListWaiting(now),
                () => reads.ListAskedBy(1, now.AddHours(-1)),
                () => reads.LatestDm(),
                () => reads.LatestRefusedAlert(),
                () => reads.CountAlertsSince(now.AddHours(-1)),
                () => checks.ListOwnRights(),
                () => checks.ListMigrations(),
                () => status.SaveLobbySamples([new LobbySample { minute = day }]),
                // The posting store, its clean-up, and the dashboard's switches and reads of the posts
                () => store.LoadAsync(new PostingLoad()),
                () => store.ListOpenAsync(),
                () => store.ListBaselinesAsync(),
                () => store.ListSeenAsync(PostSource.Submissions, [1]),
                () => store.ListHiddenAsync([1]),
                () => store.CountOlderBotRunsAsync("GuildNotificationJob.NotifyAllGuilds", now),
                () => store.SaveDetectionAsync(new Detection { At = now, ReopenArmor = [1] }),
                () => store.DropAsync([1], DeliveryReasons.TooOld, now),
                () => store.ClearFailingAsync(1, PostSource.Armor, now),
                () => store.ClaimAsync(new DeliveryClaim { AttemptKey = 1, RunStartedAt = now, GuildId = 1, ChannelId = 1, Source = PostSource.Armor, Part = 1, AttemptedAt = now,
                    QueueIds = [1] }),
                () => store.SettleAsync(new Settlement { AttemptKey = 1, Outcome = DeliveryState.InDoubt, At = now }),
                () => store.RecordRefusalAsync(new Refusal { AttemptKey = 1, RunStartedAt = now, GuildId = 1, ChannelId = 1, Source = PostSource.Armor, ItemCount = 1, At = now,
                    Reason = DeliveryReasons.NoChannel }),
                () => store.MarkCheckedAsync(1, 1),
                () => store.EndWaitsAsync(1, null, now),
                () => store.GetSwitchesAsync(1),
                () => store.ListMessageIdsAsync(1, 1, now),
                () => store.ListCarriedAsync(1),
                () => postingRetention.TotalDeliveryDays(day.AddDays(-1), day),
                () => postingRetention.DeleteDeliveries(now, 1),
                () => postingRetention.DeleteSettledQueueRows(now, 1),
                () => postingRetention.DeleteSeenLobbies(now, 1),
                () => postingRetention.DeleteLobbySamples(now, 1),
                () => switches.ListSwitches(),
                () => switches.GetSwitches(1),
                () => switches.RunInTransaction(_ => Task.FromResult(0)),
                () => switches.SaveSwitches(new SwitchChange { GuildId = 1, ArmorOn = false, At = now, ChangedBy = 1, Role = "owner" }, NoTransaction()),
                () => deliveries.ListStatuses([1]),
                () => deliveries.ListFailing(3),
                () => deliveries.ListRecentPosts(1, 25),
                () => deliveries.ListLastPosts(1),
                () => deliveries.GetItemPosts(PostSource.Armor, 1)
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

        // Every repository the dashboard uses, and the bot's status, history and servers, sends utf8mb4 whatever the
        // connection string says, so emoji and zero-width characters reach the tables and name checks as they are. The bot's
        // first two keep theirs, so what the bot already did is unchanged.
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
                new DashboardSessionData(factory, names),
                new BotStatusData(factory, names),
                new RetentionData(factory, names),
                new BotGuildData(factory, names),
                new BotStatusReads(factory, names),
                new BotGuildReads(factory, names),
                new GuildSettingsReads(factory),
                new BotRequestData(factory, names),
                new BotRequestQueue(factory, names),
                new DashboardChecks(factory, names),
                new NotificationData(factory, names),
                new PostingRetentionData(factory, names),
                new GuildSwitchData(factory, names),
                new DeliveryReads(factory, names)
            }, repository => Assert.Equal("utf8mb4", CharacterSet(repository, factory)));

            var configured = new MySqlConnectionStringBuilder(Site + setting).CharacterSet;
            Assert.Equal(configured, CharacterSet(new ArmorData(factory, cache, new CacheReset()), factory));
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
