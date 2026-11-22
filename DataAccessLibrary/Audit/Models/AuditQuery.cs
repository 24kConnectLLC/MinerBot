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

        // Actions left out of the rows and the total, such as the sign-in alerts on the Overview's latest changes, which
        // aren't changes. Null or empty leaves none out.
        public IReadOnlyCollection<string>? ExcludeActions { get; set; }

        // From 0
        public int Page { get; set; }
        public int PageSize { get; set; } = 25;

        // Each row's visitor address, which only the Owner may see. Left off, ip comes back null, so a page can't show it
        // by mistake.
        public bool IncludeIp { get; set; }
    }
}
