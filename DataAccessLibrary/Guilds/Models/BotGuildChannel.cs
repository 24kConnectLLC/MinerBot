namespace DataAccessLibrary.Guilds.Models
{
    // One of a server's text or announcement channels, with the bot's permissions there, for its row in guild_channels.
    // The names match the columns, as in Guild, and the server's comes from the server it's written with. A record, so the
    // bot can tell a channel that changed from one that didn't.
    public sealed record BotGuildChannel
    {
        public ulong channel_id { get; init; }
        public string name { get; init; } = "";

        // One of GuildChannelType's words
        public string type { get; init; } = "";

        // Its category's name, ID and position, null outside one, so a page can list the channels in Discord's order
        public string? category { get; init; }
        public ulong? category_id { get; init; }
        public int? category_position { get; init; }

        public int position { get; init; }

        // Whether the bot has View Channel, Send Messages and Embed Links there
        public bool can_view { get; init; }
        public bool can_send { get; init; }
        public bool can_embed { get; init; }
    }
}
