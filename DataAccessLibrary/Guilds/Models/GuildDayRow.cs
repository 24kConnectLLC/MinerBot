namespace DataAccessLibrary.Guilds.Models
{
    // A server's member count on one UTC day, from the bot's daily count. The names match the columns of guild_daily.
    public sealed class GuildDayRow
    {
        // The day's start, 00:00 UTC
        public DateTime day { get; set => field = UtcTimes.Mark(value); }
        public ulong guild_id { get; set; }
        public uint? member_count { get; set; }
    }
}
