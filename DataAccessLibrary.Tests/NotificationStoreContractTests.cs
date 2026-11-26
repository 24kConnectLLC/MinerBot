using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using DataAccessLibrary.Status;
using DataAccessLibrary.Tests.Database;
using MinerBot.Shared.Delivery;

namespace DataAccessLibrary.Tests
{
    // The posting store's contract, one list of scenarios run against MemoryNotificationData, which the bot without
    // BotConnection and the bot's tests use, and against NotificationData on MariaDB. Each scenario asserts the same answers
    // from both, and then everything each store holds must be the same, so the bot's tests can't pass on a store that the
    // real one differs from. IDs MariaDB gives can skip numbers the memory store doesn't, so the rows are compared without
    // them, a queued item by the attempt of the message that holds it.
    [Collection("Database")]
    public class NotificationStoreContractTests(MariaDbFixture fixture)
    {
        static readonly DateTime Noon = new(2026, 11, 24, 12, 0, 0, 123, DateTimeKind.Utc);
        const ulong RunId = 18446744073709551615, OtherRunId = 7;
        const ulong GuildA = 1000000000000000001, GuildB = 1000000000000000002;
        const ulong ChannelA = 2000000000000000001, ChannelB = 2000000000000000002;
        const ulong MapId = 3000000001, LobbyId = 109775241000000001;
        const string Job = "GuildNotificationJob.NotifyAllGuilds";

        // Every scenario, run on both stores
        async Task BothAsync(Func<Harness, Task> scenario)
        {
            var memory = new MemoryHarness();
            await scenario(memory);

            var db = fixture.Require();
            await db.ClearAsync([.. MariaDbFixture.PostingTables, MariaDbFixture.JobRunTable]);
            var sql = new SqlHarness(db);
            await scenario(sql);

            var expected = await memory.SnapshotAsync();
            var found = await sql.SnapshotAsync();

            Assert.Equal(expected.Baselines, found.Baselines);
            Assert.Equal(expected.Switches, found.Switches);
            Assert.Equal(expected.Seen, found.Seen);
            Assert.Equal(expected.Queue, found.Queue);
            Assert.Equal(expected.Deliveries, found.Deliveries);
            Assert.Equal(expected.Statuses, found.Statuses);
        }

        // A detection is saved whole and read back as it was: the baselines, the seen rows of the items asked for, the queued
        // items as pending, oldest first, and nothing else
        [Fact]
        public Task DetectionIsReadBackAsSaved() => BothAsync(async h =>
        {
            var detection = new Detection
            {
                At = Noon,
                Baselines = [Baseline(PostSource.Armor, Noon), Baseline(PostSource.Maps, Noon, time: Noon.AddDays(-3))],
                Seen = [Seen(PostSource.Armor, 11, SeenState.Baseline, Noon), Seen(PostSource.Armor, 12, SeenState.Queued, Noon),
                    Seen(PostSource.Maps, MapId, SeenState.Queued, Noon, updated: Noon.AddHours(-1), queued: Noon), Seen(PostSource.Lobbies, LobbyId, SeenState.Ignored, Noon)],
                Queue = [Queued(GuildB, PostSource.Armor, 12, Noon.AddSeconds(1)), Queued(GuildA, PostSource.Armor, 12, Noon),
                    Queued(GuildA, PostSource.Maps, MapId, Noon, revision: 1764000000)]
            };

            await h.Store.SaveDetectionAsync(detection);

            var state = await h.Store.LoadAsync(new PostingLoad { ArmorIds = [12, 11, 99], LobbyIds = [LobbyId + 1] });

            Assert.Equal(detection.Baselines, state.Baselines);
            Assert.Equal([detection.Seen[0], detection.Seen[1]], state.Seen);
            Assert.Equal([(GuildA, PostSource.Armor, 12UL, 0UL), (GuildA, PostSource.Maps, MapId, 1764000000UL), (GuildB, PostSource.Armor, 12UL, 0UL)],
                state.Open.Select(row => (row.guild_id, row.source, row.item_id, row.revision)));
            Assert.All(state.Open, row => Assert.Equal((QueueState.Pending, (ulong?)null, (string?)null, (DateTime?)null, true), (row.state, row.delivery_id, row.reason, row.settled_at, row.is_new)));
            Assert.Empty(state.Switches);
            Assert.Empty(state.Statuses);
            Assert.Empty(state.Unsettled);
            Assert.Empty(state.Withdrawn);

            Assert.Equal(detection.Baselines, await h.Store.ListBaselinesAsync());
            Assert.Equal([detection.Seen[0], detection.Seen[1]], await h.Store.ListSeenAsync(PostSource.Armor, [12, 11]));
            Assert.Equal([detection.Seen[2]], await h.Store.ListSeenAsync(PostSource.Maps, [MapId, MapId + 1]));
            Assert.Equal(state.Open, await h.Store.ListOpenAsync());
        });

        // A detection saved again lands once: its items stay as the first save and what happened since left them, and a
        // seen row keeps when its item was first seen
        [Fact]
        public Task DetectionSavedAgainLandsOnce() => BothAsync(async h =>
        {
            var detection = new Detection
            {
                At = Noon,
                Seen = [Seen(PostSource.Armor, 11, SeenState.Queued, Noon)],
                Queue = [Queued(GuildA, PostSource.Armor, 11, Noon), Queued(GuildB, PostSource.Armor, 11, Noon)]
            };

            await h.Store.SaveDetectionAsync(detection);
            await h.Store.ClaimAsync(Claim(1, GuildA, PostSource.Armor, [await IdOf(h, GuildA, PostSource.Armor, 11)], Noon.AddSeconds(1)));
            await h.Store.SaveDetectionAsync(detection);

            var later = new Detection
            {
                At = Noon.AddMinutes(5),
                Seen = [Seen(PostSource.Armor, 11, SeenState.Queued, Noon.AddMinutes(5))],
                Queue = [Queued(GuildB, PostSource.Armor, 11, Noon.AddMinutes(5))]
            };

            await h.Store.SaveDetectionAsync(later);

            var state = await h.Store.LoadAsync(new PostingLoad { ArmorIds = [11] });
            Assert.Equal([(GuildB, Noon)], state.Open.Select(row => (row.guild_id, row.queued_at)));
            Assert.Equal(Noon, Assert.Single(state.Seen).first_seen_at);
            Assert.Equal(Noon.AddMinutes(5), state.Seen[0].last_seen_at);
            Assert.Equal(DeliveryState.Sending, Assert.Single(state.Unsettled).state);
        });

