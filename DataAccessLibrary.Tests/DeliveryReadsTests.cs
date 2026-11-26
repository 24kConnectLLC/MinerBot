using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using DataAccessLibrary.Tests.Database;
using MinerBot.Shared.Delivery;

namespace DataAccessLibrary.Tests
{
    // The dashboard's reads of the bot's posts, against rows as the bot writes them
    [Collection("Database")]
    public class DeliveryReadsTests(MariaDbFixture fixture)
    {
        static readonly DateTime Noon = new(2026, 11, 24, 12, 0, 0, 321, DateTimeKind.Utc);
        const ulong GuildA = 1000000000000000001, GuildB = 1000000000000000002, GuildC = 1000000000000000003, GuildD = 1000000000000000004;
        const ulong ChannelA = 2000000000000000001, ChannelB = 2000000000000000002;

        async Task<MariaDbFixture> StartAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.PostingTables);
            return db;
        }

        // A message's row as the bot writes it, and its new ID
        async Task<ulong> DeliveryAsync(ulong key, ulong guildId, DateTime at, string state = DeliveryState.Sent, ulong? messageId = null, ulong channelId = ChannelA,
            string source = PostSource.Armor)
        {
            await fixture.RootExecuteAsync(
                $@"INSERT INTO {MariaDbFixture.DeliveryTable} (attempt_key, run_id, run_started_at, guild_id, channel_id, source, part, item_count, attempted_at, state,
                   message_id, settled_at, checks)
                   VALUES (@key, 7, @at, @guildId, @channelId, @source, 1, 1, @at, @state, @messageId, @settled, 0)",
                new { key, guildId, channelId, source, at, state, messageId, settled = DeliveryState.IsOpen(state) ? (DateTime?)null : at });

            return (await fixture.RootQueryAsync<ulong>($"SELECT id FROM {MariaDbFixture.DeliveryTable} WHERE attempt_key = @key", new { key })).Single();
        }

        Task QueueAsync(ulong guildId, string source, ulong itemId, string state, ulong? deliveryId = null, string? reason = null) => fixture.RootExecuteAsync(
            $@"INSERT INTO {MariaDbFixture.QueueTable} (guild_id, source, item_id, revision, is_new, queued_at, state, delivery_id, reason, settled_at)
               VALUES (@guildId, @source, @itemId, 0, 1, @queued, @state, @deliveryId, @reason, @settled)",
            new { guildId, source, itemId, queued = Noon.AddMinutes(-5), state, deliveryId, reason, settled = state is QueueState.Sent or QueueState.Dropped ? (DateTime?)Noon : null });

        Task StatusAsync(ulong guildId, string source, uint failures, DateTime? failingSince = null, DateTime? nextTry = null) => fixture.RootExecuteAsync(
            $@"INSERT INTO {MariaDbFixture.DeliveryStatusTable} (guild_id, source, last_sent_at, failing_since, failures, next_try_at, updated_at)
               VALUES (@guildId, @source, @sent, @failingSince, @failures, @nextTry, @at)",
            new { guildId, source, sent = Noon.AddDays(-3), failingSince, failures, nextTry, at = Noon });

        // A server's 25 latest messages, newest first, the later one first of two at the same moment, and none of another
        // server's
        [Fact]
        public async Task RecentPostsAreNewestFirstAndCapped()
        {
            var db = await StartAsync();

            for (var i = 0; i < 30; i++)
                await DeliveryAsync((ulong)i + 1, GuildA, Noon.AddMinutes(i), messageId: 5000000000000000000 + (ulong)i);

            await DeliveryAsync(31, GuildA, Noon.AddMinutes(29), DeliveryState.InDoubt);
            await DeliveryAsync(32, GuildB, Noon.AddMinutes(40));

            var reads = db.Deliveries();
            var posts = await reads.ListRecentPosts(GuildA, 25);

            Assert.Equal(25, posts.Count);
            Assert.All(posts, post => Assert.Equal(GuildA, post.guild_id));
            Assert.Equal([31UL, 30, 29, 28], posts.Take(4).Select(post => post.attempt_key));
            Assert.Equal(Noon.AddMinutes(6), posts[^1].attempted_at);
            Assert.Equal((DeliveryState.Sent, (ulong?)5000000000000000029, ChannelA), (posts[1].state, posts[1].message_id, posts[1].channel_id));

            Assert.Equal(31, (await reads.ListRecentPosts(GuildA, 100)).Count);
            Assert.Empty(await reads.ListRecentPosts(GuildC, 25));
        }

        // A server's last post of each kind, however many of its other messages came after: the newest that went, the later of
        // two at the same moment, never one that failed, may not have arrived or is in doubt, and never another server's
        [Fact]
        public async Task LastPostOfEachKindIsItsNewestMessageThatWent()
        {
            var db = await StartAsync();

            await DeliveryAsync(1, GuildA, Noon.AddDays(-10), messageId: 5000000000000000001);
            await DeliveryAsync(2, GuildA, Noon.AddDays(-10), messageId: 5000000000000000002);
            await DeliveryAsync(3, GuildA, Noon.AddDays(-9), DeliveryState.Failed);
            await DeliveryAsync(4, GuildA, Noon.AddDays(-8), DeliveryState.Unconfirmed);
            await DeliveryAsync(5, GuildA, Noon.AddDays(-7), DeliveryState.InDoubt);
            await DeliveryAsync(6, GuildB, Noon.AddDays(-1), messageId: 5000000000000000006);
            await DeliveryAsync(7, GuildA, Noon.AddDays(-3), messageId: 5000000000000000007, source: PostSource.Maps);

            for (var i = 0; i < 30; i++)
                await DeliveryAsync(100 + (ulong)i, GuildA, Noon.AddMinutes(i), messageId: 5000000000000000100 + (ulong)i, source: PostSource.Lobbies);

            var reads = db.Deliveries();

            Assert.Equal([(PostSource.Armor, 2UL, (ulong?)5000000000000000002, Noon.AddDays(-10)), (PostSource.Lobbies, 129, 5000000000000000129, Noon.AddMinutes(29)),
                (PostSource.Maps, 7, 5000000000000000007, Noon.AddDays(-3))],
                (await reads.ListLastPosts(GuildA)).OrderBy(post => post.source, StringComparer.Ordinal).Select(post => (post.source, post.attempt_key, post.message_id, post.attempted_at)));
            Assert.DoesNotContain(await reads.ListRecentPosts(GuildA, 25), post => post.source == PostSource.Armor);
            Assert.Equal([6UL], (await reads.ListLastPosts(GuildB)).Select(post => post.attempt_key));
            Assert.Empty(await reads.ListLastPosts(GuildC));
        }

        // Where an armor went: each server's row, with the message that carried or holds it and the server's next try, and the
        // bot's seen row. Not another kind's item of the same number, nor another armor's. The same in a transaction.
        [Fact]
        public async Task WhereAnArmorWentJoinsTheQueueToTheDeliveries()
        {
            var db = await StartAsync();

            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.SeenTable} (source, item_id, state, first_seen_at, last_seen_at) VALUES ('armor', 12, 'queued', @at, @at)",
                new { at = Noon.AddMinutes(-5) });

            // Another server's message first, so no message's ID is a queued item's
            await DeliveryAsync(9, GuildD, Noon.AddMinutes(-30), DeliveryState.Failed);

            var sent = await DeliveryAsync(1, GuildA, Noon, messageId: 5000000000000000001);
            var doubt = await DeliveryAsync(2, GuildB, Noon, DeliveryState.InDoubt, channelId: ChannelB);

            await QueueAsync(GuildA, PostSource.Armor, 12, QueueState.Sent, sent);
            await QueueAsync(GuildB, PostSource.Armor, 12, QueueState.Sending, doubt);
            await QueueAsync(GuildC, PostSource.Armor, 12, QueueState.Pending);
            await QueueAsync(GuildD, PostSource.Armor, 12, QueueState.Dropped, reason: DeliveryReasons.SwitchedOff);
            await QueueAsync(GuildA, PostSource.Maps, 12, QueueState.Pending);
            await QueueAsync(GuildA, PostSource.Armor, 13, QueueState.Pending);
            await StatusAsync(GuildC, PostSource.Armor, 2, Noon.AddHours(-1), Noon.AddHours(1));
            await StatusAsync(GuildC, PostSource.Maps, 4, Noon.AddHours(-1), Noon.AddHours(6));

            var reads = db.Deliveries();
            var posts = await reads.GetItemPosts(PostSource.Armor, 12);

            Assert.Equal((SeenState.Queued, Noon.AddMinutes(-5)), (posts.Seen!.state, posts.Seen.first_seen_at));
            Assert.Equal(
            [
                (GuildA, QueueState.Sent, (string?)null, (string?)DeliveryState.Sent, (ulong?)ChannelA, (ulong?)5000000000000000001, (DateTime?)Noon, (DateTime?)null),
                (GuildB, QueueState.Sending, null, DeliveryState.InDoubt, ChannelB, null, Noon, null),
                (GuildC, QueueState.Pending, null, null, null, null, null, Noon.AddHours(1)),
                (GuildD, QueueState.Dropped, DeliveryReasons.SwitchedOff, null, null, null, null, null)
            ], posts.Servers.Select(row => (row.guild_id, row.state, row.reason, row.delivery_state, row.channel_id, row.message_id, row.attempted_at, row.next_try_at)));
            Assert.Equal(Noon, posts.Servers[0].settled_at);

            Assert.Equal(posts, await db.Sessions().RunInTransaction(transaction => reads.GetItemPosts(PostSource.Armor, 12, transaction)), new PostsComparer());

            var none = await reads.GetItemPosts(PostSource.Armor, 99);
            Assert.Null(none.Seen);
            Assert.Empty(none.Servers);
        }

        // Each listed server's statuses, a row a kind, and the ones whose posts keep failing, the longest failing first
        [Fact]
        public async Task StatusesReadOneRowPerServerAndKind()
        {
            var db = await StartAsync();

            await StatusAsync(GuildA, PostSource.Armor, 0);
            await StatusAsync(GuildA, PostSource.Maps, 3, Noon.AddHours(-2), Noon.AddHours(1));
            await StatusAsync(GuildB, PostSource.Lobbies, 5, Noon.AddDays(-1), Noon.AddHours(6));
            await StatusAsync(GuildC, PostSource.Armor, 2, Noon.AddHours(-1), Noon.AddMinutes(15));

            var reads = db.Deliveries();

            Assert.Equal([(GuildA, PostSource.Armor, 0u), (GuildA, PostSource.Maps, 3u), (GuildB, PostSource.Lobbies, 5u)],
                (await reads.ListStatuses([GuildB, GuildA, GuildD])).Select(row => (row.guild_id, row.source, row.failures)));
            Assert.Empty(await reads.ListStatuses([]));

            var maps = (await reads.ListStatuses([GuildA]))[1];
            Assert.Equal(((DateTime?)Noon.AddHours(-2), (DateTime?)Noon.AddHours(1), (DateTime?)Noon.AddDays(-3), Noon), (maps.failing_since, maps.next_try_at, maps.last_sent_at, maps.updated_at));

            Assert.Equal([(GuildB, PostSource.Lobbies), (GuildA, PostSource.Maps)], (await reads.ListFailing(3)).Select(row => (row.guild_id, row.source)));
            Assert.Equal([GuildB, GuildA, GuildC], (await reads.ListFailing(1)).Select(row => row.guild_id));
        }

        // Two reads of where an item went are the same when their seen rows and their servers' rows are
        sealed class PostsComparer : IEqualityComparer<ItemPosts>
        {
            public bool Equals(ItemPosts? x, ItemPosts? y) => x!.Seen == y!.Seen && x.Servers.SequenceEqual(y.Servers);

            public int GetHashCode(ItemPosts obj) => obj.Servers.Count;
        }
    }
}
