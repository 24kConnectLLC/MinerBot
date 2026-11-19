namespace DataAccessLibrary.Status.Models
{
    // A UTC day's minutes: how many the bot sampled, and in how many of them Discord and Steam were up. From uptime_daily
    // for a day the clean-up has totalled, or counted from uptime_samples for one it hasn't.
    public sealed class UptimeDayRow
    {
        // The day's start, 00:00 UTC
        public DateTime day { get; set => field = UtcTimes.Mark(value); }

        public int sampled_minutes { get; set; }
        public int discord_minutes { get; set; }
        public int steam_minutes { get; set; }
    }

    // The same counts for any stretch of minutes, such as the last 24 hours
    public sealed class UptimeTotalRow
    {
        public int sampled_minutes { get; set; }
        public int discord_minutes { get; set; }
        public int steam_minutes { get; set; }
    }

    // Where the record of the bot's minutes begins: the first minute sampled that's still kept, and the first day totalled.
    // Either is null while there's none.
    public sealed class UptimeStartRow
    {
        public DateTime? first_minute { get; set => field = UtcTimes.Mark(value); }
        public DateTime? first_day { get; set => field = UtcTimes.Mark(value); }
    }
}
