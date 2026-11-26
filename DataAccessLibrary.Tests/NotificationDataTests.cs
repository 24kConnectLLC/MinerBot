using DataAccessLibrary.Guilds;
using DataAccessLibrary.Guilds.Models;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using DataAccessLibrary.Tests.Database;
using MinerBot.Shared.Delivery;
using MySql.Data.MySqlClient;
using System.Security.Cryptography;

namespace DataAccessLibrary.Tests
{
    // The posting store in MariaDB: what only a real database shows. Two processes on one database can't both claim a row,
    // a write that's repeated, at the same moment too, lands once, a sent row is never reopened, a map update put back
    // waits on no other server's row, the rows that wait come oldest first within their limit, and the bot's queries need
    // nothing but MinerBot's own database.
    // NotificationStoreContractTests holds the rules both stores keep.
    [Collection("Database")]
    public class NotificationDataTests(MariaDbFixture fixture)
    {
        static readonly DateTime Noon = new(2026, 11, 24, 12, 0, 0, 456, DateTimeKind.Utc);
        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
        const ulong RunId = 18446744073709551615;
        const ulong GuildA = 1000000000000000001, GuildB = 1000000000000000002;
        const ulong ChannelA = 2000000000000000001;

        async Task<MariaDbFixture> StartAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync([.. MariaDbFixture.PostingTables, MariaDbFixture.JobRunTable, MariaDbFixture.BotGuildTable, MariaDbFixture.BotChannelTable,
                MariaDbFixture.GuildEventTable, MariaDbFixture.StatusTable, MariaDbFixture.SampleTable]);
            return db;
        }

        async Task<long> CountAsync(string table) => (await fixture.RootQueryAsync<long>($"SELECT COUNT(*) FROM {table}")).Single();

        Task<List<(ulong Id, string State, string? Reason, ulong? DeliveryId)>> QueueAsync() =>
            fixture.RootQueryAsync<(ulong, string, string?, ulong?)>($"SELECT id, state, reason, delivery_id FROM {MariaDbFixture.QueueTable} ORDER BY id");

        static QueueItem Queued(ulong guildId, string source, ulong id, DateTime at, ulong revision = 0) => new()
        {
            guild_id = guildId,
            source = source,
            item_id = id,
            revision = revision,
            is_new = true,
            queued_at = at
        };

        static DeliveryClaim Claim(ulong key, ulong guildId, string source, IReadOnlyList<ulong> ids, DateTime at) => new()
        {
            AttemptKey = key,
            RunId = RunId,
            RunStartedAt = at,
            GuildId = guildId,
            ChannelId = ChannelA,
            Source = source,
            Part = 1,
            AttemptedAt = at,
            QueueIds = ids
        };

        static Settlement Failed(ulong key, DateTime at) => new()
        {
            AttemptKey = key,
            Outcome = DeliveryState.Failed,
            At = at,
            Reason = DeliveryReasons.DiscordPermissions,
            HttpStatus = 403,
            DiscordCode = 50013,
            Counts = true,
            NextTryAt = at.AddMinutes(5),
            WaitingOn = 9
        };

        static async Task<ulong> IdOf(NotificationData store, ulong guildId, string source, ulong itemId) =>
            (await store.ListOpenAsync()).Single(row => row.guild_id == guildId && row.source == source && row.item_id == itemId).id;

