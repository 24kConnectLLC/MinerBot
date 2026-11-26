namespace DataAccessLibrary.Posting.Models
{
    // What a notification run hands the posting store and gets from it. Every time is UTC, the bot's own clock, apart from
    // a map's update times and the maps baseline's, which are Steam's.

    // The items of this run's reads, whose seen rows the run needs: the approved armor and the open lobbies. Armor's are also
    // looked up for rows that were dropped because the armor wasn't approved any more. The maps are read after the load, as
    // how deep the read goes depends on their baseline, so their seen rows come from ListSeenAsync then.
    public sealed class PostingLoad
    {
        public IReadOnlyCollection<ulong> ArmorIds { get; init; } = [];
        public IReadOnlyCollection<ulong> LobbyIds { get; init; } = [];
    }

    // Everything a run decides from, read together: each source's baseline, every server's switches, every delivery status,
    // the items that wait (pending, oldest first), the messages not settled yet (sending or in doubt), the seen rows of this
    // run's armor and lobbies, and the rows of this run's approved armor that were dropped as not approved
    public sealed class PostingState
    {
        public IReadOnlyList<BaselineRow> Baselines { get; init; } = [];
        public IReadOnlyList<GuildSwitches> Switches { get; init; } = [];
        public IReadOnlyList<DeliveryStatusRow> Statuses { get; init; } = [];
        public IReadOnlyList<QueueRow> Open { get; init; } = [];
        public IReadOnlyList<DeliveryRow> Unsettled { get; init; } = [];
        public IReadOnlyList<SeenRow> Seen { get; init; } = [];
        public IReadOnlyList<QueueRow> Withdrawn { get; init; } = [];
    }

    // An item queued for one server, which starts pending. One that's already in the queue, for the same server, item and
    // revision, stays as it is, so a detection that's saved again lands once.
    public sealed record QueueItem
    {
        public ulong guild_id { get; init; }
        public string source { get; init; } = "";
        public ulong item_id { get; init; }

        // 0 for armor and lobbies, and for a map the update it's for, as Steam's Unix time
        public ulong revision { get; init; }
        public bool is_new { get; init; }
        public DateTime queued_at { get; init; }
    }

    // What a look found, saved in one transaction: baselines and seen rows written whole, so a save that's repeated writes the
    // same, items queued for the servers owed them, armor approved again, whose rows that were dropped as not approved go
    // back to pending, and maps shown again, whose rows given by their IDs that were dropped as hidden do, each queued at
    // the time given. The notification run saves what it found here, and the new-submission alerts their floor, their tries
    // and the submissions they told the owner about.
    public sealed class Detection
    {
        public DateTime At { get; init; }
        public IReadOnlyList<BaselineRow> Baselines { get; init; } = [];
        public IReadOnlyList<SeenRow> Seen { get; init; } = [];
        public IReadOnlyList<QueueItem> Queue { get; init; } = [];
        public IReadOnlyList<ulong> ReopenArmor { get; init; } = [];
        public IReadOnlyList<ulong> ReopenMaps { get; init; } = [];

        public bool IsEmpty => Baselines.Count == 0 && Seen.Count == 0 && Queue.Count == 0 && ReopenArmor.Count == 0 && ReopenMaps.Count == 0;
    }

    // A message about to be sent, which takes its pending rows: written as sending, with the rows it carries, before the
    // send. AttemptKey is the bot's own random number for the attempt.
    public sealed class DeliveryClaim
    {
        public ulong AttemptKey { get; init; }
        public ulong RunId { get; init; }
        public DateTime RunStartedAt { get; init; }
        public ulong GuildId { get; init; }
        public ulong ChannelId { get; init; }
        public string Source { get; init; } = "";

        // From 1, for the messages of one post
        public byte Part { get; init; }
        public DateTime AttemptedAt { get; init; }
        public IReadOnlyList<ulong> QueueIds { get; init; } = [];

        // A map post's updates newer than its rows are for, which it shows as Steam has the map when it's built: each is
        // queued for the server with the message, sending, so it settles with it, and once the message went, the update's
        // own queueing after its cooldown lands on that row and doesn't post it there again. One in the queue already stays
        // as it is. A message that wasn't made gives it back with its rows, and when they're dropped because the server isn't
        // owed maps then, the update's own queueing, once the server is owed maps again, puts it back.
        public IReadOnlyList<ShownUpdate> Shows { get; init; } = [];
    }

    // A map's update that a message shows, as Steam's Unix time, as a map's queue row has it
    public sealed record ShownUpdate(ulong MapId, ulong Revision);

    // How a claimed message ended, or what the look in the channel's history found:
    // - sent, with Discord's message ID: its rows are sent, and its server's kind posts again
    // - failed, with a reason: its rows go back to pending. Counts says it's that server's own failure, which adds to the
    //   status's tries in a row and sets the wait before the next try; a rate limit, a refused token or a message that
    //   wasn't made doesn't count.
    // - in doubt, while Discord didn't answer: its rows stay sending
    // - unconfirmed, when the bot can't find out: its rows are sent, so it's never sent again, with unconfirmed as their
    //   reason, which outlives the message's own row
    public sealed class Settlement
    {
        public ulong AttemptKey { get; init; }

        // One of DeliveryState's words but sending
        public string Outcome { get; init; } = "";
        public DateTime At { get; init; }
        public ulong? MessageId { get; init; }
        public string? Reason { get; init; }
        public byte? Missing { get; init; }
        public ushort? HttpStatus { get; init; }
        public uint? DiscordCode { get; init; }
        public bool Counts { get; init; }
        public DateTime? NextTryAt { get; init; }
        public ulong? WaitingOn { get; init; }
    }

    // A message the bot's cache refused before any request: the channel isn't one it can post in, or a permission is missing
    // there. Its items stay pending, and it counts against the server's kind as a failure, with the wait before the next try.
    public sealed class Refusal
    {
        public ulong AttemptKey { get; init; }
        public ulong RunId { get; init; }
        public DateTime RunStartedAt { get; init; }
        public ulong GuildId { get; init; }
        public ulong ChannelId { get; init; }
        public string Source { get; init; } = "";
        public ushort ItemCount { get; init; }
        public DateTime At { get; init; }
        public string Reason { get; init; } = "";
        public byte? Missing { get; init; }
        public DateTime? NextTryAt { get; init; }
        public ulong? WaitingOn { get; init; }
    }
}
