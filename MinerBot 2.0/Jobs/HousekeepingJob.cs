using DataAccessLibrary.Status;
using MinerBot_2._0.Attributes;
using MinerBot_2._0.Models;
using MinerBot_2._0.Status;
using System.Diagnostics;
using System.Globalization;

namespace MinerBot_2._0.Jobs
{
    // The daily clean-up of MinerBot's own database, with the owner's rules (decision 13 of the dashboard's plan): the audit
    // log for 2 years, with the visitors' addresses cleared after 90 days, the bot's events, its joins and leaves of servers
    // and its job runs for 90 days, the minute-by-minute uptime for 90 days with each day's totals kept, and dashboard
    // sessions until 7 days after they end. The servers' rows and their daily member counts are kept. Every cutoff comes
    // from the bot's own clock, never the database's. Each rule goes in chunks, until one comes back short, and every rule
    // runs even when one before it failed; the job then fails, naming the rules that did with the exception's type and
    // MariaDB's error number, and the log has a line for each. Without BotConnection, which the bot may run without, it
    // does nothing: the bot said so once as it started.
    public class HousekeepingJob(IRetentionData retention, TimeProvider clock, IConfiguration configuration, ILogger<HousekeepingJob> logger)
    {
        internal const int ChunkSize = RetentionData.ChunkLimit;
        internal const int MaxChunks = 100;

        static readonly TimeSpan KeepEvents = TimeSpan.FromDays(90);
        static readonly TimeSpan KeepAddresses = TimeSpan.FromDays(90);
        static readonly TimeSpan KeepSessions = TimeSpan.FromDays(7);
        const int KeepAuditYears = 2;
        const int KeepSampleDays = 90;

        // After this, no rule starts a chunk but its first, and the days' totals still run, so the job ends well inside
        // JobHandler's limit of 270 seconds even when every statement is slow: MariaDB ends any statement after 14 seconds.
        // The chunk under way, the later rules' first chunks and the totals make up to ten calls after it, which fit.
        internal TimeSpan TimeBudget = TimeSpan.FromSeconds(105);

        [Job("30 4 * * *")] // 04:30 US Eastern every day, after the nightly backup
        public async Task<string> ApplyRetention()
        {
            if (StatusWriter.BotConnectionProblem(configuration) is { } problem)
                return $"skipped, because ConnectionStrings:BotConnection {problem}";

            var now = clock.GetUtcNow().UtcDateTime;
            var today = now.Date;
            var keepSamplesFrom = today.AddDays(-KeepSampleDays);
            var budget = Stopwatch.StartNew();
            var failed = new List<(string Rule, string Problem)>();
            var unfinished = false;
            var ran = new List<string>();

            var auditRows = await Rule("audit rows", take => retention.DeleteAuditRows(now.AddYears(-KeepAuditYears), take));
            var addresses = await Rule("audit addresses", take => retention.ClearAuditAddresses(now - KeepAddresses, take));
            var events = await Rule("events", take => retention.DeleteEvents(now - KeepEvents, take));
            var serverEvents = await Rule("server events", take => retention.DeleteGuildEvents(now - KeepEvents, take));
            var jobRuns = await Rule("job runs", take => retention.DeleteJobRuns(now - KeepEvents, take));

            // The days before today are totalled before their samples go, and only while all of their samples are kept, so a
            // day's total is never worked out again from part of it. Only whole days of samples go.
            var days = 0;
            ran.Add("uptime totals");

            try
            {
                days = await retention.TotalUptimeDays(keepSamplesFrom, today);
            }
            catch (Exception e)
            {
                Failed("uptime totals", e);
            }

            var minutes = await Rule("uptime minutes", take => retention.DeleteSamples(keepSamplesFrom, take));
            var sessions = await Rule("sessions", take => retention.DeleteSessions(now - KeepSessions, take));

            var detail = string.Create(CultureInfo.InvariantCulture,
                $"deleted {Count(auditRows, "audit row")}, {Count(events, "event")}, {Count(serverEvents, "server event")}, {Count(jobRuns, "job run")}, {Count(minutes, "minute")} ({Count(days, "day")} totalled) and {Count(sessions, "session")}, and cleared {Count(addresses, "address", "addresses")}");

            // After the failures, so a detail that runs past 300 characters loses this rather than MariaDB's number
            var more = unfinished ? "; more is left for the next run" : "";

            if (failed.Count > 0)
                throw new JobFailedException($"{detail}; failed: {Failures(failed, ran)}{more}");

            return detail + more;

            // Chunk after chunk until one comes back short. The ones that went through count even when a later one fails.
            async Task<int> Rule(string rule, Func<int, Task<int>> chunk)
            {
                var total = 0;
                ran.Add(rule);

                try
                {
                    for (var chunks = 0; ; chunks++)
                    {
                        if (chunks == MaxChunks || (chunks > 0 && budget.Elapsed >= TimeBudget))
                        {
                            unfinished = true;
                            break;
                        }

                        var count = await chunk(ChunkSize);
                        total += count;

                        if (count < ChunkSize)
                            break;
                    }
                }
                catch (Exception e)
                {
                    Failed(rule, e);
                }

                return total;
            }

            // The exception's type and MariaDB's error number, never its message, which can name the login
            void Failed(string rule, Exception e)
            {
                var problem = FailureLog.Describe(e);

                failed.Add((rule, problem));
                logger.LogWarning("The daily clean-up's rule for {Rule} failed ({Problem})", rule, problem);
            }
        }

        // The rules that failed the same way go together, such as "events and sessions (MySqlException, MySQL error 1042)",
        // named by the rules themselves or by the ones that didn't fail that way, whichever is shorter: "every rule (...)" or
        // "every rule but audit rows (...)" on a night when the database was down for all or part of the run. So on a night
        // when every rule that failed failed the same way, with every count under 10,000, MariaDB's number stays inside the
        // details' 300 characters, the event's with the job's name before it.
        static string Failures(List<(string Rule, string Problem)> failed, List<string> ran) =>
            string.Join("; ", failed.GroupBy(failure => failure.Problem).Select(same =>
            {
                var rules = same.Select(failure => failure.Rule).ToArray();
                var others = ran.Except(rules).ToArray();
                var named = Names(rules);
                var byOthers = others.Length == 0 ? "every rule" : $"every rule but {Names(others)}";

                return $"{(byOthers.Length < named.Length ? byOthers : named)} ({same.Key})";
            }));

        static string Names(string[] rules) => rules.Length == 1 ? rules[0] : $"{string.Join(", ", rules[..^1])} and {rules[^1]}";

        static string Count(int count, string one, string? many = null) =>
            string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : many ?? one + "s")}");
    }
}
