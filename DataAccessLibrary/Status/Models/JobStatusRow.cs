namespace DataAccessLibrary.Status.Models
{
    // A scheduled job as the dashboard reads it. The names match the columns, as in JobStatus, and every time comes back
    // marked UTC.
    public sealed class JobStatusRow
    {
        // <Class>.<Method>, as the logs name it, and its cron schedule, which the bot reads in US Eastern time
        public string job { get; set; } = "";
        public string schedule { get; set; } = "";

        public DateTime? next_run_at { get; set => field = UtcTimes.Mark(value); }

        // The last run, which a restart keeps
        public DateTime? last_started_at { get; set => field = UtcTimes.Mark(value); }
        public DateTime? last_ended_at { get; set => field = UtcTimes.Mark(value); }
        public string? last_result { get; set; }
        public uint? last_duration_ms { get; set; }
        public string? last_detail { get; set; }

        // When the bot last changed it
        public DateTime updated_at { get; set => field = UtcTimes.Mark(value); }
    }
}
