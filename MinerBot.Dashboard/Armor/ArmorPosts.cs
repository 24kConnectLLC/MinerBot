using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using MinerBot.Dashboard.Servers;
using MinerBot.Shared.Delivery;

namespace MinerBot.Dashboard.Armor
{
    // One server's line under Where it was posted: the server's name as the bot's copy has it, null when the copy doesn't
    // have it, what became of the armor there, and the message's link once it was posted
    public sealed record ArmorPostLine(ulong GuildId, string? Name, string Words, string? Link);

    // Where an armor went, from the bot's record, which the review page reads and the review's transaction reads again before
    // a move back to pending from Approved: the bot's seen row of it, and each server's row in the queue
    internal static class ArmorPosts
    {
        // Posted, or counted as posted: a row that holds a message, sent, unconfirmed, or still being sent or checked, or the
        // armor in the record's baseline, which the bot takes as posted before its record began. A move back to pending can't
        // undo either.
        public static bool Posted(ItemPosts record) => record.Seen is { state: SeenState.Baseline } || PostedServers(record) > 0;

        public static int PostedServers(ItemPosts record) =>
            record.Servers.Where(row => row.state is QueueState.Sending or QueueState.Sent).Select(row => row.guild_id).Distinct().Count();

        public static IReadOnlyList<ArmorPostLine> Lines(ItemPosts record, IReadOnlyDictionary<ulong, string> names, DateTime now) =>
            record.Servers.Select(row => new ArmorPostLine(row.guild_id, names.GetValueOrDefault(row.guild_id), Words(row, now), Link(row))).ToList();

        // A post that may not have arrived says so by its message, and by its row's reason once the night's clean-up has
        // deleted the record of the message, 90 days on, while armor's rows stay; a post that went is shown at its row's
        // settle then
        static string Words(ItemServerRow row, DateTime now) => row.state switch
        {
            QueueState.Sent when row.delivery_state == DeliveryState.Unconfirmed || row.reason == DeliveryReasons.Unconfirmed => ArmorWords.MayNotHaveArrived,
            QueueState.Sent => ArmorWords.PostedAt(row.attempted_at ?? row.settled_at ?? row.queued_at),
            QueueState.Sending => ArmorWords.BeingChecked,
            QueueState.Pending when row.next_try_at is { } next && next > now => ArmorWords.WaitingUntil(next),
            QueueState.Pending => ArmorWords.Waiting,
            QueueState.Dropped => ArmorWords.NotPosted(row.reason),
            _ => ArmorWords.UnknownPostState
        };

        // Built from the three numbers, never from text
        static string? Link(ItemServerRow row) => row is { state: QueueState.Sent, delivery_state: DeliveryState.Sent, channel_id: { } channel, message_id: { } message }
            ? DeliveryWords.MessageLink(row.guild_id, channel, message)
            : null;
    }
}
