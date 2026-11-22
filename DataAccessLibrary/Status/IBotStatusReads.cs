using DataAccessLibrary.Status.Models;

namespace DataAccessLibrary.Status
{
    // The dashboard's reads of the bot's status and history. Reads only, so the dashboard is never handed a way to write
    // the bot's tables.
    public interface IBotStatusReads
    {
        Task<IReadOnlyList<EventCountRow>> CountEvents(IReadOnlyCollection<string> kinds, DateTime from, DateTime recentFrom);
        Task<UptimeTotalRow> CountSamples(DateTime from, DateTime to);
        Task<IReadOnlyList<UptimeDayRow>> CountSampleDays(DateTime from, DateTime to);
        Task<BotEventRow?> FirstEventAfter(string kind, BotEventRow? after);
        Task<UptimeStartRow> FirstRecord();
        Task<BotStatusRow?> GetStatus();
        Task<BotEventRow?> LatestEvent(string kind);
        Task<BotEventRow?> LatestEventWith(string kind, IReadOnlyCollection<string> phrases, DateTime since);
        Task<Page<BotEventRow>> ListEvents(EventQuery query);
        Task<IReadOnlyList<JobRunRow>> ListJobRuns(IReadOnlyCollection<string> jobs, int perJob);
        Task<IReadOnlyList<JobStatusRow>> ListJobs();
        Task<IReadOnlyList<UptimeDayRow>> ListUptimeDays(DateTime from, DateTime to);
    }
}
