namespace DataAccessLibrary.Status.Models
{
    // How many events of one kind there were since a time, and how many of those since a later one, such as the bot's
    // starts in the last 30 days and in the last 7
    public sealed class EventCountRow
    {
        public string kind { get; set; } = "";
        public long total { get; set; }
        public long recent { get; set; }
    }
}
