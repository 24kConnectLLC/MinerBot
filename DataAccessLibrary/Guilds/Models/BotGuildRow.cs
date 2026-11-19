namespace DataAccessLibrary.Guilds.Models
{
    // A server the bot is in or has left, as the dashboard reads it. The names match the columns, as in BotGuild, which is
    // the bot's own model for writing it, and every time comes back marked UTC.
    public sealed class BotGuildRow
    {
        public ulong guild_id { get; set; }
        public string name { get; set; } = "";

        // For Discord's CDN, null when the server has no icon
        public string? icon_hash { get; set; }
        public uint? member_count { get; set; }

        // When the bot joined, as Discord says, when it first wrote the server, and when it left, null while it's in it
        public DateTime? bot_joined_at { get; set => field = UtcTimes.Mark(value); }
        public DateTime first_seen_at { get; set => field = UtcTimes.Mark(value); }
        public DateTime? left_at { get; set => field = UtcTimes.Mark(value); }

        // False while Discord reports the server unavailable, in an outage
        public bool available { get; set; }

        // Administrator from any of the bot's roles, and the roles' raw permissions
        public bool bot_administrator { get; set; }
        public ulong bot_permissions { get; set; }

        // When the bot last wrote the row, which isn't when it last checked the server
        public DateTime updated_at { get; set => field = UtcTimes.Mark(value); }
    }
}