        // A claim takes pending rows of its own server and kind only, all of them or none, and an attempt claims once
        [Fact]
        public Task ClaimTakesPendingRowsAllOrNone() => BothAsync(async h =>
        {
            await h.Store.SaveDetectionAsync(Detection(Noon, Queued(GuildA, PostSource.Armor, 11, Noon), Queued(GuildA, PostSource.Armor, 12, Noon),
                Queued(GuildB, PostSource.Armor, 11, Noon), Queued(GuildA, PostSource.Lobbies, LobbyId, Noon)));

            var a11 = await IdOf(h, GuildA, PostSource.Armor, 11);
            var a12 = await IdOf(h, GuildA, PostSource.Armor, 12);
            var b11 = await IdOf(h, GuildB, PostSource.Armor, 11);
            var lobby = await IdOf(h, GuildA, PostSource.Lobbies, LobbyId);

            await Assert.ThrowsAsync<ClaimLostException>(() => h.Store.ClaimAsync(Claim(1, GuildA, PostSource.Armor, [a11, b11], Noon)));
            await Assert.ThrowsAsync<ClaimLostException>(() => h.Store.ClaimAsync(Claim(2, GuildA, PostSource.Armor, [a11, lobby], Noon)));
            await Assert.ThrowsAsync<ClaimLostException>(() => h.Store.ClaimAsync(Claim(3, GuildA, PostSource.Armor, [a11, 999999], Noon)));
            Assert.Equal(4, (await h.Store.ListOpenAsync()).Count);

            var id = await h.Store.ClaimAsync(Claim(4, GuildA, PostSource.Armor, [a11, a12], Noon.AddSeconds(1)));

            await Assert.ThrowsAsync<ClaimLostException>(() => h.Store.ClaimAsync(Claim(5, GuildA, PostSource.Armor, [a12], Noon.AddSeconds(2))));
            await Assert.ThrowsAsync<ClaimLostException>(() => h.Store.ClaimAsync(Claim(4, GuildB, PostSource.Armor, [b11], Noon.AddSeconds(2))));

            var state = await h.Store.LoadAsync(new PostingLoad());
            var delivery = Assert.Single(state.Unsettled);
            Assert.Equal((id, 4UL, RunId, GuildA, ChannelA, PostSource.Armor, (byte)1, (ushort)2, Noon.AddSeconds(1), DeliveryState.Sending, (byte)0),
                (delivery.id, delivery.attempt_key, delivery.run_id, delivery.guild_id, delivery.channel_id, delivery.source, delivery.part, delivery.item_count,
                delivery.attempted_at, delivery.state, delivery.checks));
            Assert.Equal([b11, lobby], state.Open.Select(row => row.id));
        });

        // A message that went, then one that failed twice and then went: each settle lands once, and the status counts each
        // failure once, keeps when they started, and clears them when a post goes, keeping the last one as history
        [Fact]
        public Task SettleIsSafeToRepeat() => BothAsync(async h =>
        {
            await h.Store.SaveDetectionAsync(Detection(Noon, Queued(GuildA, PostSource.Armor, 11, Noon), Queued(GuildB, PostSource.Armor, 11, Noon)));

            var a11 = await IdOf(h, GuildA, PostSource.Armor, 11);
            var b11 = await IdOf(h, GuildB, PostSource.Armor, 11);

            await h.Store.ClaimAsync(Claim(1, GuildA, PostSource.Armor, [a11], Noon.AddSeconds(1)));
            Assert.True(await h.Store.SettleAsync(Sent(1, 5000000000000000001, Noon.AddSeconds(2))));
            Assert.False(await h.Store.SettleAsync(Sent(1, 5000000000000000001, Noon.AddSeconds(3))));
            Assert.False(await h.Store.SettleAsync(Refused(1, Noon.AddSeconds(3))));
            Assert.False(await h.Store.SettleAsync(Sent(99, 5000000000000000009, Noon.AddSeconds(3))));

            await h.Store.ClaimAsync(Claim(2, GuildB, PostSource.Armor, [b11], Noon.AddSeconds(4), ChannelB));
            Assert.True(await h.Store.SettleAsync(Refused(2, Noon.AddSeconds(5), next: Noon.AddMinutes(5))));
            Assert.False(await h.Store.SettleAsync(Refused(2, Noon.AddSeconds(6), next: Noon.AddMinutes(6))));

            var b11Again = await IdOf(h, GuildB, PostSource.Armor, 11);
            Assert.Equal(b11, b11Again);

            await h.Store.ClaimAsync(Claim(3, GuildB, PostSource.Armor, [b11], Noon.AddMinutes(5), ChannelB));
            Assert.True(await h.Store.SettleAsync(Refused(3, Noon.AddMinutes(5).AddSeconds(1), next: Noon.AddMinutes(20))));

            var statuses = (await h.Store.LoadAsync(new PostingLoad())).Statuses;
            Assert.Equal(2, statuses.Count);
            Assert.Equal((Noon.AddSeconds(1), (ulong?)ChannelA, (ulong?)5000000000000000001, 0u, (DateTime?)null), (statuses[0].last_sent_at!.Value, statuses[0].last_sent_channel_id,
                statuses[0].last_message_id, statuses[0].failures, statuses[0].failing_since));
            Assert.Equal(((DateTime?)Noon.AddSeconds(4), 2u, (DateTime?)Noon.AddMinutes(20), (DateTime?)Noon.AddMinutes(5), DeliveryReasons.DiscordPermissions, (ushort?)403,
                (uint?)50013), (statuses[1].failing_since, statuses[1].failures, statuses[1].next_try_at, statuses[1].last_failed_at, statuses[1].last_reason,
                statuses[1].last_http_status, statuses[1].last_discord_code));

            await h.Store.ClaimAsync(Claim(4, GuildB, PostSource.Armor, [b11], Noon.AddMinutes(20), ChannelB));
            Assert.True(await h.Store.SettleAsync(Sent(4, 5000000000000000002, Noon.AddMinutes(20).AddSeconds(1))));

            var cleared = (await h.Store.LoadAsync(new PostingLoad())).Statuses[1];
            Assert.Equal(((DateTime?)null, 0u, (DateTime?)null, (ulong?)null, (DateTime?)Noon.AddMinutes(5), DeliveryReasons.DiscordPermissions, (DateTime?)Noon.AddMinutes(20)),
                (cleared.failing_since, cleared.failures, cleared.next_try_at, cleared.waiting_on, cleared.last_failed_at, cleared.last_reason, cleared.last_sent_at));
            Assert.Empty(await h.Store.ListOpenAsync());
        });

