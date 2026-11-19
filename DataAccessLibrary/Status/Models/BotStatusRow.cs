namespace DataAccessLibrary.Status.Models
{
    // The bot's status row as the dashboard reads it. The names match the columns, as in BotStatus, which is the bot's own
    // model for writing it, and every time comes back marked UTC.
    public sealed class BotStatusRow
    {
        public ulong run_id { get; set; }

        // The commit the image was built from, or null when it has none
        public string? version { get; set; }

        // heartbeat_at is when the row was last written, and stopped_at is set by a clean stop only
        public DateTime started_at { get; set => field = UtcTimes.Mark(value); }
        public DateTime heartbeat_at { get; set => field = UtcTimes.Mark(value); }
        public DateTime? stopped_at { get; set => field = UtcTimes.Mark(value); }

        // One of DiscordState's words, and since when. The latency and the count of servers only while it's connected.
        public string discord_state { get; set; } = "";
        public DateTime? discord_since { get; set => field = UtcTimes.Mark(value); }
        public int? discord_latency_ms { get; set; }
        public int? discord_guilds { get; set; }

        // One of SteamState's words, and since when
        public string steam_state { get; set; } = "";
        public DateTime? steam_since { get; set => field = UtcTimes.Mark(value); }

        // Steam's last answer to a logon, an EResult name, while the bot isn't logged on, and the UTC day the token ends
        public string? steam_last_result { get; set; }
        public DateTime? steam_token_expires { get; set => field = UtcTimes.Mark(value); }
    }
}
