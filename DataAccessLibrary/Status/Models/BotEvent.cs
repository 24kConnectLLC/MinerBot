namespace DataAccessLibrary.Status.Models
{
    // Something that happened to the bot, such as discord.disconnected. The names match the columns, as in Guild.
    public class BotEvent
    {
        // The run and the event's place in it, from 1, so an event that's written again lands once
        public ulong run_id { get; set; }
        public uint seq { get; set; }

        // UTC
        public DateTime at { get; set; }

        // One of the bot's kinds, and one of EventLevel's words
        public string kind { get; set; } = "";
        public string level { get; set; } = "";

        // In the bot's own wording, never a secret or a library's message
        public string? detail { get; set; }
    }
}
