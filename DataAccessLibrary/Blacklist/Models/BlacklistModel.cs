namespace DataAccessLibrary.Blacklist.Models
{
    // A row of the website's blacklist. The names match the columns, as in Guild.
    public class BlacklistModel
    {
        // The account number, the n in [U:1:n], which is what the website's table holds
        public uint steam_id { get; set; }

        // Each one NULL in a row that has none
        public string? username { get; set; }
        public string? reason { get; set; }

        // Whole seconds, with no time zone, read back with no kind. Null for no end, and for a stored value that isn't a
        // real date, which expire_date_unreadable then says.
        public DateTime? expire_date { get; set; }

        // Not a column, and never written: the stored end date is one no DateTime can hold, such as 0000-00-00, which
        // MariaDB's default mode lets anything else that writes the table store
        public bool expire_date_unreadable { get; set; }

        public string? notes { get; set; }

        public override string ToString()
        {
            return $"{steam_id};{username};{reason};{expire_date};{notes}";
        }
    }
}
