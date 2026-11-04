namespace DataAccessLibrary.Audit.Models
{
    // One row of the audit log in MinerBot's own database. The names match the columns, as in Guild.
    public class AuditEntry
    {
        public ulong id { get; set; }

        // UTC
        public DateTime at { get; set; }

        // A Discord user ID, or null for the bot itself, with the name as it was at the time
        public ulong? actor_id { get; set; }
        public string? actor_name { get; set; }
        public string actor_role { get; set; } = "";

        // Such as armor.approve, and one of AuditOutcome's words
        public string action { get; set; } = "";
        public string outcome { get; set; } = "";

        public ulong? guild_id { get; set; }
        public string? target_type { get; set; }
        public string? target_id { get; set; }

        // Only the fields that changed, and never a secret
        public string? before_json { get; set; }
        public string? after_json { get; set; }

        public string? detail { get; set; }
        public string? ip { get; set; }
        public string? request_id { get; set; }
    }
}
