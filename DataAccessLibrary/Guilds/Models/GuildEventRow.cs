namespace DataAccessLibrary.Guilds.Models
{
    // The bot joining or leaving a server, as the dashboard reads it. The names match the columns of guild_events, and the
    // time comes back marked UTC.
    public sealed class GuildEventRow
    {
        public ulong id { get; set; }
        public ulong guild_id { get; set; }
        public DateTime at { get; set => field = UtcTimes.Mark(value); }

        // One of GuildEventKind's words, with a detail in the bot's own wording
        public string kind { get; set; } = "";
        public string? detail { get; set; }
    }
}
