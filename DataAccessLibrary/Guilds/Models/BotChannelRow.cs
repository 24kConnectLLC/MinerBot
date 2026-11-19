namespace DataAccessLibrary.Guilds.Models
{
    // One of a server's text or announcement channels, with the bot's permissions there, as the dashboard reads it. The
    // names match the columns, as in BotGuildChannel, and the time comes back marked UTC.
    public sealed class BotChannelRow
    {
        public ulong channel_id { get; set; }
        public ulong guild_id { get; set; }

        // Discord's name, or ___hidden___ for a channel the bot can't see
        public string name { get; set; } = "";

        // One of GuildChannelType's words
        public string type { get; set; } = "";

        // Its category's name, ID and position, null outside one
        public string? category { get; set; }
        public ulong? category_id { get; set; }
        public int? category_position { get; set; }
        public int position { get; set; }

        // Whether the bot has View Channel, Send Messages and Embed Links there
        public bool can_view { get; set; }
        public bool can_send { get; set; }
        public bool can_embed { get; set; }

        // When the bot last wrote its server's channels
        public DateTime updated_at { get; set => field = UtcTimes.Mark(value); }
    }
}
