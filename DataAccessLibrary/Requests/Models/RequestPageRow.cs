namespace DataAccessLibrary.Requests.Models
{
    // A request to the bot as the dashboard's pages show it: every column of bot_commands but the key and the signature,
    // which no page needs. The names match the columns. A time is null while the bot hasn't written it, and also when the
    // row holds one MySql.Data can't read, such as 0000-00-00, which MariaDB's default mode lets the dashboard's own login
    // write. Every time comes back marked UTC.
    public sealed class RequestPageRow
    {
        public ulong id { get; set; }

        // What the dashboard wrote and signed
        public string kind { get; set; } = "";
        public ulong actor_id { get; set; }
        public string actor_name { get; set; } = "";
        public string actor_role { get; set; } = "";
        public ulong? guild_id { get; set; }
        public ulong? channel_id { get; set; }
        public string? argument { get; set; }
        public DateTime? created_at { get; set => field = UtcTimes.Mark(value); }
        public DateTime? expires_at { get; set => field = UtcTimes.Mark(value); }

        // What the bot wrote: one of RequestStates' words, null while the request waits, and the run that decided or took it
        public string? state { get; set; }
        public ulong? run_id { get; set; }
        public DateTime? claimed_at { get; set => field = UtcTimes.Mark(value); }
        public DateTime? ended_at { get; set => field = UtcTimes.Mark(value); }
        public string? result { get; set; }
        public ulong? result_number { get; set; }

        // One of RequestDmStates' words, for the kinds after which the bot tells the owner
        public string? dm_state { get; set; }
    }
}