        // Saved again, and twice at the same moment, as a write that timed out and a later run's might be: one row each, as
        // written
        [Fact]
        public async Task DetectionIsSafeToRepeat()
        {
            var db = await StartAsync();
            var store = db.Notifications();

            var detection = new Detection
            {
                At = Noon,
                Baselines = [new BaselineRow { source = PostSource.Maps, taken_at = Noon, taken_by_run = RunId, reason = BaselineReason.First, item_count = 20,
                    baseline_time = Noon.AddDays(-30), checked_at = Noon, checked_by_run = RunId }],
                Seen = [.. Enumerable.Range(1, 150).Select(id => new SeenRow { source = PostSource.Maps, item_id = (ulong)id, state = SeenState.Baseline, first_seen_at = Noon,
                    last_seen_at = Noon, updated_at = Noon.AddDays(-id) })],
                Queue = [.. Enumerable.Range(1, 120).Select(id => Queued(GuildA + (ulong)(id % 3), PostSource.Maps, (ulong)id, Noon, revision: 1764000000 + (ulong)id))]
            };

            await store.SaveDetectionAsync(detection);
            await store.SaveDetectionAsync(detection);
            await Task.WhenAll(store.SaveDetectionAsync(detection), db.Notifications().SaveDetectionAsync(detection)).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.Equal(1, await CountAsync(MariaDbFixture.BaselineTable));
            Assert.Equal(150, await CountAsync(MariaDbFixture.SeenTable));
            Assert.Equal(120, await CountAsync(MariaDbFixture.QueueTable));

            var state = await store.LoadAsync(new PostingLoad());
            Assert.Equal(detection.Baselines, state.Baselines);
            Assert.Equal(detection.Seen, await store.ListSeenAsync(PostSource.Maps, [.. Enumerable.Range(1, 150).Select(id => (ulong)id)]));
            Assert.Equal(120, state.Open.Count);
        }

        // Two processes claim the same rows at the same moment: one takes them all, the other none, and only the winner's
        // message is recorded. Fails for a claim that isn't conditional on the rows being pending.
        [Fact]
        public async Task ClaimTakesOnlyPendingRows()
        {
            var db = await StartAsync();
            var first = db.Notifications();
            var second = db.Notifications();

            await first.SaveDetectionAsync(new Detection { At = Noon, Queue = [Queued(GuildA, PostSource.Armor, 11, Noon), Queued(GuildA, PostSource.Armor, 12, Noon)] });
            ulong[] ids = [.. (await first.ListOpenAsync()).Select(row => row.id)];

            await using var holder = new MySqlConnection(db.RootConnection);
            await holder.OpenAsync(TestContext.Current.CancellationToken).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            await RawAsync(holder, "START TRANSACTION");
            await RawAsync(holder, $"SELECT id FROM {MariaDbFixture.QueueTable} WHERE id IN ({ids[0]}, {ids[1]}) FOR UPDATE");

            var claims = new[]
            {
                Settled(first.ClaimAsync(Claim(1, GuildA, PostSource.Armor, ids, Noon))),
                Settled(second.ClaimAsync(Claim(2, GuildA, PostSource.Armor, ids, Noon)))
            };

            try
            {
                Assert.Equal(2, await fixture.LockWaitsAsync("notification_queue", 2, WaitLimit));
            }
            finally
            {
                await RawAsync(holder, "COMMIT");
            }

            var results = await Task.WhenAll(claims).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Single(results, result => result.Id != null);
            Assert.Single(results, result => result.Failure is ClaimLostException);

            var winner = results.Single(result => result.Id != null).Id;
            Assert.Equal(1, await CountAsync(MariaDbFixture.DeliveryTable));
            Assert.All(await QueueAsync(), row => Assert.Equal((QueueState.Sending, winner), (row.State, row.DeliveryId)));
        }

        // A claim whose rows aren't all pending takes none of them, and leaves no message behind
        [Fact]
        public async Task ClaimThatLosesARowCommitsNothing()
        {
            var db = await StartAsync();
            var store = db.Notifications();

            await store.SaveDetectionAsync(new Detection { At = Noon, Queue = [Queued(GuildA, PostSource.Armor, 11, Noon), Queued(GuildA, PostSource.Armor, 12, Noon)] });
            var a11 = await IdOf(store, GuildA, PostSource.Armor, 11);
            var a12 = await IdOf(store, GuildA, PostSource.Armor, 12);

            await store.ClaimAsync(Claim(1, GuildA, PostSource.Armor, [a12], Noon));
            await Assert.ThrowsAsync<ClaimLostException>(() => store.ClaimAsync(Claim(2, GuildA, PostSource.Armor, [a11, a12], Noon.AddSeconds(1))));

            Assert.Equal([1UL], await fixture.RootQueryAsync<ulong>($"SELECT attempt_key FROM {MariaDbFixture.DeliveryTable}"));
            Assert.Equal((QueueState.Pending, (ulong?)null), (await QueueAsync()).Where(row => row.Id == a11).Select(row => (row.State, row.DeliveryId)).Single());
        }