        // A refusal Discord.Net can't hold against the server, a rate limit or a message that wasn't made, gives its rows back
        // without counting; a message in doubt keeps its rows until a look settles it, and one that can't be found out is
        // counted as sent, so it's never sent again, its rows keeping why after the message's own row is gone
        [Fact]
        public Task DoubtfulMessagesSettleFromTheLook() => BothAsync(async h =>
        {
            await h.Store.SaveDetectionAsync(Detection(Noon, Queued(GuildA, PostSource.Maps, MapId, Noon, revision: 5), Queued(GuildA, PostSource.Lobbies, LobbyId, Noon),
                Queued(GuildB, PostSource.Lobbies, LobbyId, Noon)));

            var map = await IdOf(h, GuildA, PostSource.Maps, MapId);
            var lobbyA = await IdOf(h, GuildA, PostSource.Lobbies, LobbyId);
            var lobbyB = await IdOf(h, GuildB, PostSource.Lobbies, LobbyId);

            await h.Store.ClaimAsync(Claim(1, GuildA, PostSource.Maps, [map], Noon.AddSeconds(1)));
            Assert.True(await h.Store.SettleAsync(new Settlement { AttemptKey = 1, Outcome = DeliveryState.InDoubt, At = Noon.AddSeconds(16), Reason = DeliveryReasons.NoAnswer, HttpStatus = 502 }));
            Assert.False(await h.Store.SettleAsync(new Settlement { AttemptKey = 1, Outcome = DeliveryState.InDoubt, At = Noon.AddSeconds(17) }));

            Assert.True(await h.Store.MarkCheckedAsync(1, 1));
            Assert.False(await h.Store.MarkCheckedAsync(1, 1));
            Assert.True(await h.Store.MarkCheckedAsync(1, 2));
            Assert.DoesNotContain(map, (await h.Store.ListOpenAsync()).Select(row => row.id));

            Assert.True(await h.Store.SettleAsync(new Settlement { AttemptKey = 1, Outcome = DeliveryState.Unconfirmed, At = Noon.AddHours(1) }));
            Assert.False(await h.Store.SettleAsync(Sent(1, 5000000000000000001, Noon.AddHours(1))));
            Assert.Equal((QueueState.Sent, (string?)DeliveryReasons.Unconfirmed, (DateTime?)Noon.AddHours(1)),
                (await h.SnapshotAsync()).Queue.Where(row => row.source == PostSource.Maps).Select(row => (row.state, row.reason, row.settled_at)).Single());
            Assert.False(await h.Store.MarkCheckedAsync(1, 3));

            // A sending message of a run that stopped, which a look couldn't settle, is in doubt from then
            await h.Store.ClaimAsync(Claim(2, GuildA, PostSource.Lobbies, [lobbyA], Noon.AddSeconds(2), runId: OtherRunId));
            Assert.True(await h.Store.MarkCheckedAsync(2, 1));
            Assert.True(await h.Store.SettleAsync(new Settlement { AttemptKey = 2, Outcome = DeliveryState.Failed, At = Noon.AddMinutes(5), Reason = DeliveryReasons.NotSent }));

            await h.Store.ClaimAsync(Claim(3, GuildB, PostSource.Lobbies, [lobbyB], Noon.AddSeconds(3), ChannelB));
            Assert.True(await h.Store.SettleAsync(new Settlement { AttemptKey = 3, Outcome = DeliveryState.Failed, At = Noon.AddSeconds(4), Reason = DeliveryReasons.RateLimited, HttpStatus = 429 }));

            var state = await h.Store.LoadAsync(new PostingLoad());
            Assert.Equal([lobbyA, lobbyB], state.Open.Select(row => row.id));
            Assert.Empty(state.Unsettled);
            Assert.Empty(state.Statuses);
        });

        // A message that the look in the channel found after Discord didn't answer went: it has its ID and no reason, so it
        // counts with the messages that went, and keeps the numbers of the answer that left it in doubt
        [Fact]
        public Task MessageFoundAfterADoubtWentWithNoReason() => BothAsync(async h =>
        {
            await h.Store.SaveDetectionAsync(Detection(Noon, Queued(GuildA, PostSource.Armor, 11, Noon)));

            await h.Store.ClaimAsync(Claim(1, GuildA, PostSource.Armor, [await IdOf(h, GuildA, PostSource.Armor, 11)], Noon));
            await h.Store.SettleAsync(new Settlement { AttemptKey = 1, Outcome = DeliveryState.InDoubt, At = Noon.AddSeconds(15), Reason = DeliveryReasons.NoAnswer, HttpStatus = 502 });
            Assert.True(await h.Store.SettleAsync(Sent(1, 5000000000000000001, Noon.AddMinutes(5))));

            var delivery = Assert.Single((await h.SnapshotAsync()).Deliveries);
            Assert.Equal((DeliveryState.Sent, (ulong?)5000000000000000001, (string?)null, (ushort?)502, (DateTime?)Noon.AddMinutes(5)),
                (delivery.state, delivery.message_id, delivery.reason, delivery.http_status, delivery.settled_at));
            Assert.Equal(QueueState.Sent, Assert.Single((await h.SnapshotAsync()).Queue).state);
        });

        // The messages the bot's rows hold in one channel of one server, tried since the time given: not a message in doubt or
        // a refusal, which hold none, and not another channel's or another server's
        [Fact]
        public Task MessagesHeldInAChannelAreListed() => BothAsync(async h =>
        {
            await h.Store.SaveDetectionAsync(Detection(Noon, Queued(GuildA, PostSource.Armor, 11, Noon), Queued(GuildA, PostSource.Armor, 12, Noon),
                Queued(GuildA, PostSource.Armor, 13, Noon), Queued(GuildA, PostSource.Maps, MapId, Noon), Queued(GuildB, PostSource.Armor, 11, Noon)));

            async Task SendAsync(ulong key, ulong guildId, string source, ulong itemId, int minutes, ulong channelId, ulong? messageId)
            {
                await h.Store.ClaimAsync(Claim(key, guildId, source, [await IdOf(h, guildId, source, itemId)], Noon.AddMinutes(minutes), channelId));

                if (messageId is { } message)
                    await h.Store.SettleAsync(Sent(key, message, Noon.AddMinutes(minutes)));
            }

            await SendAsync(1, GuildA, PostSource.Armor, 11, 0, ChannelA, 5000000000000000001);
            await SendAsync(2, GuildA, PostSource.Armor, 12, 1, ChannelA, null);
            await SendAsync(3, GuildA, PostSource.Armor, 13, 2, ChannelA, 5000000000000000003);
            await SendAsync(4, GuildA, PostSource.Maps, MapId, 3, ChannelB, 5000000000000000004);
            await SendAsync(5, GuildB, PostSource.Armor, 11, 4, ChannelA, 5000000000000000005);
            await h.Store.RecordRefusalAsync(Refusal(6, GuildA, PostSource.Armor, ChannelA, Noon.AddMinutes(5)));

            Assert.Equal([5000000000000000001UL, 5000000000000000003], await h.Store.ListMessageIdsAsync(GuildA, ChannelA, Noon));
            Assert.Equal([5000000000000000003UL], await h.Store.ListMessageIdsAsync(GuildA, ChannelA, Noon.AddMinutes(1)));
            Assert.Equal([5000000000000000004UL], await h.Store.ListMessageIdsAsync(GuildA, ChannelB, Noon));
            Assert.Empty(await h.Store.ListMessageIdsAsync(GuildA + 9, ChannelA, Noon));
        });

        // From a time finer than the millisecond the rows keep, as MariaDB compares it: to the microsecond, as MySql.Data sends
        // it, so a time inside a row's millisecond is after the row, and one finer than a microsecond is cut to it
        [Fact]
        public Task MessagesHeldFromAFinerTimeAreListedAsMariaDbComparesIt() => BothAsync(async h =>
        {
            await h.Store.SaveDetectionAsync(Detection(Noon, Queued(GuildA, PostSource.Armor, 11, Noon), Queued(GuildA, PostSource.Armor, 12, Noon),
                Queued(GuildA, PostSource.Armor, 13, Noon)));

            for (ulong item = 11; item <= 13; item++)
            {
                var at = Noon.AddSeconds(item - 11);
                await h.Store.ClaimAsync(Claim(item, GuildA, PostSource.Armor, [await IdOf(h, GuildA, PostSource.Armor, item)], at));
                await h.Store.SettleAsync(Sent(item, 5000000000000000000 + item, at));
            }

            Assert.Equal([5000000000000000011UL, 5000000000000000012, 5000000000000000013], await h.Store.ListMessageIdsAsync(GuildA, ChannelA, Noon));
            Assert.Equal([5000000000000000012UL, 5000000000000000013], await h.Store.ListMessageIdsAsync(GuildA, ChannelA, Noon.AddTicks(5000)));
            Assert.Equal([5000000000000000012UL, 5000000000000000013], await h.Store.ListMessageIdsAsync(GuildA, ChannelA, Noon.AddSeconds(1).AddTicks(1)));
            Assert.Equal([5000000000000000013UL], await h.Store.ListMessageIdsAsync(GuildA, ChannelA, Noon.AddSeconds(1).AddTicks(10)));
        });

