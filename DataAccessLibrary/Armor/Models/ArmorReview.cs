namespace DataAccessLibrary.Armor.Models
{
    // A change of an armor's status, in MinerBot's own database. The names match the columns, as in Guild.
    public class ArmorReview
    {
        public ulong id { get; set; }
        public long armor_id { get; set; }

        // Null when the armor had no status
        public string? from_status { get; set; }
        public string to_status { get; set; } = "";

        // Optional, and never shown to the submitter
        public string? reason { get; set; }

        // A Discord user ID, with the name as it was at the time
        public ulong reviewed_by { get; set; }
        public string? reviewer_name { get; set; }

        // UTC
        public DateTime reviewed_at { get; set; }
    }
}
