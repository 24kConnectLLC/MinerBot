namespace DataAccessLibrary.Requests.Models
{
    // The bot's settings that only signed requests change, as bot_settings' one row holds them. There's no row until the
    // first change, and no row means posts aren't paused and the status is the default. The names match the columns, and
    // every time comes back marked UTC.
    public sealed class BotSettingsRow
    {
        // Whether the notification job's armor, map and lobby posts are paused, why, and who paused or resumed them, when
        // and on which request
        public bool posts_paused { get; set; }
        public string? pause_reason { get; set; }
        public DateTime? pause_changed_at { get; set => field = UtcTimes.Mark(value); }
        public ulong? pause_changed_by { get; set; }
        public ulong? pause_request_id { get; set; }

        // The bot's status text, null for the default, and who set it, when and on which request
        public string? status_text { get; set; }
        public DateTime? status_changed_at { get; set => field = UtcTimes.Mark(value); }
        public ulong? status_changed_by { get; set; }
        public ulong? status_request_id { get; set; }

        public DateTime updated_at { get; set => field = UtcTimes.Mark(value); }
    }
}