        // The items a message carries while it isn't settled, which the look in its channel's history knows a map post by: not
        // another message's, and none once it's settled
        [Fact]
        public Task MessagesCarryTheirItemsUntilSettled() => BothAsync(async h =>
        {
            await h.Store.SaveDetectionAsync(Detection(Noon, Queued(GuildA, PostSource.Maps, MapId + 1, Noon, revision: 1764000000),
                Queued(GuildA, PostSource.Maps, MapId, Noon, revision: 1764000000), Queued(GuildA, PostSource.Armor, 11, Noon), Queued(GuildB, PostSource.Maps, MapId, Noon,
                revision: 1764000000)));

            var maps = await h.Store.ClaimAsync(Claim(1, GuildA, PostSource.Maps, [await IdOf(h, GuildA, PostSource.Maps, MapId + 1), await IdOf(h, GuildA, PostSource.Maps, MapId)],
                Noon));
            var armor = await h.Store.ClaimAsync(Claim(2, GuildA, PostSource.Armor, [await IdOf(h, GuildA, PostSource.Armor, 11)], Noon));
            await h.Store.SettleAsync(new Settlement { AttemptKey = 1, Outcome = DeliveryState.InDoubt, At = Noon.AddSeconds(15), Reason = DeliveryReasons.NoAnswer });

            Assert.Equal([MapId, MapId + 1], await h.Store.ListCarriedAsync(maps));
            Assert.Equal([11UL], await h.Store.ListCarriedAsync(armor));
            Assert.Empty(await h.Store.ListCarriedAsync(armor + maps + 99));

            await h.Store.SettleAsync(Sent(1, 5000000000000000001, Noon.AddMinutes(5)));
            await h.Store.SettleAsync(new Settlement { AttemptKey = 2, Outcome = DeliveryState.Failed, At = Noon.AddSeconds(1), Reason = DeliveryReasons.NotSent });

            Assert.Empty(await h.Store.ListCarriedAsync(maps));
            Assert.Empty(await h.Store.ListCarriedAsync(armor));
        });

        // A map post's claim queues for its server the updates it shows that none of its rows is for, held by it: they settle
        // with it, sent or back to pending, and the update's own queueing later lands on them, so a server that was shown it
        // isn't sent it again. An update that's in the queue for the server already stays as it is.
        [Fact]
        public Task ClaimQueuesTheUpdatesItsPostShows() => BothAsync(async h =>
        {
            const ulong First = 1764000000, Second = 1764001800, Third = 1764003600;

            await h.Store.SaveDetectionAsync(Detection(Noon, Queued(GuildA, PostSource.Maps, MapId, Noon, revision: First), Queued(GuildB, PostSource.Maps, MapId, Noon, revision: First),
                Queued(GuildA, PostSource.Maps, MapId + 1, Noon, revision: First), Queued(GuildA, PostSource.Maps, MapId + 1, Noon, revision: Second)));

            var a = await IdOf(h, GuildA, PostSource.Maps, MapId);
            var older = (await h.Store.ListOpenAsync()).Single(row => row.guild_id == GuildA && row.item_id == MapId + 1 && row.revision == First).id;
            var dropped = (await h.Store.ListOpenAsync()).Single(row => row.guild_id == GuildA && row.item_id == MapId + 1 && row.revision == Second).id;
            await h.Store.DropAsync([dropped], DeliveryReasons.TooOld, Noon.AddMinutes(1));

            var shown = await h.Store.ClaimAsync(Claim(1, GuildA, PostSource.Maps, [a, older], Noon.AddMinutes(2), shows: [new(MapId, Second), new(MapId + 1, Second)]));

            Assert.Equal([MapId, MapId, MapId + 1], await h.Store.ListCarriedAsync(shown));
            Assert.Equal((ushort)2, Assert.Single((await h.Store.LoadAsync(new PostingLoad())).Unsettled).item_count);

            await h.Store.SettleAsync(Sent(1, 5000000000000000001, Noon.AddMinutes(3)));
            await h.Store.SaveDetectionAsync(Detection(Noon.AddDays(1), Queued(GuildA, PostSource.Maps, MapId, Noon.AddDays(1), revision: Second) with { is_new = false },
                Queued(GuildB, PostSource.Maps, MapId, Noon.AddDays(1), revision: Second) with { is_new = false }));

            Assert.Equal([(GuildB, First), (GuildB, Second)], (await h.Store.ListOpenAsync()).Select(row => (row.guild_id, row.revision)));

            // A message that wasn't made gives back what it shows with its rows, so the server is still owed the map as it is
            var b = (await h.Store.ListOpenAsync()).Select(row => row.id).ToArray();
            await h.Store.ClaimAsync(Claim(2, GuildB, PostSource.Maps, b, Noon.AddDays(1).AddMinutes(1), shows: [new(MapId, Third)]));
            await h.Store.SettleAsync(new Settlement { AttemptKey = 2, Outcome = DeliveryState.Failed, At = Noon.AddDays(1).AddMinutes(2), Reason = DeliveryReasons.NotSent });

            Assert.Equal([(GuildB, First, (ulong?)null), (GuildB, Second, null), (GuildB, Third, null)],
                (await h.Store.ListOpenAsync()).Select(row => (row.guild_id, row.revision, row.delivery_id)).OrderBy(row => row.revision));
        });

