namespace DataAccessLibrary.Status.Models
{
    // A page of the bot's events, newest first. A filter left null matches every event.
    public sealed class EventQuery
    {
        // One of EventLevel's words
        public string? Level { get; set; }

        // A kind, in the form the bot writes them, such as discord.connected
        public string? Kind { get; set; }

        // Whether the routine reconnects Discord asks for are in the list (RoutineReconnect). Left out unless asked for.
        public bool IncludeRoutine { get; set; }

        // From 0
        public int Page { get; set; }
        public int PageSize { get; set; } = 50;
    }
}