        // A settle written again, by the next run as one the database may not have taken, or at the same moment by another
        // caller, lands once: the status counts the failure once, and the rows go back to pending once
        [Fact]
        public async Task SettleIsSafeToRepeat()
        {
            var db = await StartAsync();
            var store = db.Notifications();

            await store.SaveDetectionAsync(new Detection { At = Noon, Queue = [Queued(GuildA, PostSource.Armor, 11, Noon)] });
            var a11 = await IdOf(store, GuildA, PostSource.Armor, 11);

            await store.ClaimAsync(Claim(1, GuildA, PostSource.Armor, [a11], Noon));

            var settles = await Task.WhenAll(store.SettleAsync(Failed(1, Noon.AddSeconds(1))), db.Notifications().SettleAsync(Failed(1, Noon.AddSeconds(1))))
                .WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            Assert.Single(settles, settled => settled);
            Assert.False(await store.SettleAsync(Failed(1, Noon.AddSeconds(2))));

            var status = Assert.Single((await store.LoadAsync(new PostingLoad())).Statuses);
            Assert.Equal((1u, (DateTime?)Noon, (DateTime?)Noon.AddSeconds(1).AddMinutes(5)), (status.failures, status.failing_since, status.next_try_at));
            Assert.Equal([(a11, QueueState.Pending, (string?)null, (ulong?)null)], await QueueAsync());

            // And a message that went: its ID once, the same whatever a later write says
            await store.ClaimAsync(Claim(2, GuildA, PostSource.Armor, [a11], Noon.AddMinutes(5)));
            var sent = new Settlement { AttemptKey = 2, Outcome = DeliveryState.Sent, At = Noon.AddMinutes(5), MessageId = 5000000000000000001 };
            Assert.True(await store.SettleAsync(sent));
            Assert.False(await store.SettleAsync(new Settlement { AttemptKey = 2, Outcome = DeliveryState.Sent, At = Noon.AddMinutes(6), MessageId = 5000000000000000002 }));

            Assert.Equal([(ulong?)5000000000000000001], await fixture.RootQueryAsync<ulong?>($"SELECT message_id FROM {MariaDbFixture.DeliveryTable} WHERE attempt_key = 2"));
            Assert.Equal(0u, Assert.Single((await store.LoadAsync(new PostingLoad())).Statuses).failures);
        }

        // A row that was sent stays sent, whatever comes after it: the armor approved again, a drop, a claim, a settle of its
        // message as failed, the detection that queued it saved again, and its server's leave
        [Fact]
        public async Task SentRowsAreNeverReopened()
        {
            var db = await StartAsync();
            var store = db.Notifications();
            var detection = new Detection { At = Noon, Queue = [Queued(GuildA, PostSource.Armor, 11, Noon)] };

            await db.BotGuilds().SaveGuild(new BotGuild { guild_id = GuildA, name = "Test Server", available = true }, [], Noon, RunId, 1);
            await store.SaveDetectionAsync(detection);
            var a11 = await IdOf(store, GuildA, PostSource.Armor, 11);

            await store.ClaimAsync(Claim(1, GuildA, PostSource.Armor, [a11], Noon));
            Assert.True(await store.SettleAsync(new Settlement { AttemptKey = 1, Outcome = DeliveryState.Sent, At = Noon.AddSeconds(1), MessageId = 5000000000000000001 }));

            await store.SaveDetectionAsync(new Detection { At = Noon.AddDays(1), ReopenArmor = [11] });
            Assert.Equal(0, await store.DropAsync([a11], DeliveryReasons.NotApproved, Noon.AddDays(1)));
            await Assert.ThrowsAsync<ClaimLostException>(() => store.ClaimAsync(Claim(2, GuildA, PostSource.Armor, [a11], Noon.AddDays(1))));
            Assert.False(await store.SettleAsync(Failed(1, Noon.AddDays(1))));
            await store.SaveDetectionAsync(detection);
            Assert.True(await db.BotGuilds().LeaveGuild(GuildA, Noon.AddDays(2), RunId, 2, null));

            Assert.Equal([(a11, QueueState.Sent, (string?)null)], (await QueueAsync()).Select(row => (row.Id, row.State, row.Reason)));
        }

