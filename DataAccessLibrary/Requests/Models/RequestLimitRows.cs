namespace DataAccessLibrary.Requests.Models
{
    // A request the bot took, by any run and however it ended, for the dashboard's check of the bot's limit before it
    // writes another: its ID, which tells it from a waiting row the bot took after that list was read, its kind, its
    // server, and when the bot took it. The time is null for one MySql.Data can't read.
    public sealed class ClaimRow
    {
        public ulong id { get; set; }
        public string kind { get; set; } = "";
        public ulong? guild_id { get; set; }
        public DateTime? claimed_at { get; set => field = UtcTimes.Mark(value); }
    }

    // A request that waits for the bot and hasn't expired. The bot doesn't count it toward its limit yet, but will once it
    // takes it. A time is null for one MySql.Data can't read.
    public sealed class WaitingRow
    {
        public ulong id { get; set; }
        public string kind { get; set; } = "";
        public ulong? guild_id { get; set; }
        public DateTime? created_at { get; set => field = UtcTimes.Mark(value); }
        public DateTime? expires_at { get; set => field = UtcTimes.Mark(value); }
    }

    // A request one person asked for, in any state, for the dashboard's limit on each person. The time is null for one
    // MySql.Data can't read.
    public sealed class AskedRow
    {
        public string kind { get; set; } = "";
        public DateTime? created_at { get; set => field = UtcTimes.Mark(value); }
    }
}
