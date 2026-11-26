using DataAccessLibrary;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using DataAccessLibrary.Tests.Database;

namespace MinerBot.Dashboard.Tests.Support
{
    // The bot's record of its posts as the bot writes it, through its own store on the fixture, so the pages read what the
    // bot really writes, and a server's switches as the dashboard's own repository saves them. Times are given, from the
    // factory's clock.
    internal static class PostSteps
    {
        // Each attempt's key, as the bot draws its own at random
        static long keys = 7000;

        static ulong NextKey() => (ulong)Interlocked.Increment(ref keys);

        // An item the bot found and queued for the servers given, with the seen row a notification run writes; each server's
        // row in the queue
        public static async Task<IReadOnlyList<QueueRow>> QueueAsync(MariaDbFixture db, string source, ulong itemId, DateTime at, params ulong[] servers)
        {
            var store = db.Notifications();

            await store.SaveDetectionAsync(new Detection
            {
                At = at,
                Seen = [new SeenRow { source = source, item_id = itemId, state = SeenState.Queued, first_seen_at = at, last_seen_at = at }],
                Queue = [.. servers.Select(server => new QueueItem { guild_id = server, source = source, item_id = itemId, is_new = true, queued_at = at })]
            });

            return [.. (await store.ListOpenAsync()).Where(row => row.source == source && row.item_id == itemId)];
        }

        // Armor that was approved before the bot's record began, which it takes as posted
        public static Task BaselineAsync(MariaDbFixture db, ulong armorId, DateTime at) => db.Notifications().SaveDetectionAsync(new Detection
        {
            At = at,
            Seen = [new SeenRow { source = PostSource.Armor, item_id = armorId, state = SeenState.Baseline, first_seen_at = at, last_seen_at = at }]
        });

        // A message of one server carrying those rows, claimed as the bot claims it before the send; its attempt's key
        public static async Task<ulong> ClaimAsync(MariaDbFixture db, IReadOnlyList<QueueRow> rows, ulong channelId, DateTime at)
        {
            var key = NextKey();

            await db.Notifications().ClaimAsync(new DeliveryClaim
            {
                AttemptKey = key,
                RunId = StatusSteps.RunId,
                RunStartedAt = at,
                GuildId = rows[0].guild_id,
                ChannelId = channelId,
                Source = rows[0].source,
                Part = 1,
                AttemptedAt = at,
                QueueIds = [.. rows.Select(row => row.id)]
            });

            return key;
        }

        // The same, settled as Discord answered: sent, with the message's ID
        public static async Task SentAsync(MariaDbFixture db, IReadOnlyList<QueueRow> rows, ulong channelId, DateTime at, ulong messageId)
        {
            var key = await ClaimAsync(db, rows, channelId, at);
            Assert.True(await db.Notifications().SettleAsync(new Settlement { AttemptKey = key, Outcome = DeliveryState.Sent, At = at, MessageId = messageId }));
        }

        // Refused by Discord for that server's own reason, which counts against it, with the wait before its next try
        public static async Task FailedAsync(MariaDbFixture db, IReadOnlyList<QueueRow> rows, ulong channelId, DateTime at, string reason, ushort httpStatus,
            uint? discordCode, DateTime nextTry)
        {
            var key = await ClaimAsync(db, rows, channelId, at);
            Assert.True(await db.Notifications().SettleAsync(new Settlement
            {
                AttemptKey = key,
                Outcome = DeliveryState.Failed,
                At = at,
                Reason = reason,
                HttpStatus = httpStatus,
                DiscordCode = discordCode,
                Counts = true,
                NextTryAt = nextTry,
                WaitingOn = 1
            }));
        }

        // Settled as the bot's look in the channel left it: in doubt, or unconfirmed
        public static async Task UnsettledAsync(MariaDbFixture db, IReadOnlyList<QueueRow> rows, ulong channelId, DateTime at, string outcome)
        {
            var key = await ClaimAsync(db, rows, channelId, at);
            Assert.True(await db.Notifications().SettleAsync(new Settlement { AttemptKey = key, Outcome = outcome, At = at }));
        }