        // Only armor's rows dropped because it wasn't approved any more go back, queued from the re-approval, and with no
        // message: not another drop reason's, and not another kind's
        [Fact]
        public async Task OnlyWithdrawnArmorIsReopened()
        {
            var db = await StartAsync();
            var store = db.Notifications();
            var reasons = DeliveryReasons.Drops;

            await store.SaveDetectionAsync(new Detection
            {
                At = Noon,
                Queue = [.. reasons.Select((_, i) => Queued(GuildA + (ulong)i, PostSource.Armor, 11, Noon)), Queued(GuildA, PostSource.Maps, 11, Noon),
                    Queued(GuildA, PostSource.Lobbies, 11, Noon)]
            });

            var open = await store.ListOpenAsync();

            for (var i = 0; i < reasons.Count; i++)
                Assert.Equal(1, await store.DropAsync([open.Single(row => row.source == PostSource.Armor && row.guild_id == GuildA + (ulong)i).id], reasons[i], Noon.AddMinutes(1)));

            await store.DropAsync([.. open.Where(row => row.source != PostSource.Armor).Select(row => row.id)], DeliveryReasons.NotApproved, Noon.AddMinutes(1));

            Assert.Equal([GuildA], (await store.LoadAsync(new PostingLoad { ArmorIds = [11] })).Withdrawn.Select(row => row.guild_id));

            await store.SaveDetectionAsync(new Detection { At = Noon.AddDays(1), ReopenArmor = [11] });

            var reopened = Assert.Single(await store.ListOpenAsync());
            Assert.Equal((GuildA, PostSource.Armor, Noon.AddDays(1), (ulong?)null, (string?)null, (DateTime?)null),
                (reopened.guild_id, reopened.source, reopened.queued_at, reopened.delivery_id, reopened.reason, reopened.settled_at));
            Assert.Equal(reasons.Count + 1, (await QueueAsync()).Count(row => row.State == QueueState.Dropped));
        }

        // A detection that puts back one server's map update, dropped while that server wasn't owed maps, reads and holds that
        // map's rows only, as it does for two or more, so it's saved while a write holds another server's dropped row, as the
        // clean-up's delete does, and holds up no such write. Fails for the rows named by their list alone: for a list of one
        // row, MariaDB's UPDATE finds no index in it and reads every server's dropped rows, holding each until the commit.
        [Fact]
        public async Task PutBackOfOneUpdateWaitsOnNoOtherServersRow()
        {
            var db = await StartAsync();
            var store = db.Notifications();
            const ulong Revision = 1764000000;

            Detection Update(DateTime at) => new() { At = at, Queue = [Queued(GuildA, PostSource.Maps, 21, at, revision: Revision) with { is_new = false }] };

            await store.SaveDetectionAsync(Update(Noon));
            await store.SaveDetectionAsync(new Detection { At = Noon, Queue = [.. Enumerable.Range(31, 3).Select(id => Queued(GuildB, PostSource.Lobbies, (ulong)id, Noon))] });

            var open = await store.ListOpenAsync();
            var a21 = open.Single(row => row.source == PostSource.Maps).id;
            var b31 = open.Single(row => row.item_id == 31).id;
            Assert.Equal(1, await store.DropAsync([a21], DeliveryReasons.SwitchedOff, Noon.AddMinutes(1)));
            Assert.Equal(3, await store.DropAsync([.. open.Where(row => row.source == PostSource.Lobbies).Select(row => row.id)], DeliveryReasons.Closed, Noon.AddMinutes(1)));

            await using var holder = new MySqlConnection(db.RootConnection);
            await holder.OpenAsync(TestContext.Current.CancellationToken).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            await RawAsync(holder, "START TRANSACTION");
            await RawAsync(holder, $"SELECT id FROM {MariaDbFixture.QueueTable} WHERE id = {b31} FOR UPDATE");

            var saved = store.SaveDetectionAsync(Update(Noon.AddDays(1)));

            try
            {
                await saved.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            }
            finally
            {
                await RawAsync(holder, "COMMIT");
            }

            var back = Assert.Single(await store.ListOpenAsync());
            Assert.Equal((a21, Noon.AddDays(1), (string?)null), (back.id, back.queued_at, back.reason));
            Assert.Equal(3, (await QueueAsync()).Count(row => row.State == QueueState.Dropped && row.Reason == DeliveryReasons.Closed));
        }

