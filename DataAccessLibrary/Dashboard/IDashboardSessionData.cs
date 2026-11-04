using DataAccessLibrary.Dashboard.Models;

namespace DataAccessLibrary.Dashboard
{
    public interface IDashboardSessionData
    {
        Task CreateSession(string key, DashboardSession session, DataTransaction? transaction = null);
        Task<int> DeleteExpiredSessions(DateTime expiredBefore);
        Task<int> DeleteOldestSessions(ulong userId, int keep, DataTransaction? transaction = null);
        Task<int> DeleteOtherSessions(ulong userId, string keepKey, DataTransaction? transaction = null);
        Task<bool> DeleteSession(string key, DataTransaction? transaction = null);
        Task<DashboardSession?> GetSession(string key, DateTime now);
        Task<bool> RenewSession(string key, byte[] ticket);
        Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work);
        Task<bool> TouchSession(string key, DateTime lastSeenAt);
    }
}
