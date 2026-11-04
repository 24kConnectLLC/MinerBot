namespace DataAccessLibrary.Audit.Models
{
    // A filter left null matches every row. From is included and To isn't, both in UTC.
    public class AuditQuery
    {
        public ulong? ActorId { get; set; }
        public string? Action { get; set; }
        public ulong? GuildId { get; set; }
        public string? TargetType { get; set; }
        public string? TargetId { get; set; }
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }

        // From 0
        public int Page { get; set; }
        public int PageSize { get; set; } = 25;
    }
}
