using DataAccessLibrary.Status.Models;

namespace DataAccessLibrary.Status
{
    public interface IBotStatusData
    {
        Task AddEvents(IReadOnlyList<BotEvent> events);
        Task<int> DeleteOtherJobs(IReadOnlyCollection<string> jobs);
        Task<BotStatus?> GetStatus();
        Task SaveJobRuns(IReadOnlyList<JobRun> runs);
        Task SaveJobStatus(IReadOnlyList<JobStatus> jobs);
        Task SaveSamples(IReadOnlyList<UptimeSample> samples);
        Task SaveStatus(BotStatus status);
    }
}
