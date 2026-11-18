namespace DataAccessLibrary.Guilds
{
    // The words of the servers' tables

    // The bot joining or leaving a server
    public static class GuildEventKind
    {
        public const string Joined = "joined";
        public const string Left = "left";

        public static IReadOnlyList<string> All { get; } = [Joined, Left];

        public static bool IsKnown(string? kind) => kind is Joined or Left;
    }

    // The two kinds of channel /admin set_channels takes: a text channel, and an announcement channel, which Discord.Net
    // calls a news channel
    public static class GuildChannelType
    {
        public const string Text = "text";
        public const string Announcement = "announcement";

        public static IReadOnlyList<string> All { get; } = [Text, Announcement];

        public static bool IsKnown(string? type) => type is Text or Announcement;
    }
}
