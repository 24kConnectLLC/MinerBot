namespace DataAccessLibrary.Status
{
    public interface IRetentionData
    {
        Task<int> ClearAuditAddresses(DateTime before, int take);
        Task<int> DeleteAuditRows(DateTime before, int take);
        Task<int> DeleteEvents(DateTime before, int take);
        Task<int> DeleteGuildEvents(DateTime before, int take);
        Task<int> DeleteJobRuns(DateTime before, int take);
        Task<int> DeleteSamples(DateTime before, int take);
        Task<int> DeleteSessions(DateTime before, int take);
        Task<int> TotalUptimeDays(DateTime from, DateTime to);
    }
}
