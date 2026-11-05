using DataAccessLibrary.Audit.Models;

namespace DataAccessLibrary.Audit
{
    public interface IAuditLogData
    {
        Task AddAudit(AuditEntry entry, DataTransaction? transaction = null);
        Task<Page<AuditEntry>> ListAudit(AuditQuery query);
    }
}