        // What a map post that wasn't made showed goes back to pending with its rows, and is dropped with them when the server
        // isn't owed maps then, for any of the five reasons: no website row, its channel 0, its switch off, the bot not in it,
        // or the bot left it. The update's own queueing, once the server is owed maps again, puts that row back, queued from
        // then: a detection queues each update once, so the row can only be what a post showed, and the server never got it.
        // Not one dropped for anything else, so a map shown again with a post's update in it isn't posted twice. The reasons
        // are written out, not read from the store's list, so a reason missing from it fails here.
        [Theory]
        [InlineData(DeliveryReasons.NoSettings)]
        [InlineData(DeliveryReasons.ChannelOff)]
        [InlineData(DeliveryReasons.SwitchedOff)]
        [InlineData(DeliveryReasons.NotInServer)]
        [InlineData(DeliveryReasons.Left)]
        public Task MapUpdateQueuedForAServerThatWasOffPutsBackWhatAPostShowed(string reason) => BothAsync(async h =>
        {
            const ulong First = 1764000000, Second = 1764001800;

            await h.Store.SaveDetectionAsync(Detection(Noon, Queued(GuildA, PostSource.Maps, MapId, Noon, revision: First), Queued(GuildB, PostSource.Maps, MapId, Noon, revision: First)));

            var a = await IdOf(h, GuildA, PostSource.Maps, MapId);
            var b = await IdOf(h, GuildB, PostSource.Maps, MapId);

            await h.Store.ClaimAsync(Claim(1, GuildA, PostSource.Maps, [a], Noon.AddMinutes(1), shows: [new(MapId, Second)]));
            await h.Store.SettleAsync(Refused(1, Noon.AddMinutes(2)));
            await h.Store.ClaimAsync(Claim(2, GuildB, PostSource.Maps, [b], Noon.AddMinutes(1), shows: [new(MapId, Second)]));
            await h.Store.SettleAsync(Refused(2, Noon.AddMinutes(2)));

            var open = await h.Store.ListOpenAsync();
            Assert.Equal(1, await h.Store.DropAsync([.. open.Where(row => row.guild_id == GuildA && row.revision == First).Select(row => row.id)], DeliveryReasons.SwitchedOff, Noon.AddMinutes(3)));
            Assert.Equal(1, await h.Store.DropAsync([.. open.Where(row => row.guild_id == GuildA && row.revision == Second).Select(row => row.id)], reason, Noon.AddMinutes(3)));
            Assert.Equal(2, await h.Store.DropAsync([.. open.Where(row => row.guild_id == GuildB).Select(row => row.id)], DeliveryReasons.Hidden, Noon.AddMinutes(3)));

            var update = Detection(Noon.AddDays(1), Queued(GuildA, PostSource.Maps, MapId, Noon.AddDays(1), revision: Second) with { is_new = false },
                Queued(GuildB, PostSource.Maps, MapId, Noon.AddDays(1), revision: Second) with { is_new = false });

            await h.Store.SaveDetectionAsync(update);
            await h.Store.SaveDetectionAsync(update);

            var back = Assert.Single(await h.Store.ListOpenAsync());
            Assert.Equal((GuildA, Second, false, Noon.AddDays(1), (ulong?)null, (string?)null, (DateTime?)null),
                (back.guild_id, back.revision, back.is_new, back.queued_at, back.delivery_id, back.reason, back.settled_at));
        });

        // A message counts the items its rows are for, which the pages show, not the rows: a map's rows for two of its updates,
        // which a message that showed the update and wasn't made gives back, are one map in the message that takes them again
        [Fact]
        public Task MessageCountsTheItemsItsRowsAreFor() => BothAsync(async h =>
        {
            const ulong First = 1764000000, Second = 1764001800;

            await h.Store.SaveDetectionAsync(Detection(Noon, Queued(GuildA, PostSource.Maps, MapId, Noon, revision: First), Queued(GuildA, PostSource.Maps, MapId + 1, Noon, revision: First)));
            await h.Store.ClaimAsync(Claim(1, GuildA, PostSource.Maps, [.. (await h.Store.ListOpenAsync()).Select(row => row.id)], Noon.AddMinutes(1), shows: [new(MapId, Second)]));
            await h.Store.SettleAsync(new Settlement { AttemptKey = 1, Outcome = DeliveryState.Failed, At = Noon.AddMinutes(2), Reason = DeliveryReasons.NotSent });

            var again = (await h.Store.ListOpenAsync()).Select(row => row.id).ToArray();
            await h.Store.ClaimAsync(Claim(2, GuildA, PostSource.Maps, again, Noon.AddMinutes(5)));

            Assert.Equal(3, again.Length);
            Assert.Equal((ushort)2, Assert.Single((await h.Store.LoadAsync(new PostingLoad())).Unsettled).item_count);
        });

        // A refusal the cache saw first is recorded once and counts, its items stay pending, and a refresh or a test message
        // that went ends the wait of the server it's about, all of its kinds' or the channel's only
        [Fact]
        public Task RefusalCountsOnceAndItsWaitEnds() => BothAsync(async h =>
        {
            await h.Store.SaveDetectionAsync(Detection(Noon, Queued(GuildA, PostSource.Armor, 11, Noon), Queued(GuildA, PostSource.Maps, MapId, Noon),
                Queued(GuildB, PostSource.Armor, 11, Noon)));

            Assert.True(await h.Store.RecordRefusalAsync(Refusal(1, GuildA, PostSource.Armor, ChannelA, Noon.AddSeconds(1))));
            Assert.False(await h.Store.RecordRefusalAsync(Refusal(1, GuildA, PostSource.Armor, ChannelA, Noon.AddSeconds(2))));
            Assert.True(await h.Store.RecordRefusalAsync(Refusal(2, GuildA, PostSource.Maps, ChannelB, Noon.AddSeconds(3))));
            Assert.True(await h.Store.RecordRefusalAsync(Refusal(3, GuildB, PostSource.Armor, ChannelA, Noon.AddSeconds(4))));

            Assert.Equal(3, (await h.Store.ListOpenAsync()).Count);

            Assert.Equal(0, await h.Store.EndWaitsAsync(GuildA, 9999, Noon.AddMinutes(1)));
            Assert.Equal(1, await h.Store.EndWaitsAsync(GuildA, ChannelB, Noon.AddMinutes(1)));
            Assert.Equal(1, await h.Store.EndWaitsAsync(GuildA, null, Noon.AddMinutes(2)));
            Assert.Equal(0, await h.Store.EndWaitsAsync(GuildA, null, Noon.AddMinutes(3)));

            var statuses = (await h.Store.LoadAsync(new PostingLoad())).Statuses;
            Assert.Equal([(GuildA, PostSource.Armor, (DateTime?)null, 1u), (GuildA, PostSource.Maps, null, 1u), (GuildB, PostSource.Armor, Noon.AddMinutes(5).AddSeconds(4), 1u)],
                statuses.Select(row => (row.guild_id, row.source, row.next_try_at, row.failures)));
            Assert.Equal(((byte?)DeliveryReasons.SendMessages, DeliveryReasons.MissingPermissions), (statuses[0].last_missing, statuses[0].last_reason));
        });

        // A drop takes pending rows only, and a kind that's off stops failing, keeping its last failure
        [Fact]
        public Task DropsTakePendingRowsAndOffKindsStopFailing() => BothAsync(async h =>
        {
            await h.Store.SaveDetectionAsync(Detection(Noon, Queued(GuildA, PostSource.Armor, 11, Noon), Queued(GuildA, PostSource.Armor, 12, Noon)));

            var a11 = await IdOf(h, GuildA, PostSource.Armor, 11);
            var a12 = await IdOf(h, GuildA, PostSource.Armor, 12);

            await h.Store.ClaimAsync(Claim(1, GuildA, PostSource.Armor, [a12], Noon.AddSeconds(1)));
            Assert.Equal(1, await h.Store.DropAsync([a11, a12, 999999], DeliveryReasons.SwitchedOff, Noon.AddMinutes(1)));
            Assert.Equal(0, await h.Store.DropAsync([a11], DeliveryReasons.TooOld, Noon.AddMinutes(2)));
            Assert.Equal(0, await h.Store.DropAsync([], DeliveryReasons.TooOld, Noon.AddMinutes(2)));

            Assert.True(await h.Store.RecordRefusalAsync(Refusal(2, GuildA, PostSource.Maps, ChannelA, Noon.AddSeconds(2))));
            Assert.Equal(0, await h.Store.ClearFailingAsync(GuildA, PostSource.Armor, Noon.AddMinutes(3)));
            Assert.Equal(1, await h.Store.ClearFailingAsync(GuildA, PostSource.Maps, Noon.AddMinutes(3)));
            Assert.Equal(0, await h.Store.ClearFailingAsync(GuildA, PostSource.Maps, Noon.AddMinutes(4)));

            var status = Assert.Single((await h.Store.LoadAsync(new PostingLoad())).Statuses);
            Assert.Equal(((DateTime?)null, 0u, (DateTime?)null, (ulong?)null, (DateTime?)Noon.AddSeconds(2), DeliveryReasons.MissingPermissions, Noon.AddMinutes(3)),
                (status.failing_since, status.failures, status.next_try_at, status.waiting_on, status.last_failed_at, status.last_reason, status.updated_at));
            Assert.Empty(await h.Store.ListOpenAsync());
        });

