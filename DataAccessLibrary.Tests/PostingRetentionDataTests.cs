using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using DataAccessLibrary.Tests.Database;
using MinerBot.Shared.Delivery;

namespace DataAccessLibrary.Tests
{
    // The nightly clean-up of the bot's posts' records, each rule with rows just either side of its cutoff, which stay when
    // they're exactly at it, and the rows still open, which never go. The daily totals are worked out again whole.
    [Collection("Database")]
    public class PostingRetentionDataTests(MariaDbFixture fixture)
    {
        static readonly DateTime Cutoff = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
        static readonly DateTime Today = new(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);
        static readonly DateTime KeepFrom = Today.AddDays(-90);
        const int Chunk = PostingRetentionData.ChunkLimit;
        const ulong GuildId = 1000000000000000001;

        async Task<MariaDbFixture> StartAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.PostingTables);
            return db;
        }

        uint key;

        Task DeliveryAsync(DateTime at, string state, string source = PostSource.Armor, int items = 1, string? reason = null) => fixture.RootExecuteAsync(
            $@"INSERT INTO {MariaDbFixture.DeliveryTable} (attempt_key, run_id, run_started_at, guild_id, channel_id, source, part, item_count, attempted_at, state,
               settled_at, reason, checks)
               VALUES (@key, 7, @at, @guild, 2, @source, 1, @items, @at, @state, @settled, @reason, 0)",
            new { key = ++key, at, guild = GuildId, source, items, state, settled = DeliveryState.IsOpen(state) ? (DateTime?)null : at, reason });

        Task QueueAsync(ulong itemId, string state, DateTime? settled, string source = PostSource.Armor, string? reason = null) => fixture.RootExecuteAsync(
            $@"INSERT INTO {MariaDbFixture.QueueTable} (guild_id, source, item_id, revision, is_new, queued_at, state, reason, settled_at)
               VALUES (@guild, @source, @itemId, 0, 1, @queued, @state, @reason, @settled)",
            new { guild = GuildId, source, itemId, queued = (settled ?? Cutoff).AddDays(-1), state, reason, settled });

        Task SeenAsync(string source, ulong itemId, DateTime lastSeen) => fixture.RootExecuteAsync(
            $"INSERT INTO {MariaDbFixture.SeenTable} (source, item_id, state, first_seen_at, last_seen_at) VALUES (@source, @itemId, 'baseline', @lastSeen, @lastSeen)",
            new { source, itemId, lastSeen });

        async Task<List<DateTime>> TimesAsync(string column, string table) => await fixture.RootQueryAsync<DateTime>($"SELECT {column} FROM {table} ORDER BY {column}");

        Task<List<(DateTime Day, string Source, string Outcome, string Reason, uint Messages, uint Items)>> DailyAsync() =>
            fixture.RootQueryAsync<(DateTime, string, string, string, uint, uint)>(
                $"SELECT day, source, outcome, reason, messages, items FROM {MariaDbFixture.DeliveryDailyTable} ORDER BY day, source, outcome, reason");

        // 90 days, once settled: a message still sending or in doubt stays, however old
        [Fact]
        public async Task DeliveriesGoAfter90DaysOnceSettled()
        {
            var db = await StartAsync();
            await DeliveryAsync(Cutoff.AddMilliseconds(-1), DeliveryState.Sent);
            await DeliveryAsync(Cutoff.AddDays(-30), DeliveryState.Failed);
            await DeliveryAsync(Cutoff.AddDays(-30), DeliveryState.Unconfirmed);
            await DeliveryAsync(Cutoff.AddDays(-30), DeliveryState.Sending);
            await DeliveryAsync(Cutoff.AddDays(-30), DeliveryState.InDoubt);
            await DeliveryAsync(Cutoff, DeliveryState.Sent);

            Assert.Equal(3, await db.PostingRetention().DeleteDeliveries(Cutoff, Chunk));
            Assert.Equal([DeliveryState.InDoubt, DeliveryState.Sending, DeliveryState.Sent],
                await fixture.RootQueryAsync<string>($"SELECT state FROM {MariaDbFixture.DeliveryTable} ORDER BY state"));
            Assert.Equal(0, await db.PostingRetention().DeleteDeliveries(Cutoff, Chunk));
        }

        // Maps and lobbies, 90 days after they were settled, sent or dropped. Items that still wait, or are being sent, have no
        // such time and stay.
        [Fact]
        public async Task SettledQueueRowsGoAndWaitingOnesStay()
        {
            var db = await StartAsync();
            await QueueAsync(1, QueueState.Sent, Cutoff.AddMilliseconds(-1), PostSource.Maps);
            await QueueAsync(2, QueueState.Dropped, Cutoff.AddDays(-1), PostSource.Lobbies, DeliveryReasons.TooOld);
            await QueueAsync(3, QueueState.Dropped, Cutoff, PostSource.Maps, DeliveryReasons.Left);
            await QueueAsync(4, QueueState.Pending, null, PostSource.Lobbies);
            await QueueAsync(5, QueueState.Sending, null, PostSource.Maps);

            Assert.Equal(2, await db.PostingRetention().DeleteSettledQueueRows(Cutoff, Chunk));
            Assert.Equal([3UL, 4, 5], await fixture.RootQueryAsync<ulong>($"SELECT item_id FROM {MariaDbFixture.QueueTable} ORDER BY item_id"));
        }

        // Armor's rows are where each armor went, kept however long ago they were settled, as its seen rows are: sent, and
        // dropped for any reason, not approved any more among them, which an approval again reopens
        [Fact]
        public async Task ArmorsQueueRowsStay()
        {
            var db = await StartAsync();
            await QueueAsync(1, QueueState.Sent, Cutoff.AddYears(-1));
            await QueueAsync(2, QueueState.Dropped, Cutoff.AddYears(-1), reason: DeliveryReasons.NotApproved);
            await QueueAsync(3, QueueState.Dropped, Cutoff.AddDays(-1), reason: DeliveryReasons.TooOld);
            await QueueAsync(4, QueueState.Sent, Cutoff.AddDays(-1), PostSource.Maps);
            await QueueAsync(5, QueueState.Dropped, Cutoff.AddDays(-1), PostSource.Lobbies, DeliveryReasons.Closed);

            Assert.Equal(2, await db.PostingRetention().DeleteSettledQueueRows(Cutoff, Chunk));
            Assert.Equal([1UL, 2, 3], await fixture.RootQueryAsync<ulong>($"SELECT item_id FROM {MariaDbFixture.QueueTable} ORDER BY item_id"));
            Assert.Equal(0, await db.PostingRetention().DeleteSettledQueueRows(Cutoff, Chunk));
        }

        // An armor posted in two servers, and one withdrawn before its post, both long before the cutoff, whose post records
        // the clean-up deletes: the first still reads as posted in both, so Back to pending stays refused, and the second still
        // has its rows to reopen when it's approved again, so it's posted then
        [Fact]
        public async Task ArmorsRecordOutlivesItsPostRecords()
        {
            var db = await StartAsync();
            var store = db.Notifications();
            var found = Cutoff.AddDays(-30);
            const ulong OtherGuildId = GuildId + 1;

            static QueueItem Queued(ulong guildId, ulong itemId, DateTime at) => new() { guild_id = guildId, source = PostSource.Armor, item_id = itemId, is_new = true, queued_at = at };
            static SeenRow Seen(ulong itemId, DateTime at) => new() { source = PostSource.Armor, item_id = itemId, state = SeenState.Queued, first_seen_at = at, last_seen_at = at };

            await store.SaveDetectionAsync(new Detection
            {
                At = found,
                Seen = [Seen(11, found), Seen(12, found)],
                Queue = [Queued(GuildId, 11, found), Queued(OtherGuildId, 11, found), Queued(GuildId, 12, found)]
            });

            var open = await store.ListOpenAsync();
            ulong attempt = 0;

            foreach (var row in open.Where(row => row.item_id == 11))
            {
                attempt++;
                await store.ClaimAsync(new DeliveryClaim { AttemptKey = attempt, RunId = 7, RunStartedAt = found, GuildId = row.guild_id, ChannelId = 2, Source = PostSource.Armor,
                    Part = 1, AttemptedAt = found, QueueIds = [row.id] });
                await store.SettleAsync(new Settlement { AttemptKey = attempt, Outcome = DeliveryState.Sent, At = found, MessageId = 5000000000000000000 + attempt });
            }

            await store.DropAsync([open.Single(row => row.item_id == 12).id], DeliveryReasons.NotApproved, found.AddMinutes(1));

            var retention = db.PostingRetention();
            Assert.Equal(2, await retention.DeleteDeliveries(Cutoff, Chunk));
            await retention.DeleteSettledQueueRows(Cutoff, Chunk);

            var posted = await db.Deliveries().GetItemPosts(PostSource.Armor, 11);
            Assert.Equal(SeenState.Queued, posted.Seen?.state);
            Assert.Equal([(GuildId, QueueState.Sent, (DateTime?)found), (OtherGuildId, QueueState.Sent, found)],
                posted.Servers.Select(row => (row.guild_id, row.state, row.settled_at)));

            Assert.Single((await store.LoadAsync(new PostingLoad { ArmorIds = [12] })).Withdrawn);
            await store.SaveDetectionAsync(new Detection { At = Today, ReopenArmor = [12] });
            Assert.Equal([(GuildId, 12UL)], (await store.ListOpenAsync()).Select(row => (row.guild_id, row.item_id)));
        }

        // A week after a lobby was last in a read. The armor, maps and submissions the bot found are its record, and stay.
        [Fact]
        public async Task SeenLobbiesGoAfterAWeekAndTheRestStay()
        {
            var db = await StartAsync();
            await SeenAsync(PostSource.Lobbies, 1, Cutoff.AddMilliseconds(-1));
            await SeenAsync(PostSource.Lobbies, 2, Cutoff);
            await SeenAsync(PostSource.Armor, 3, Cutoff.AddYears(-1));
            await SeenAsync(PostSource.Maps, 4, Cutoff.AddYears(-1));
            await SeenAsync(PostSource.Submissions, 5, Cutoff.AddYears(-1));

            Assert.Equal(1, await db.PostingRetention().DeleteSeenLobbies(Cutoff, Chunk));
            Assert.Equal([2UL, 3, 4, 5], await fixture.RootQueryAsync<ulong>($"SELECT item_id FROM {MariaDbFixture.SeenTable} ORDER BY item_id"));
        }

        [Fact]
        public async Task LobbySamplesGoBeforeTheCutoff()
        {
            var db = await StartAsync();

            foreach (var minute in new[] { Cutoff.AddMinutes(-1), Cutoff, Cutoff.AddMinutes(1) })
                await fixture.RootExecuteAsync($"INSERT INTO {MariaDbFixture.LobbySampleTable} (minute, steam_answered) VALUES (@minute, 0)", new { minute });

            Assert.Equal(1, await db.PostingRetention().DeleteLobbySamples(Cutoff, Chunk));
            Assert.Equal([Cutoff, Cutoff.AddMinutes(1)], await TimesAsync("minute", MariaDbFixture.LobbySampleTable));
        }

        [Fact]
        public async Task MoreRowsThanOneChunkGoInChunks()
        {
            var db = await StartAsync();

            await fixture.RootExecuteAsync(
                $@"INSERT INTO {MariaDbFixture.DeliveryTable} (attempt_key, run_id, run_started_at, guild_id, channel_id, source, part, item_count, attempted_at, state,
                   settled_at, checks)
                   SELECT seq, 7, @from, @guild, 2, 'lobbies', 1, 1, TIMESTAMPADD(SECOND, seq, @from), 'sent', TIMESTAMPADD(SECOND, seq, @from), 0
                   FROM {MariaDbFixture.BotDatabase}.seq_1_to_6000", new { from = Cutoff.AddDays(-10), guild = GuildId });

            var retention = db.PostingRetention();

            Assert.Equal(Chunk, await retention.DeleteDeliveries(Cutoff, Chunk));
            Assert.Equal(Cutoff.AddDays(-10).AddSeconds(Chunk + 1), (await TimesAsync("attempted_at", MariaDbFixture.DeliveryTable))[0]);
            Assert.Equal(1000, await retention.DeleteDeliveries(Cutoff, Chunk));
            Assert.Equal(0, await retention.DeleteDeliveries(Cutoff, Chunk));
        }

        // Each day kept, up to today, totalled from the messages tried that day that went, failed or may not have arrived, and
        // the items dropped that day, by source, outcome and reason. Totalled again, the numbers are the same; a message settled
        // late counts; an item dropped and queued again stops counting; and a day before the first kept keeps its totals.
        [Fact]
        public async Task DailyTotalsRepeatToTheSameNumbersAndCountLateSettles()
        {
            var db = await StartAsync();
            var retention = db.PostingRetention();
            var dayOne = Today.AddDays(-2);
            var dayTwo = Today.AddDays(-1);

            await fixture.RootExecuteAsync($"INSERT INTO {MariaDbFixture.DeliveryDailyTable} (day, source, outcome, reason, messages, items) VALUES (@day, 'armor', 'sent', '', 9, 9)",
                new { day = KeepFrom.AddDays(-1) });

            await DeliveryAsync(dayOne.AddHours(1), DeliveryState.Sent, items: 3);
            await DeliveryAsync(dayOne.AddHours(23).AddMinutes(59), DeliveryState.Sent, items: 2);
            await DeliveryAsync(dayOne.AddHours(2), DeliveryState.Failed, reason: DeliveryReasons.DiscordPermissions);
            await DeliveryAsync(dayOne.AddHours(3), DeliveryState.Unconfirmed, PostSource.Maps, reason: DeliveryReasons.Unconfirmed);
            await DeliveryAsync(dayTwo, DeliveryState.Sent, PostSource.Lobbies, items: 4);
            await DeliveryAsync(dayTwo.AddHours(5), DeliveryState.InDoubt, PostSource.Lobbies);

            // Before the first day kept, which is never totalled again, and today, which isn't over
            await DeliveryAsync(KeepFrom.AddMilliseconds(-1), DeliveryState.Sent);
            await DeliveryAsync(Today, DeliveryState.Sent);

            await QueueAsync(1, QueueState.Dropped, dayOne.AddHours(4), reason: DeliveryReasons.SwitchedOff);
            await QueueAsync(2, QueueState.Dropped, dayOne.AddHours(5), reason: DeliveryReasons.SwitchedOff);
            await QueueAsync(3, QueueState.Dropped, dayOne.AddHours(6), reason: DeliveryReasons.NotApproved);
            await QueueAsync(4, QueueState.Dropped, dayTwo.AddHours(1), PostSource.Maps, DeliveryReasons.TooOld);
            await QueueAsync(5, QueueState.Sent, dayTwo.AddHours(1));
            await QueueAsync(6, QueueState.Pending, null);

            List<(DateTime, string, string, string, uint, uint)> expected =
            [
                (KeepFrom.AddDays(-1), PostSource.Armor, DeliveryState.Sent, "", 9, 9),
                (dayOne, PostSource.Armor, QueueState.Dropped, DeliveryReasons.NotApproved, 0, 1),
                (dayOne, PostSource.Armor, QueueState.Dropped, DeliveryReasons.SwitchedOff, 0, 2),
                (dayOne, PostSource.Armor, DeliveryState.Failed, DeliveryReasons.DiscordPermissions, 1, 1),
                (dayOne, PostSource.Armor, DeliveryState.Sent, "", 2, 5),
                (dayOne, PostSource.Maps, DeliveryState.Unconfirmed, DeliveryReasons.Unconfirmed, 1, 1),
                (dayTwo, PostSource.Lobbies, DeliveryState.Sent, "", 1, 4),
                (dayTwo, PostSource.Maps, QueueState.Dropped, DeliveryReasons.TooOld, 0, 1)
            ];

            Assert.Equal(2, await retention.TotalDeliveryDays(KeepFrom, Today));
            Assert.Equal(expected, await DailyAsync());

            Assert.Equal(2, await retention.TotalDeliveryDays(KeepFrom, Today));
            Assert.Equal(expected, await DailyAsync());

            // The message in doubt went after all, and the armor not approved any more was approved again and waits
            await fixture.RootExecuteAsync($"UPDATE {MariaDbFixture.DeliveryTable} SET state = 'sent', settled_at = @at WHERE state = 'in_doubt'", new { at = Today });
            await fixture.RootExecuteAsync($"UPDATE {MariaDbFixture.QueueTable} SET state = 'pending', reason = NULL, settled_at = NULL WHERE item_id = 3");

            Assert.Equal(2, await retention.TotalDeliveryDays(KeepFrom, Today));
            Assert.Equal([expected[0], .. expected[2..6], (dayTwo, PostSource.Lobbies, DeliveryState.Sent, "", 2u, 5u), expected[7]], await DailyAsync());
        }
    }
}
