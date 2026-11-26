using DataAccessLibrary.Posting.Models;

namespace DataAccessLibrary.Posting
{
    // The notification run's record of what it found and what each server is owed, so a restart loses no post and a crash
    // doubles none: NotificationData in MinerBot's database, and MemoryNotificationData, the same rules in memory, for a bot
    // without BotConnection and for the bot's tests. NotificationStoreContractTests runs one list of scenarios against both.
    //
    // The rules each keeps:
    // - a message is sent only after its claim has committed, and a claim is a conditional change of pending rows, so two
    //   claims, or two processes, never take one row: a claim that finds one of its rows taken takes none (ClaimLostException)
    // - a row leaves pending only by a claim or a drop, and goes back from sending to pending only when its message is known
    //   not to have been made
    // - a server's kind with a message not settled yet, sending or in doubt, gets no new one (the caller's rule, from Unsettled)
    // - every write is safe to repeat: a detection writes whole rows and queues by the queue's unique key, as a claim queues
    //   the updates its map post shows, a claim, a settle and a refusal land once by their attempt's key, and the rest say in
    //   their WHERE when they apply
    // - every list is bounded, and the pending rows come oldest first, so the ones a run doesn't reach go first next time
    public interface INotificationData
    {
        // Step 4 of a run: everything it decides from
        Task<PostingState> LoadAsync(PostingLoad load);

        // The pending rows as they're committed now, oldest first, at most OpenLimit
        Task<IReadOnlyList<QueueRow>> ListOpenAsync();

        // Every source's baseline, and the seen rows of one source's items, for the new-submission alerts
        Task<IReadOnlyList<BaselineRow>> ListBaselinesAsync();
        Task<IReadOnlyList<SeenRow>> ListSeenAsync(string source, IReadOnlyCollection<ulong> itemIds);

        // The rows of these maps that were dropped because Steam didn't show the map any more, by map, server and row: a run
        // reopens the ones whose map it reads again with the update they're for
        Task<IReadOnlyList<QueueRow>> ListHiddenAsync(IReadOnlyCollection<ulong> mapIds);

        // How many runs of the job an older bot finished well after the time given: ones with no detail, only "asked for on
        // request N", or a PR 9 bot's lost maps, which this bot's runs never return
        Task<int> CountOlderBotRunsAsync(string job, DateTime since);

        // What a run, or a look for new submissions, found, in one transaction
        Task SaveDetectionAsync(Detection detection);

        // Pending rows dropped with a reason, and how many were still pending
        Task<int> DropAsync(IReadOnlyCollection<ulong> queueIds, string reason, DateTime at);

        // A kind that's off now: its status stops failing and waiting, and keeps its last failure as history. 1 when it was
        // failing or waiting.
        Task<int> ClearFailingAsync(ulong guildId, string source, DateTime at);

        // The message's row, sending, with its rows taken from pending and the server's rows for the updates it shows queued
        // with it, in one transaction; its new ID. Throws ClaimLostException, having taken nothing, when a row isn't pending or
        // isn't the server's and kind's.
        Task<ulong> ClaimAsync(DeliveryClaim claim);

        // True when this call settled the message, false when it was settled already or never claimed
        Task<bool> SettleAsync(Settlement settlement);

        // True when this call recorded the refusal, false when its attempt was recorded already
        Task<bool> RecordRefusalAsync(Refusal refusal);

        // A look in the channel's history that couldn't tell: the message is in doubt, and the count of looks is the one given.
        // False when it was settled meanwhile, or the count already reached that.
        Task<bool> MarkCheckedAsync(ulong attemptKey, byte checks);

        // A refresh of the server's copy, or a test message that went to the channel given: the server's waits end, all of its
        // kinds' or only those that last failed in that channel. How many ended.
        Task<int> EndWaitsAsync(ulong guildId, ulong? channelId, DateTime at);

        // One server's switches, null when it has none, which means every kind is on
        Task<GuildSwitches?> GetSwitchesAsync(ulong guildId);

        // The messages the bot's rows already hold in one channel, tried since the time given, oldest first, at most
        // MessageLimit: the look in a channel's history takes only a message no other row holds
        Task<IReadOnlyList<ulong>> ListMessageIdsAsync(ulong guildId, ulong channelId, DateTime since);

        // The items a message not settled yet carries, by their queue rows, at most ClaimLimit: the look in a channel's
        // history knows a map post by them
        Task<IReadOnlyList<ulong>> ListCarriedAsync(ulong deliveryId);
    }

    // A claim that found one of its rows no longer pending, so it took none of them and nothing may be sent
    public sealed class ClaimLostException(string message) : Exception(message)
    {
    }
}
