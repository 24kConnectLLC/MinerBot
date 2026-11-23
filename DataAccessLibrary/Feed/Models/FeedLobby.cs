namespace DataAccessLibrary.Feed.Models
{
    // An open public lobby, for its row in the website's mmh_feed_lobbies. The names match the columns, as in Guild, and
    // the row's first_seen and last_seen come from the time it's written at. A record, so tests can compare rows.
    public sealed record FeedLobby
    {
        public ulong lobby_id { get; init; }

        // The host's SteamID64
        public ulong owner_id { get; init; }

        // As the bot's lobby list shows it, Gamenight or Official for a lobby of that kind, and empty when the host has none
        public string host_name { get; init; } = "";

        // One of FeedHostKind's words
        public string host_kind { get; init; } = FeedHostKind.Player;

        public string map_name { get; init; } = "";

        // Empty when they aren't known, never a guess
        public string modes { get; init; } = "";

        public uint version_number { get; init; }

        // The branch's name, such as Update 41, when the version is a branch's own, otherwise empty
        public string version_label { get; init; } = "";

        // Whether it's on the version the bot counts as the game's current one
        public bool is_current { get; init; }

        public ushort players { get; init; }
        public ushort max_players { get; init; }
    }
}
