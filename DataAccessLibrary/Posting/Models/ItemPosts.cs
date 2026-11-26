namespace DataAccessLibrary.Posting.Models
{
    // Where one item went, as the armor review page reads it: the bot's seen row, null when it hasn't found the item, and one
    // row for each server it was owed to
    public sealed record ItemPosts(SeenRow? Seen, IReadOnlyList<ItemServerRow> Servers);

    // One server's row of an item in the queue, with the message that holds or carried it, if any, and the server's wait for
    // that kind. The names are the columns', with the message's own under delivery_ and the status's next try.
    public sealed record ItemServerRow
    {
        public ulong queue_id { get; set; }
        public ulong guild_id { get; set; }
        public ulong revision { get; set; }
        public bool is_new { get; set; }
        public DateTime queued_at { get; set => field = UtcTimes.Mark(value); }

        // One of QueueState's words, why it was dropped, or unconfirmed for a message that may not have arrived, and when it
        // was settled
        public string state { get; set; } = "";
        public string? reason { get; set; }
        public DateTime? settled_at { get; set => field = UtcTimes.Mark(value); }

        // The message, null while none holds the row
        public string? delivery_state { get; set; }
        public ulong? channel_id { get; set; }
        public ulong? message_id { get; set; }
        public DateTime? attempted_at { get; set => field = UtcTimes.Mark(value); }

        // When the server's kind is tried again, while it waits after a failure
        public DateTime? next_try_at { get; set => field = UtcTimes.Mark(value); }
    }
}