        // At most 5,000 rows that wait, the oldest first and in the order they were queued on a tie, and none that isn't
        // pending, so the groups a run doesn't reach go first next time
        [Fact]
        public async Task OpenRowsAreBoundedAndOldestFirst()
        {
            var db = await StartAsync();
            var store = db.Notifications();

            // MariaDB's sequence table makes many rows in one statement: the first is the newest, a second apart
            await db.RootExecuteAsync(
                $@"INSERT INTO {MariaDbFixture.QueueTable} (guild_id, source, item_id, revision, is_new, queued_at, state)
                   SELECT @guild, 'lobbies', seq, 0, 1, TIMESTAMPADD(SECOND, 6000 - seq, @noon), 'pending' FROM {MariaDbFixture.BotDatabase}.seq_1_to_5001",
                new { guild = GuildA, noon = Noon });

            // Older than all of them, and not waiting
            await db.RootExecuteAsync(
                $@"INSERT INTO {MariaDbFixture.QueueTable} (guild_id, source, item_id, revision, is_new, queued_at, state, settled_at)
                   VALUES (@guild, 'armor', 1, 0, 1, @noon, 'sent', @noon), (@guild, 'armor', 2, 0, 1, @noon, 'dropped', @noon), (@guild, 'armor', 3, 0, 1, @noon, 'sending', NULL)",
                new { guild = GuildB, noon = Noon });

            // Two queued at the same moment as the oldest of the 5,001, after them
            await store.SaveDetectionAsync(new Detection { At = Noon, Queue = [Queued(GuildB, PostSource.Maps, 2, Noon.AddSeconds(999)), Queued(GuildB, PostSource.Maps, 1, Noon.AddSeconds(999))] });

            var open = await store.ListOpenAsync();

            Assert.Equal(NotificationData.OpenLimit, open.Count);
            Assert.All(open, row => Assert.Equal(QueueState.Pending, row.state));
            Assert.Equal([5001UL, 2, 1, 5000, 4999], open.Take(5).Select(row => row.item_id));
            Assert.Equal(open.OrderBy(row => row.queued_at).ThenBy(row => row.id).Select(row => row.id), open.Select(row => row.id));
            Assert.Equal(Noon.AddSeconds(5996), open[^1].queued_at);

            Assert.Equal(open, (await store.LoadAsync(new PostingLoad())).Open);
        }

        // A refresh of one server ends its waits, every kind's or one channel's, and leaves another server's
        [Fact]
        public async Task EndWaitsClearsOnlyThatServer()
        {
            var db = await StartAsync();
            var store = db.Notifications();

            Refusal Refusal(ulong key, ulong guildId, string source, ulong channelId) => new()
            {
                AttemptKey = key,
                RunId = RunId,
                RunStartedAt = Noon,
                GuildId = guildId,
                ChannelId = channelId,
                Source = source,
                ItemCount = 1,
                At = Noon,
                Reason = DeliveryReasons.NoChannel,
                NextTryAt = Noon.AddHours(1),
                WaitingOn = 5
            };

            await store.RecordRefusalAsync(Refusal(1, GuildA, PostSource.Armor, ChannelA));
            await store.RecordRefusalAsync(Refusal(2, GuildA, PostSource.Maps, ChannelA + 1));
            await store.RecordRefusalAsync(Refusal(3, GuildB, PostSource.Armor, ChannelA));

            Assert.Equal(1, await store.EndWaitsAsync(GuildA, ChannelA + 1, Noon.AddMinutes(1)));
            Assert.Equal([(GuildA, PostSource.Armor, true), (GuildA, PostSource.Maps, false), (GuildB, PostSource.Armor, true)],
                (await store.LoadAsync(new PostingLoad())).Statuses.Select(row => (row.guild_id, row.source, row.next_try_at != null && row.waiting_on != null)));

            Assert.Equal(1, await store.EndWaitsAsync(GuildA, null, Noon.AddMinutes(2)));

            var statuses = (await store.LoadAsync(new PostingLoad())).Statuses;
            Assert.Equal([(GuildA, false, 1u), (GuildA, false, 1u), (GuildB, true, 1u)], statuses.Select(row => (row.guild_id, row.next_try_at != null, row.failures)));
            Assert.Equal(Noon.AddHours(1), statuses[2].next_try_at);
        }

