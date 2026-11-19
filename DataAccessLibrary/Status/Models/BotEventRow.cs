namespace DataAccessLibrary.Status.Models
{
    // One of the bot's events as the dashboard reads it. The names match the columns, as in BotEvent, and the time comes
    // back marked UTC.
    public sealed class BotEventRow
    {
        // The order the events were written in, which breaks a tie between two at the same time
        public ulong id { get; set; }

        public DateTime at { get; set => field = UtcTimes.Mark(value); }

        // One of the bot's kinds, and one of EventLevel's words
        public string kind { get; set; } = "";
        public string level { get; set; } = "";

        // In the bot's own wording, never a secret or a library's message
        public string? detail { get; set; }
    }
}
