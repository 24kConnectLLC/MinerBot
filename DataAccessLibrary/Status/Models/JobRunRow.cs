namespace DataAccessLibrary.Status.Models
{
    // One run of a job as the dashboard reads it. The names match the columns, as in JobRun, and every time comes back
    // marked UTC.
    public sealed class JobRunRow
    {
        public ulong id { get; set; }
        public string job { get; set; } = "";

        // A run that was cut off has no end until it ends
        public DateTime started_at { get; set => field = UtcTimes.Mark(value); }
        public DateTime? ended_at { get; set => field = UtcTimes.Mark(value); }

        // One of JobResult's words
        public string result { get; set; } = "";
        public uint? duration_ms { get; set; }
        public string? detail { get; set; }
    }
}
