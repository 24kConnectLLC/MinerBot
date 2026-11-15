namespace DataAccessLibrary.Status.Models
{
    // One run of a scheduled job. The names match the columns, as in Guild.
    public class JobRun
    {
        // The bot's run and the job run's place in it, from 1, so a run that's written again, or that ends after it was cut
        // off, stays one row
        public ulong run_id { get; set; }
        public uint seq { get; set; }

        // <Class>.<Method>, as the logs name it
        public string job { get; set; } = "";

        // UTC. A run that was cut off has no end until it ends.
        public DateTime started_at { get; set; }
        public DateTime? ended_at { get; set; }

        // One of JobResult's words
        public string result { get; set; } = "";
        public uint? duration_ms { get; set; }
        public string? detail { get; set; }
    }
}
