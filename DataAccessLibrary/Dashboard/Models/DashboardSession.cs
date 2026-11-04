namespace DataAccessLibrary.Dashboard.Models
{
    // A signed-in dashboard session, in MinerBot's own database. The names match the columns, as in Guild.
    public class DashboardSession
    {
        // A Discord user ID, with the name as it was when they signed in
        public ulong user_id { get; set; }
        public string user_name { get; set; } = "";

        // UTC. The session ends at expires_at, whatever its activity.
        public DateTime created_at { get; set; }
        public DateTime last_seen_at { get; set; }
        public DateTime expires_at { get; set; }

        public string? ip { get; set; }
        public string? user_agent { get; set; }

        // Encrypted by the dashboard before it arrives
        public byte[] ticket { get; set; } = [];
    }
}
