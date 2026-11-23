using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using MinerBot.Dashboard.Status;

namespace MinerBot.Dashboard.Tests.Support
{
    // The bot's status as a restart's box and the request command read it, without a database: the status row given, and
    // the bot's record of a run's first connection to Discord, given or failing as a database that stopped answering would.
    // Every read of the connection is noted with where it reads from. Any other read isn't one these make, and fails.
    internal sealed class ConnectionReads(BotStatusRow? status, BotEventRow? connected = null, bool fails = false) : IBotStatusReads
    {
        public List<BotEventRow?> Asked { get; } = [];

        public Task<BotStatusRow?> GetStatus() => Task.FromResult(status);

        public Task<BotEventRow?> FirstEventAfter(string kind, BotEventRow? after)
        {
            Assert.Equal(EventWords.DiscordConnected, kind);
            Asked.Add(after);

            return fails ? Task.FromException<BotEventRow?>(new TimeoutException("The database didn't answer within 15 seconds")) : Task.FromResult(connected);
        }

        static NotSupportedException NotTheirs() => new("Not a read the box or the request command makes");

        public Task<IReadOnlyList<EventCountRow>> CountEvents(IReadOnlyCollection<string> kinds, DateTime from, DateTime recentFrom) => throw NotTheirs();
        public Task<UptimeTotalRow> CountSamples(DateTime from, DateTime to) => throw NotTheirs();
        public Task<IReadOnlyList<UptimeDayRow>> CountSampleDays(DateTime from, DateTime to) => throw NotTheirs();
        public Task<UptimeStartRow> FirstRecord() => throw NotTheirs();
        public Task<BotEventRow?> LatestEvent(string kind) => throw NotTheirs();
        public Task<BotEventRow?> LatestEventWith(string kind, IReadOnlyCollection<string> phrases, DateTime since) => throw NotTheirs();
        public Task<DataAccessLibrary.Page<BotEventRow>> ListEvents(EventQuery query) => throw NotTheirs();
        public Task<IReadOnlyList<JobRunRow>> ListJobRuns(IReadOnlyCollection<string> jobs, int perJob) => throw NotTheirs();
        public Task<IReadOnlyList<JobStatusRow>> ListJobs() => throw NotTheirs();
        public Task<IReadOnlyList<UptimeDayRow>> ListUptimeDays(DateTime from, DateTime to) => throw NotTheirs();
    }
}
