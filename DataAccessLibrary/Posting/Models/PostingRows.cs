namespace DataAccessLibrary.Posting.Models
{
    // The posting tables' rows, as the bot and the dashboard read them. The names match the columns, as in Guild, and every
    // time comes back marked UTC. They're records, so two reads of the same row are equal.

    // A server's switches for the kinds the bot posts. No row means every kind is on.
    public sealed record GuildSwitches
    {
        public ulong guild_id { get; set; }
        public bool armor_on { get; set; }
        public bool maps_on { get; set; }
        public bool lobbies_on { get; set; }

        // 1 at the first save, and one more at each
        public uint version { get; set; }

        // Who saved them last: a Discord user ID, and owner or server_admin
        public DateTime changed_at { get; set => field = UtcTimes.Mark(value); }
        public ulong changed_by { get; set; }
        public string changed_by_role { get; set; } = "";

        // Whether a kind the bot posts is switched on
        public bool IsOn(string source) => source switch
        {
            PostSource.Armor => armor_on,
            PostSource.Maps => maps_on,
            PostSource.Lobbies => lobbies_on,
            _ => throw new ArgumentException($"Only the kinds the bot posts have a switch: {string.Join(", ", PostSource.Posted)}", nameof(source))
        };
    }

    // A source's baseline and its last good read. high_id and alerted_at are the submissions' only, baseline_time the maps'.
    public sealed record BaselineRow
    {
        public string source { get; set; } = "";
        public DateTime taken_at { get; set => field = UtcTimes.Mark(value); }
        public ulong taken_by_run { get; set; }

        // One of BaselineReason's words, and how many items the baseline took
        public string reason { get; set; } = "";
        public uint item_count { get; set; }

        // By Steam's clock: the oldest update of the maps baseline's read
        public DateTime? baseline_time { get; set => field = UtcTimes.Mark(value); }

        // The highest armor ID of any status at the submissions' first look, and when the owner was last told, or tried
        public ulong? high_id { get; set; }
        public DateTime? alerted_at { get; set => field = UtcTimes.Mark(value); }

        // The start of the last run that read the source and saved what it found
        public DateTime checked_at { get; set => field = UtcTimes.Mark(value); }
        public ulong checked_by_run { get; set; }
    }

    // An item the bot has found
    public sealed record SeenRow
    {
        public string source { get; set; } = "";
        public ulong item_id { get; set; }

        // One of SeenState's words
        public string state { get; set; } = "";
        public DateTime first_seen_at { get; set => field = UtcTimes.Mark(value); }
        public DateTime last_seen_at { get; set => field = UtcTimes.Mark(value); }

        // A map's: Steam's time of the update last recorded, and the bot's of its last queueing, for the cooldown
        public DateTime? updated_at { get; set => field = UtcTimes.Mark(value); }
        public DateTime? queued_at { get; set => field = UtcTimes.Mark(value); }
    }

    // One item one server is owed, and what became of it
    public sealed record QueueRow
    {
        public ulong id { get; set; }
        public ulong guild_id { get; set; }
        public string source { get; set; } = "";
        public ulong item_id { get; set; }

        // 0 for armor and lobbies, and for a map the update it's for, as Steam's Unix time
        public ulong revision { get; set; }
        public bool is_new { get; set; }
        public DateTime queued_at { get; set => field = UtcTimes.Mark(value); }

        // One of QueueState's words. The message that holds or carried it, while it's sending and once it's sent, and why it
        // was dropped, or unconfirmed when it's sent in a message that may not have arrived.
        public string state { get; set; } = "";
        public ulong? delivery_id { get; set; }
        public string? reason { get; set; }
        public DateTime? settled_at { get; set => field = UtcTimes.Mark(value); }
    }

    // One message the bot sent or tried, or a refusal its cache saw first
    public sealed record DeliveryRow
    {
        public ulong id { get; set; }

        // The bot's own random number for the attempt, by which its writes land once
        public ulong attempt_key { get; set; }

        // The run of the bot, as bot_status.run_id, and the start of the notification run that made it
        public ulong run_id { get; set; }
        public DateTime run_started_at { get; set => field = UtcTimes.Mark(value); }
        public ulong guild_id { get; set; }
        public ulong channel_id { get; set; }
        public string source { get; set; } = "";

        // The message's place in its post, from 1, and 0 for a refusal, which was no message
        public byte part { get; set; }
        public ushort item_count { get; set; }
        public DateTime attempted_at { get; set => field = UtcTimes.Mark(value); }

        // One of DeliveryState's words, Discord's message ID once it's sent, and when it was settled
        public string state { get; set; } = "";
        public ulong? message_id { get; set; }
        public DateTime? settled_at { get; set => field = UtcTimes.Mark(value); }

        // One of DeliveryReasons' words, the mask of missing permissions, and Discord's numbers, never its text
        public string? reason { get; set; }
        public byte? missing { get; set; }
        public ushort? http_status { get; set; }
        public uint? discord_code { get; set; }

        // How many times the bot looked for it in the channel's history while Discord's answer was unknown
        public byte checks { get; set; }
    }

    // One server's delivery of one kind, as its last message or refusal left it
    public sealed record DeliveryStatusRow
    {
        public ulong guild_id { get; set; }
        public string source { get; set; } = "";

        // The last message that went
        public DateTime? last_sent_at { get; set => field = UtcTimes.Mark(value); }
        public ulong? last_sent_channel_id { get; set; }
        public ulong? last_message_id { get; set; }

        // The last that didn't, and why
        public DateTime? last_failed_at { get; set => field = UtcTimes.Mark(value); }
        public ulong? last_failed_channel_id { get; set; }
        public string? last_reason { get; set; }
        public byte? last_missing { get; set; }
        public ushort? last_http_status { get; set; }
        public uint? last_discord_code { get; set; }

        // Since when posts keep failing and how many tries in a row failed, null and 0 while posts go, and the wait before the
        // next try, with a hash of what it started from
        public DateTime? failing_since { get; set => field = UtcTimes.Mark(value); }
        public uint failures { get; set; }
        public DateTime? next_try_at { get; set => field = UtcTimes.Mark(value); }
        public ulong? waiting_on { get; set; }
        public DateTime updated_at { get; set => field = UtcTimes.Mark(value); }
    }
}
