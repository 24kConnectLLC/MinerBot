namespace DataAccessLibrary.Guilds.Models
{
    // A server the bot is in, as Discord.Net's cache has it, for its row in guilds. The names match the columns, as in
    // Guild. A record, so the bot can tell a server that changed since it last wrote it from one that didn't.
    public sealed record BotGuild
    {
        public ulong guild_id { get; init; }
        public string name { get; init; } = "";

        // For Discord's CDN, null when the server has no icon
        public string? icon_hash { get; init; }

        // Right only just after Discord sent the server, as the bot has no members intent, so it's written only for a
        // server new to the table, or back in it. The daily count keeps it current.
        public uint? member_count { get; init; }

        // UTC, as Discord says
        public DateTime? bot_joined_at { get; init; }

        // False while Discord reports the server unavailable, in an outage
        public bool available { get; init; }

        // Administrator from any of its roles. The permissions are Discord's raw value of its roles' permissions, which
        // Discord.Net would turn into every permission for an Administrator.
        public bool bot_administrator { get; init; }
        public ulong bot_permissions { get; init; }
    }
}
