namespace DataAccessLibrary.Requests.Models
{
    // How many requests of some kinds the bot took since a time, by any run, and when it took the oldest of them, null when
    // it took none. A kind's limit is reached at its count, and lets the next one through once the oldest is a window old.
    public sealed class ClaimCountRow
    {
        public long count { get; set; }
        public DateTime? oldest { get; set => field = UtcTimes.Mark(value); }
    }
}
