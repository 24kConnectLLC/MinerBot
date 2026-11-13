namespace DataAccessLibrary.Blacklist.Models
{
    // A row of the website's blacklist whose steam_id no account number can be, such as a SteamID64 cut to ten digits, a
    // negative number or one past 4294967295, which something other than the dashboard typed in. Its number is read as
    // the column stores it, since BlacklistModel's uint can't hold it. The other names match the columns too.
    public class OutOfRangeBan
    {
        public decimal steam_id { get; set; }
        public string? username { get; set; }
        public string? reason { get; set; }

        // As in BlacklistModel: null for no end, and for a stored value that isn't a real date, which the flag says
        public DateTime? expire_date { get; set; }
        public bool expire_date_unreadable { get; set; }

        public string? notes { get; set; }
    }
}
