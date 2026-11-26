using DataAccessLibrary.Audit.Models;

namespace DataAccessLibrary.Audit
{
    public interface IAuditLogData
    {
        Task AddAudit(AuditEntry entry, DataTransaction? transaction = null);
        Task<IReadOnlyList<AuditEntry>> LatestChanges(string targetType, IReadOnlyCollection<string> targetIds);
        Task<Page<AuditEntry>> ListAudit(AuditQuery query);
        Task<IReadOnlyList<DateTime>> ListActionTimes(ulong actorId, string action, IReadOnlyCollection<string> outcomes, DateTime from, int take);
    }
}