        // Refused by the bot's copy of the server before any request, times over, each a failure that counts, the last waiting
        // until the time given
        public static async Task RefusedAsync(MariaDbFixture db, ulong guildId, string source, ulong channelId, DateTime at, string reason, byte? missing, int times,
            DateTime nextTry)
        {
            for (var time = 0; time < times; time++)
            {
                Assert.True(await db.Notifications().RecordRefusalAsync(new Refusal
                {
                    AttemptKey = NextKey(),
                    RunId = StatusSteps.RunId,
                    RunStartedAt = at.AddMinutes(5 * time),
                    GuildId = guildId,
                    ChannelId = channelId,
                    Source = source,
                    ItemCount = 1,
                    At = at.AddMinutes(5 * time),
                    Reason = reason,
                    Missing = missing,
                    NextTryAt = nextTry,
                    WaitingOn = 1
                }));
            }
        }

        // Dropped before any message, with the reason
        public static Task<int> DroppedAsync(MariaDbFixture db, IReadOnlyList<QueueRow> rows, string reason, DateTime at) =>
            db.Notifications().DropAsync([.. rows.Select(row => row.id)], reason, at);

        // A server's switches as the Owner saves them on its page, from no row or the version it has
        public static async Task SwitchesAsync(MariaDbFixture db, ulong guildId, bool armor, bool maps, bool lobbies, DateTime at)
        {
            var switches = db.Switches();
            var version = (await switches.GetSwitches(guildId))?.version ?? 0;

            var save = await switches.RunInTransaction(transaction => switches.SaveSwitches(new SwitchChange
            {
                GuildId = guildId,
                ArmorOn = armor,
                MapsOn = maps,
                LobbiesOn = lobbies,
                Version = version,
                At = at,
                ChangedBy = TestUsers.Owner.Id,
                Role = AuditRole.Owner
            }, transaction));

            Assert.NotEqual(SwitchSaveOutcome.Stale, save.Outcome);
        }
    }

    // The dashboard's repository of the switches, whose transactions run their work and commit, then lose the commit's answer,
    // as when the database stops answering at the commit: the save may or may not have landed
    internal sealed class LateSwitches(GuildSwitchData real) : IGuildSwitchData
    {
        public async Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work)
        {
            await real.RunInTransaction(work);
            throw new TimeoutException("The database didn't answer within 15 seconds");
        }

        public Task<IReadOnlyList<GuildSwitches>> ListSwitches() => real.ListSwitches();
        public Task<GuildSwitches?> GetSwitches(ulong guildId, DataTransaction? transaction = null) => real.GetSwitches(guildId, transaction);
        public Task<SwitchSave> SaveSwitches(SwitchChange change, DataTransaction transaction) => real.SaveSwitches(change, transaction);
    }

    // The dashboard's reads of the bot's record, failing as a missing right or a missing table would
    internal sealed class FailingDeliveries : IDeliveryReads
    {
        static Exception Failure() => new TimeoutException("The database didn't answer within 15 seconds");

        public Task<IReadOnlyList<DeliveryStatusRow>> ListStatuses(IReadOnlyCollection<ulong> guildIds) => Task.FromException<IReadOnlyList<DeliveryStatusRow>>(Failure());
        public Task<IReadOnlyList<DeliveryStatusRow>> ListFailing(uint minFailures) => Task.FromException<IReadOnlyList<DeliveryStatusRow>>(Failure());
        public Task<IReadOnlyList<DeliveryRow>> ListRecentPosts(ulong guildId, int take) => Task.FromException<IReadOnlyList<DeliveryRow>>(Failure());
        public Task<IReadOnlyList<DeliveryRow>> ListLastPosts(ulong guildId) => Task.FromException<IReadOnlyList<DeliveryRow>>(Failure());
        public Task<ItemPosts> GetItemPosts(string source, ulong itemId, DataTransaction? transaction = null) => Task.FromException<ItemPosts>(Failure());
    }
}