        // Armor approved again: only its rows dropped as not approved go back to pending, queued from then. A row that was sent
        // or dropped for another reason stays, and so does another kind's.
        [Fact]
        public Task OnlyWithdrawnArmorIsReopened() => BothAsync(async h =>
        {
            await h.Store.SaveDetectionAsync(Detection(Noon, Queued(GuildA, PostSource.Armor, 11, Noon), Queued(GuildB, PostSource.Armor, 11, Noon),
                Queued(GuildA, PostSource.Armor, 12, Noon), Queued(GuildA, PostSource.Maps, 11, Noon)));

            var a11 = await IdOf(h, GuildA, PostSource.Armor, 11);
            var b11 = await IdOf(h, GuildB, PostSource.Armor, 11);
            var a12 = await IdOf(h, GuildA, PostSource.Armor, 12);
            var map = await IdOf(h, GuildA, PostSource.Maps, 11);

            await h.Store.ClaimAsync(Claim(1, GuildB, PostSource.Armor, [b11], Noon.AddSeconds(1)));
            await h.Store.SettleAsync(Sent(1, 5000000000000000001, Noon.AddSeconds(2)));
            await h.Store.DropAsync([a11, map], DeliveryReasons.NotApproved, Noon.AddMinutes(1));
            await h.Store.DropAsync([a12], DeliveryReasons.SwitchedOff, Noon.AddMinutes(1));

            var state = await h.Store.LoadAsync(new PostingLoad { ArmorIds = [11, 12] });
            Assert.Equal([a11], state.Withdrawn.Select(row => row.id));

            var reopen = new Detection { At = Noon.AddDays(1), ReopenArmor = [11, 12] };
            await h.Store.SaveDetectionAsync(reopen);
            await h.Store.SaveDetectionAsync(reopen);

            var open = Assert.Single(await h.Store.ListOpenAsync());
            Assert.Equal((a11, Noon.AddDays(1), (string?)null, (DateTime?)null), (open.id, open.queued_at, open.reason, open.settled_at));
            Assert.Empty((await h.Store.LoadAsync(new PostingLoad { ArmorIds = [11, 12] })).Withdrawn);
        });

        // A map shown again: of the rows given, only the ones dropped because Steam didn't show the map go back to pending,
        // queued from then, and only those are read as a map's hidden rows. A row that was sent, dropped for another reason or
        // is another kind's stays.
        [Fact]
        public Task OnlyHiddenMapsAreReopened() => BothAsync(async h =>
        {
            await h.Store.SaveDetectionAsync(Detection(Noon, Queued(GuildA, PostSource.Maps, MapId, Noon, revision: 1764000000),
                Queued(GuildB, PostSource.Maps, MapId, Noon, revision: 1764000000), Queued(GuildA, PostSource.Maps, MapId + 1, Noon, revision: 1764000000),
                Queued(GuildA, PostSource.Armor, 11, Noon)));

            var a = await IdOf(h, GuildA, PostSource.Maps, MapId);
            var b = await IdOf(h, GuildB, PostSource.Maps, MapId);
            var other = await IdOf(h, GuildA, PostSource.Maps, MapId + 1);
            var armor = await IdOf(h, GuildA, PostSource.Armor, 11);

            await h.Store.ClaimAsync(Claim(1, GuildB, PostSource.Maps, [b], Noon.AddSeconds(1)));
            await h.Store.SettleAsync(Sent(1, 5000000000000000001, Noon.AddSeconds(2)));
            await h.Store.DropAsync([a, armor], DeliveryReasons.Hidden, Noon.AddMinutes(1));
            await h.Store.DropAsync([other], DeliveryReasons.TooOld, Noon.AddMinutes(1));

            Assert.Equal([(a, GuildA, 1764000000UL)], (await h.Store.ListHiddenAsync([MapId, MapId + 1, 11])).Select(row => (row.id, row.guild_id, row.revision)));

            var reopen = new Detection { At = Noon.AddDays(1), ReopenMaps = [a, b, other, armor] };
            await h.Store.SaveDetectionAsync(reopen);
            await h.Store.SaveDetectionAsync(reopen);

            var open = Assert.Single(await h.Store.ListOpenAsync());
            Assert.Equal((a, Noon.AddDays(1), (ulong?)null, (string?)null, (DateTime?)null), (open.id, open.queued_at, open.delivery_id, open.reason, open.settled_at));
            Assert.Empty(await h.Store.ListHiddenAsync([MapId]));
        });

        // Runs of the job an older bot made after the time given, by their detail: none, only "asked for on request N", or PR
        // 9's maps that waited and weren't found, asked for or not. This bot's details, runs that failed or were cut off,
        // earlier runs and other jobs don't count.
        [Fact]
        public Task OlderBotsRunsAreCountedByTheirDetail() => BothAsync(async h =>
        {
            var since = Noon;
            uint seq = 0;

            async Task Run(string? detail, string result = JobResult.Ok, string job = Job, int minutes = 5) =>
                await h.AddJobRunAsync(++seq, job, since.AddMinutes(minutes), result, detail);

            await Run(null);
            await Run("asked for on request 12");
            await Run("1 map that waited wasn't in Steam's latest list any more, so it wasn't posted: it was hidden meanwhile, or more maps were updated while posts waited than the read reaches");
            await Run("asked for on request 5: 3 maps that waited weren't in Steam's latest list any more, so they weren't posted: they were hidden meanwhile, or more maps were updated while posts waited than the read reaches");
            await Run("asked for on request 12: nothing new to post");
            await Run("nothing new to post");
            await Run("took the first baseline of armor, maps and lobbies (122, 20 and 3 items) and posted nothing");
            await Run(null, JobResult.Failed);
            await Run("still running when the time limit of 270 s cut it off", JobResult.CutOff);
            await Run(null, minutes: 0);
            await Run(null, job: "HousekeepingJob.ApplyRetention");

            Assert.Equal(4, await h.Store.CountOlderBotRunsAsync(Job, since));
            Assert.Equal(0, await h.Store.CountOlderBotRunsAsync(Job, since.AddMinutes(5)));
        });

        // Every server's switches, and one server's, as saved
        [Fact]
        public Task SwitchesAreReadAsSaved() => BothAsync(async h =>
        {
            var off = Switches(GuildB, armor: false, version: 3);
            var on = Switches(GuildA, lobbies: true);

            await h.SetSwitchesAsync(off);
            await h.SetSwitchesAsync(on);

            Assert.Equal([on, off], (await h.Store.LoadAsync(new PostingLoad())).Switches);
            Assert.Equal(off, await h.Store.GetSwitchesAsync(GuildB));
            Assert.Null(await h.Store.GetSwitchesAsync(999));
            Assert.False(off.IsOn(PostSource.Armor));
            Assert.True(off.IsOn(PostSource.Maps));
        });

