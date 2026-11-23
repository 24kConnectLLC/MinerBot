namespace DataAccessLibrary.Feed.Models
{
    // A public map of the Steam Workshop, for its row in the website's mmh_feed_maps. The names match the columns, as in
    // Guild, and the row's first_seen and last_seen come from the time it's written at. A record, so tests can compare rows.
    public sealed record FeedMap
    {
        public ulong file_id { get; init; }
        public string title { get; init; } = "";

        // Plain text, null when the map has none
        public string? description { get; init; }

        // Empty when the map has no picture
        public string preview_url { get; init; } = "";

        // The author's SteamID64, 0 when Steam didn't say
        public ulong creator_id { get; init; }

        // Separated by commas, without spaces around them
        public string tags { get; init; } = "";

        public ulong file_size { get; init; }
        public uint subscriptions { get; init; }
        public uint lifetime_subscriptions { get; init; }
        public uint favorited { get; init; }
        public uint lifetime_favorited { get; init; }
        public uint views { get; init; }

        // Steam's own times, in UTC
        public DateTime time_created { get; init; }
        public DateTime time_updated { get; init; }
    }
}
