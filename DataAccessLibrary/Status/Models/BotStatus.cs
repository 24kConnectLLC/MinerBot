namespace DataAccessLibrary.Status.Models
{
    // The bot's one status row, in MinerBot's own database. The names match the columns, as in Guild.
    public class BotStatus
    {
        // Random for each start of the process
        public ulong run_id { get; set; }

        // The commit the image was built from, or null when it has none
        public string? version { get; set; }

        // UTC. heartbeat_at is when the row was last written, and stopped_at is set by a clean stop only.
        public DateTime started_at { get; set; }
        public DateTime heartbeat_at { get; set; }
        public DateTime? stopped_at { get; set; }

        // One of DiscordState's words, and since when, UTC. The latency and the count of servers only while it's connected.
        public string discord_state { get; set; } = "";
        public DateTime? discord_since { get; set; }
        public int? discord_latency_ms { get; set; }
        public int? discord_guilds { get; set; }

        // One of SteamState's words, and since when, UTC
        public string steam_state { get; set; } = "";
        public DateTime? steam_since { get; set; }

        // Steam's last answer to a logon, an EResult name, while the bot isn't logged on
        public string? steam_last_result { get; set; }
        public DateOnly? steam_token_expires { get; set; }
    }
}
