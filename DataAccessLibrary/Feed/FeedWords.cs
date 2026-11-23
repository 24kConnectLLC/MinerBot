namespace DataAccessLibrary.Feed
{
    // The words of the website feed's tables, which the website's pages read

    // The two feeds, each with its row in mmh_feed_state
    public static class FeedName
    {
        public const string Lobbies = "lobbies";
        public const string Maps = "maps";

        public static IReadOnlyList<string> All { get; } = [Lobbies, Maps];

        public static bool IsKnown(string? feed) => feed is Lobbies or Maps;
    }

    // Who hosts a lobby: a player, or the game's own Gamenight or Official host
    public static class FeedHostKind
    {
        public const string Player = "player";
        public const string Gamenight = "gamenight";
        public const string Official = "official";

        public static IReadOnlyList<string> All { get; } = [Player, Gamenight, Official];

        public static bool IsKnown(string? kind) => kind is Player or Gamenight or Official;
    }
}
