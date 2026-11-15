namespace DataAccessLibrary.Status.Models
{
    // A scheduled job, with when it next runs and how its last run went. The names match the columns, as in Guild.
    public class JobStatus
    {
        // <Class>.<Method>, as the logs name it, and its cron schedule, which the bot reads in US Eastern time
        public string job { get; set; } = "";
        public string schedule { get; set; } = "";

        // UTC
        public DateTime? next_run_at { get; set; }

        // The last run. Without a start, a write keeps the last run the row has, such as the one before a restart.
        public DateTime? last_started_at { get; set; }
        public DateTime? last_ended_at { get; set; }
        public string? last_result { get; set; }
        public uint? last_duration_ms { get; set; }
        public string? last_detail { get; set; }

        // UTC, when the bot last changed it
        public DateTime updated_at { get; set; }
    }
}
