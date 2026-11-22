namespace DataAccessLibrary.Dashboard.Models
{
    // A session as the Sessions page lists and ends it: every column of dashboard_sessions but the ticket, which no page
    // reads. The names match the columns. id_hash is the SHA-256 of the cookie's key, so it can't sign anyone in, and it's
    // how the page names a session to end. Every time comes back marked UTC.
    public sealed class SessionListRow
    {
        public byte[] id_hash { get; set; } = [];

        // A Discord user ID, with the name as it was when they signed in
        public ulong user_id { get; set; }
        public string user_name { get; set; } = "";

        public DateTime created_at { get; set => field = UtcTimes.Mark(value); }
        public DateTime last_seen_at { get; set => field = UtcTimes.Mark(value); }
        public DateTime expires_at { get; set => field = UtcTimes.Mark(value); }

        // As the visitor's browser sent them, which only the Owner sees
        public string? ip { get; set; }
        public string? user_agent { get; set; }
    }
}