        // Every call the bot makes for its posts, its new-submission alerts, its lobby samples and its posts' clean-up, as a
        // login with the bot's rights in MinerBot's database and none in the website's, which the bot's own login is. The
        // tests' usual login has rights in both, so a query that reached a website table would pass everywhere but here
        // and on the server.
        [Fact]
        public async Task BotPostingQueriesNeedOnlyTheBotDatabase()
        {
            var db = await StartAsync();
            const string Login = "minerbot_bot_only";
            const string Grantee = "'" + Login + "'@'%'";
            var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

            await db.RootExecuteAsync($"DROP USER IF EXISTS {Grantee}");
            await db.RootExecuteAsync($"CREATE USER {Grantee} IDENTIFIED BY '{password}'");
            await db.RootExecuteAsync($"GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX, DROP, REFERENCES ON {MariaDbFixture.BotDatabase}.* TO {Grantee}");

            try
            {
                // Never pooled, so nothing of it stays open once it's dropped. The website's database has a name, as the bot
                // knows it, so SQL that named one of its tables would reach MariaDB and be refused.
                var bot = new MySqlConnectionStringBuilder(db.ConnectionAs(Login, password, MariaDbFixture.BotDatabase)) { Pooling = false }.ConnectionString;
                var names = new DatabaseNames(new Dictionary<DBConnectionName, string> { [DBConnectionName.Bot] = bot, [DBConnectionName.Guild] = db.SiteConnection,
                    [DBConnectionName.Armor] = db.SiteConnection, [DBConnectionName.Feed] = db.SiteConnection });
                var factory = new SQLConnectionFactory(new Dictionary<DBConnectionName, string> { [DBConnectionName.Bot] = bot });

                var store = new NotificationData(factory, names);
                var retention = new PostingRetentionData(factory, names);
                var status = new BotStatusData(factory, names);
                var guilds = new BotGuildData(factory, names);
                var day = Noon.Date;
                var failures = new List<string>();

                async Task Run(string what, Func<Task> call)
                {
                    try
                    {
                        await call();
                    }
                    catch (MySqlException e)
                    {
                        failures.Add($"{what}: MySQL error {e.Number}");
                    }
                }

                await Run("SaveGuild", () => guilds.SaveGuild(new BotGuild { guild_id = GuildB, name = "Test Server", available = true }, [], Noon, RunId, 1));
                await Run("SaveDetectionAsync", () => store.SaveDetectionAsync(new Detection
                {
                    At = Noon,
                    Baselines = [new BaselineRow { source = PostSource.Submissions, taken_at = Noon, taken_by_run = RunId, reason = BaselineReason.First, high_id = 40,
                        alerted_at = Noon, checked_at = Noon, checked_by_run = RunId }],
                    Seen = [new SeenRow { source = PostSource.Submissions, item_id = 41, state = SeenState.Alerted, first_seen_at = Noon, last_seen_at = Noon }],
                    Queue = [Queued(GuildA, PostSource.Armor, 11, Noon), Queued(GuildA, PostSource.Armor, 12, Noon), Queued(GuildB, PostSource.Lobbies, 13, Noon),
                        Queued(GuildB, PostSource.Maps, 21, Noon, revision: 1764000000) with { is_new = false }],
                    ReopenArmor = [11],
                    ReopenMaps = [21]
                }));
                await Run("LoadAsync", () => store.LoadAsync(new PostingLoad { ArmorIds = [11, 12], LobbyIds = [13] }));
                await Run("ListHiddenAsync", () => store.ListHiddenAsync([21]));
                await Run("ListBaselinesAsync", () => store.ListBaselinesAsync());
                await Run("ListSeenAsync", () => store.ListSeenAsync(PostSource.Submissions, [41]));
                await Run("CountOlderBotRunsAsync", () => store.CountOlderBotRunsAsync("GuildNotificationJob.NotifyAllGuilds", Noon.AddDays(-1)));
                await Run("GetSwitchesAsync", () => store.GetSwitchesAsync(GuildA));
                await Run("ListMessageIdsAsync", () => store.ListMessageIdsAsync(GuildA, ChannelA, Noon.AddDays(-1)));

                Assert.Empty(failures);
                var ids = (await store.ListOpenAsync()).Where(row => row.guild_id == GuildA).Select(row => row.id).ToArray();
                Assert.Equal(2, ids.Length);

                await Run("ClaimAsync", () => store.ClaimAsync(Claim(1, GuildA, PostSource.Armor, ids[..1], Noon)));
                await Run("SettleAsync as sent", () => store.SettleAsync(new Settlement { AttemptKey = 1, Outcome = DeliveryState.Sent, At = Noon, MessageId = 5000000000000000001 }));
                await Run("ClaimAsync again", () => store.ClaimAsync(Claim(2, GuildA, PostSource.Armor, ids[1..], Noon)));
                await Run("SettleAsync in doubt", () => store.SettleAsync(new Settlement { AttemptKey = 2, Outcome = DeliveryState.InDoubt, At = Noon }));
                await Run("ListCarriedAsync", () => store.ListCarriedAsync(2));
                await Run("MarkCheckedAsync", () => store.MarkCheckedAsync(2, 1));
                await Run("SettleAsync as failed", () => store.SettleAsync(Failed(2, Noon.AddMinutes(1))));
                await Run("RecordRefusalAsync", () => store.RecordRefusalAsync(new Refusal { AttemptKey = 3, RunId = RunId, RunStartedAt = Noon, GuildId = GuildA,
                    ChannelId = ChannelA, Source = PostSource.Maps, ItemCount = 1, At = Noon, Reason = DeliveryReasons.NoChannel, NextTryAt = Noon.AddMinutes(5) }));
                await Run("EndWaitsAsync", () => store.EndWaitsAsync(GuildA, ChannelA, Noon));
                await Run("ClearFailingAsync", () => store.ClearFailingAsync(GuildA, PostSource.Armor, Noon));
                await Run("DropAsync", () => store.DropAsync([.. ids], DeliveryReasons.TooOld, Noon.AddDays(8)));
                await Run("LeaveGuild", () => guilds.LeaveGuild(GuildB, Noon.AddDays(8), RunId, 2, null));
                await Run("SaveStatus", () => status.SaveStatus(new BotStatus { run_id = RunId, started_at = Noon, heartbeat_at = Noon, discord_state = DiscordState.Connected,
                    steam_state = SteamState.LoggedOn }));
                await Run("SaveSamples", () => status.SaveSamples([new UptimeSample { minute = day, discord_up = true }]));
                await Run("SaveLobbySamples", () => status.SaveLobbySamples([new LobbySample { minute = day, steam_answered = false }]));
                await Run("TotalDeliveryDays", () => retention.TotalDeliveryDays(day.AddDays(-90), day.AddDays(9)));
                await Run("DeleteDeliveries", () => retention.DeleteDeliveries(Noon.AddDays(100), 10));
                await Run("DeleteSettledQueueRows", () => retention.DeleteSettledQueueRows(Noon.AddDays(100), 10));
                await Run("DeleteSeenLobbies", () => retention.DeleteSeenLobbies(Noon.AddDays(100), 10));
                await Run("DeleteLobbySamples", () => retention.DeleteLobbySamples(Noon.AddDays(100), 10));

                // Armor's rows stay, as where each armor went
                Assert.Empty(failures);
                Assert.Equal([PostSource.Armor, PostSource.Armor], await fixture.RootQueryAsync<string>($"SELECT source FROM {MariaDbFixture.QueueTable} ORDER BY id"));
                Assert.Equal(0, await CountAsync(MariaDbFixture.DeliveryTable));
            }
            finally
            {
                await db.RootExecuteAsync($"DROP USER IF EXISTS {Grantee}");
            }
        }

        // What a claim did, without throwing, so two at once can be awaited together
        static async Task<(ulong? Id, Exception? Failure)> Settled(Task<ulong> claim)
        {
            try
            {
                return (await claim, null);
            }
            catch (Exception e)
            {
                return (null, e);
            }
        }

        static async Task RawAsync(MySqlConnection connection, string sql)
        {
            await using var command = new MySqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }
}
