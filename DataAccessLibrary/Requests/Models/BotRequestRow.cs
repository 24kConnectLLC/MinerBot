using MinerBot.Shared.Requests;

namespace DataAccessLibrary.Requests.Models
{
    // A request to the bot as bot_commands holds it, for the bot and the dashboard alike. The names match the columns, and
    // every time comes back marked UTC.
    public sealed class BotRequestRow
    {
        public ulong id { get; set; }

        // What the dashboard wrote and signed
        public string request_key { get; set; } = "";
        public string kind { get; set; } = "";
        public ulong actor_id { get; set; }
        public string actor_name { get; set; } = "";
        public string actor_role { get; set; } = "";
        public ulong? guild_id { get; set; }
        public ulong? channel_id { get; set; }
        public string? argument { get; set; }
        public DateTime created_at { get; set => field = UtcTimes.Mark(value); }
        public DateTime expires_at { get; set => field = UtcTimes.Mark(value); }
        public byte[] signature { get; set; } = [];

        // What the bot wrote: one of RequestStates' words, null while the request waits, and the run that decided or took it
        public string? state { get; set; }
        public ulong? run_id { get; set; }
        public DateTime? claimed_at { get; set => field = UtcTimes.Mark(value); }
        public DateTime? ended_at { get; set => field = UtcTimes.Mark(value); }
        public string? result { get; set; }
        public ulong? result_number { get; set; }

        // One of RequestDmStates' words, for the kinds after which the bot tells the owner
        public string? dm_state { get; set; }

        // What the signature covers, as the row holds it, which is what the bot verifies
        public RequestFields Fields() => new(request_key, kind, actor_id, actor_name, actor_role, guild_id, channel_id, argument, created_at, expires_at);
    }
}