        // What either store refuses, it refuses before it changes anything
        [Fact]
        public Task WhatCantBeRightIsRefusedAndChangesNothing() => BothAsync(async h =>
        {
            await h.Store.SaveDetectionAsync(Detection(Noon, Queued(GuildA, PostSource.Armor, 11, Noon)));
            var a11 = await IdOf(h, GuildA, PostSource.Armor, 11);
            var local = DateTime.SpecifyKind(Noon, DateTimeKind.Local);

            var calls = new Func<Task>[]
            {
                () => h.Store.SaveDetectionAsync(Detection(local, Queued(GuildA, PostSource.Armor, 12, Noon))),
                () => h.Store.SaveDetectionAsync(Detection(Noon, Queued(GuildA, PostSource.Armor, 12, Noon), Queued(GuildA, PostSource.Armor, 12, Noon))),
                () => h.Store.SaveDetectionAsync(Detection(Noon, Queued(GuildA, PostSource.Submissions, 12, Noon))),
                () => h.Store.SaveDetectionAsync(Detection(Noon, Queued(GuildA, PostSource.Armor, 12, Noon, revision: 7))),
                () => h.Store.SaveDetectionAsync(new Detection { At = Noon, Seen = [Seen(PostSource.Armor, 12, SeenState.Alerted, Noon)] }),
                () => h.Store.SaveDetectionAsync(new Detection { At = Noon, Seen = [Seen(PostSource.Submissions, 12, SeenState.Queued, Noon)] }),
                () => h.Store.SaveDetectionAsync(new Detection { At = Noon, Baselines = [Baseline(PostSource.Armor, Noon, time: Noon)] }),
                () => h.Store.SaveDetectionAsync(new Detection { At = Noon, Baselines = [Baseline(PostSource.Armor, Noon) with { reason = "again" }] }),
                () => h.Store.SaveDetectionAsync(new Detection { At = Noon, ReopenMaps = [0] }),
                () => h.Store.SaveDetectionAsync(new Detection { At = Noon, ReopenMaps = [a11, a11] }),
                () => h.Store.ClaimAsync(Claim(1, GuildA, PostSource.Armor, [], Noon)),
                () => h.Store.ClaimAsync(Claim(1, GuildA, PostSource.Armor, [a11, a11], Noon)),
                () => h.Store.ClaimAsync(Claim(1, GuildA, PostSource.Armor, [a11], Noon, part: 0)),
                () => h.Store.ClaimAsync(Claim(0, GuildA, PostSource.Armor, [a11], Noon)),
                () => h.Store.ClaimAsync(Claim(1, GuildA, PostSource.Armor, [a11], Noon, shows: [new(MapId, 1764000000)])),
                () => h.Store.ClaimAsync(Claim(1, GuildA, PostSource.Maps, [a11], Noon, shows: [new(0, 1764000000)])),
                () => h.Store.ClaimAsync(Claim(1, GuildA, PostSource.Maps, [a11], Noon, shows: [new(MapId, 0)])),
                () => h.Store.ClaimAsync(Claim(1, GuildA, PostSource.Maps, [a11], Noon, shows: [new(MapId, 1764000000), new(MapId, 1764001800)])),
                () => h.Store.SettleAsync(new Settlement { AttemptKey = 1, Outcome = DeliveryState.Sent, At = Noon }),
                () => h.Store.SettleAsync(new Settlement { AttemptKey = 1, Outcome = DeliveryState.Sending, At = Noon }),
                () => h.Store.SettleAsync(new Settlement { AttemptKey = 1, Outcome = DeliveryState.Failed, At = Noon, Reason = DeliveryReasons.SwitchedOff }),
                () => h.Store.SettleAsync(new Settlement { AttemptKey = 1, Outcome = DeliveryState.Sent, At = Noon, MessageId = 1, Counts = true }),
                () => h.Store.SettleAsync(new Settlement { AttemptKey = 1, Outcome = DeliveryState.Failed, At = Noon, Reason = DeliveryReasons.RateLimited, NextTryAt = Noon }),
                () => h.Store.SettleAsync(new Settlement { AttemptKey = 1, Outcome = DeliveryState.Failed, At = Noon, Reason = DeliveryReasons.Refused, Missing = 2 }),
                () => h.Store.RecordRefusalAsync(new Refusal { AttemptKey = 1, RunId = RunId, RunStartedAt = Noon, GuildId = GuildA, ChannelId = ChannelA, Source = PostSource.Armor,
                    ItemCount = 1, At = Noon, Reason = DeliveryReasons.MissingPermissions, Missing = 0 }),
                () => h.Store.RecordRefusalAsync(Refusal(1, GuildA, PostSource.Armor, 0, Noon)),
                () => h.Store.DropAsync([a11], DeliveryReasons.MissingAccess, Noon),
                () => h.Store.DropAsync([a11], DeliveryReasons.TooOld, local),
                () => h.Store.ClearFailingAsync(GuildA, PostSource.Submissions, Noon),
                () => h.Store.EndWaitsAsync(GuildA, 0, Noon),
                () => h.Store.MarkCheckedAsync(1, 0),
                () => h.Store.CountOlderBotRunsAsync("", Noon),
                () => h.Store.ListSeenAsync("armour", [11]),
                () => h.Store.ListHiddenAsync(Enumerable.Range(1, NotificationData.IdLimit + 1).Select(id => (ulong)id).ToList()),
                () => h.Store.ListMessageIdsAsync(GuildA, ChannelA, local),
                () => h.Store.LoadAsync(new PostingLoad { LobbyIds = Enumerable.Range(1, NotificationData.IdLimit + 1).Select(id => (ulong)id).ToList() })
            };

            foreach (var call in calls)
                await Assert.ThrowsAnyAsync<ArgumentException>(call);

            Assert.Equal([a11], (await h.Store.ListOpenAsync()).Select(row => row.id));
            Assert.Empty((await h.Store.LoadAsync(new PostingLoad())).Unsettled);
        });

        // A store, with what a scenario needs beside the contract: switches as the dashboard saves them, the job's runs as
        // JobHandler records them, and everything held, read the same way from both
        abstract class Harness
        {
            public abstract INotificationData Store { get; }
            public abstract Task SetSwitchesAsync(GuildSwitches row);
            public abstract Task AddJobRunAsync(uint seq, string job, DateTime startedAt, string result, string? detail);
            public abstract Task<Snapshot> SnapshotAsync();
        }

        sealed class MemoryHarness : Harness
        {
            readonly MemoryNotificationData store = new();

            public override INotificationData Store => store;

            public override Task SetSwitchesAsync(GuildSwitches row)
            {
                store.SetSwitches(row);
                return Task.CompletedTask;
            }

            public override Task AddJobRunAsync(uint seq, string job, DateTime startedAt, string result, string? detail)
            {
                store.AddJobRun(job, startedAt, result, detail);
                return Task.CompletedTask;
            }

