namespace DataAccessLibrary.Status.Models
{
    // One minute the bot ran, with whether Discord and Steam were up then. The names match the columns, as in Guild.
    public class UptimeSample
    {
        // The minute's start, UTC, with no seconds
        public DateTime minute { get; set; }

        public bool discord_up { get; set; }
        public bool steam_up { get; set; }
    }
}