            public override Task<Snapshot> SnapshotAsync()
            {
                var keys = store.Deliveries.ToDictionary(row => row.id, row => row.attempt_key);

                return Task.FromResult(new Snapshot(store.Baselines, store.Switches, store.Seen,
                    [.. store.Queue.Select(row => new QueueSnap
                    {
                        guild_id = row.guild_id,
                        source = row.source,
                        item_id = row.item_id,
                        revision = row.revision,
                        is_new = row.is_new,
                        queued_at = row.queued_at,
                        state = row.state,
                        attempt_key = row.delivery_id is { } id ? keys[id] : null,
                        reason = row.reason,
                        settled_at = row.settled_at
                    })],
                    [.. store.Deliveries.Select(row => row with { id = 0 })],
                    store.Statuses));
            }
        }

        sealed class SqlHarness(MariaDbFixture db) : Harness
        {
            public override INotificationData Store { get; } = db.Notifications();

            public override Task SetSwitchesAsync(GuildSwitches row) => db.RootExecuteAsync(
                $"INSERT INTO {MariaDbFixture.SwitchTable} ({NotificationData.SwitchColumns}) VALUES (@guild_id, @armor_on, @maps_on, @lobbies_on, @version, @changed_at, @changed_by, @changed_by_role)",
                row);

            public override Task AddJobRunAsync(uint seq, string job, DateTime startedAt, string result, string? detail) => db.RootExecuteAsync(
                $"INSERT INTO {MariaDbFixture.JobRunTable} (run_id, seq, job, started_at, result, detail) VALUES (@run, @seq, @job, @startedAt, @result, @detail)",
                new { run = OtherRunId, seq, job, startedAt, result, detail });

            public override async Task<Snapshot> SnapshotAsync() => new(
                await db.RootQueryAsync<BaselineRow>($"SELECT {NotificationData.BaselineColumns} FROM {MariaDbFixture.BaselineTable} ORDER BY source"),
                await db.RootQueryAsync<GuildSwitches>($"SELECT {NotificationData.SwitchColumns} FROM {MariaDbFixture.SwitchTable} ORDER BY guild_id"),
                await db.RootQueryAsync<SeenRow>($"SELECT {NotificationData.SeenColumns} FROM {MariaDbFixture.SeenTable} ORDER BY source, item_id"),
                await db.RootQueryAsync<QueueSnap>($@"SELECT q.guild_id, q.source, q.item_id, q.revision, q.is_new, q.queued_at, q.state, d.attempt_key, q.reason, q.settled_at
                                                      FROM {MariaDbFixture.QueueTable} q LEFT JOIN {MariaDbFixture.DeliveryTable} d ON d.id = q.delivery_id ORDER BY q.id"),
                [.. (await db.RootQueryAsync<DeliveryRow>($"SELECT {NotificationData.DeliveryColumns} FROM {MariaDbFixture.DeliveryTable} ORDER BY id")).Select(row => row with { id = 0 })],
                await db.RootQueryAsync<DeliveryStatusRow>($"SELECT {NotificationData.StatusColumns} FROM {MariaDbFixture.DeliveryStatusTable} ORDER BY guild_id, source"));
        }

        sealed record Snapshot(IReadOnlyList<BaselineRow> Baselines, IReadOnlyList<GuildSwitches> Switches, IReadOnlyList<SeenRow> Seen, IReadOnlyList<QueueSnap> Queue,
            IReadOnlyList<DeliveryRow> Deliveries, IReadOnlyList<DeliveryStatusRow> Statuses);

        // A queued item without its ID, and with the attempt of the message that holds it in place of that message's ID
        public sealed record QueueSnap
        {
            public ulong guild_id { get; set; }
            public string source { get; set; } = "";
            public ulong item_id { get; set; }
            public ulong revision { get; set; }
            public bool is_new { get; set; }
            public DateTime queued_at { get; set; }
            public string state { get; set; } = "";
            public ulong? attempt_key { get; set; }
            public string? reason { get; set; }
            public DateTime? settled_at { get; set; }
        }

        static async Task<ulong> IdOf(Harness h, ulong guildId, string source, ulong itemId) =>
            (await h.Store.ListOpenAsync()).Single(row => row.guild_id == guildId && row.source == source && row.item_id == itemId).id;

        static BaselineRow Baseline(string source, DateTime at, uint count = 2, DateTime? time = null) => new()
        {
            source = source,
            taken_at = at,
            taken_by_run = RunId,
            reason = BaselineReason.First,
            item_count = count,
            baseline_time = time,
            checked_at = at,
            checked_by_run = RunId
        };

        static SeenRow Seen(string source, ulong id, string state, DateTime at, DateTime? updated = null, DateTime? queued = null) => new()
        {
            source = source,
            item_id = id,
            state = state,
            first_seen_at = at,
            last_seen_at = at,
            updated_at = updated,
            queued_at = queued
        };

        static QueueItem Queued(ulong guildId, string source, ulong id, DateTime at, ulong revision = 0) => new()
        {
            guild_id = guildId,
            source = source,
            item_id = id,
            revision = revision,
            is_new = true,
            queued_at = at
        };

        static Detection Detection(DateTime at, params QueueItem[] queue) => new() { At = at, Queue = queue };

        static DeliveryClaim Claim(ulong key, ulong guildId, string source, ulong[] ids, DateTime at, ulong channelId = ChannelA, byte part = 1, ulong runId = RunId,
            ShownUpdate[]? shows = null) => new()
        {
            AttemptKey = key,
            RunId = runId,
            RunStartedAt = at.AddSeconds(-1),
            GuildId = guildId,
            ChannelId = channelId,
            Source = source,
            Part = part,
            AttemptedAt = at,
            QueueIds = ids,
            Shows = shows ?? []
        };

        static Settlement Sent(ulong key, ulong messageId, DateTime at) => new() { AttemptKey = key, Outcome = DeliveryState.Sent, At = at, MessageId = messageId };

        // Discord's 403 with its code for a missing permission, which counts against the server, with the wait given
        static Settlement Refused(ulong key, DateTime at, DateTime? next = null) => new()
        {
            AttemptKey = key,
            Outcome = DeliveryState.Failed,
            At = at,
            Reason = DeliveryReasons.DiscordPermissions,
            HttpStatus = 403,
            DiscordCode = 50013,
            Counts = true,
            NextTryAt = next,
            WaitingOn = 77
        };

        static Refusal Refusal(ulong key, ulong guildId, string source, ulong channelId, DateTime at) => new()
        {
            AttemptKey = key,
            RunId = RunId,
            RunStartedAt = at.AddSeconds(-1),
            GuildId = guildId,
            ChannelId = channelId,
            Source = source,
            ItemCount = 1,
            At = at,
            Reason = DeliveryReasons.MissingPermissions,
            Missing = DeliveryReasons.SendMessages,
            NextTryAt = at.AddMinutes(5),
            WaitingOn = 42
        };

        static GuildSwitches Switches(ulong guildId, bool armor = true, bool maps = true, bool lobbies = false, uint version = 1) => new()
        {
            guild_id = guildId,
            armor_on = armor,
            maps_on = maps,
            lobbies_on = lobbies,
            version = version,
            changed_at = Noon,
            changed_by = 1000000000000000009,
            changed_by_role = "owner"
        };
    }
}
