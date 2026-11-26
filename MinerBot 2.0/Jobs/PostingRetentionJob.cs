using DataAccessLibrary.Posting;
using MinerBot_2._0.Attributes;
using MinerBot_2._0.Models;
using MinerBot_2._0.Status;
using System.Diagnostics;

namespace MinerBot_2._0.Jobs
{
    // The nightly clean-up of the record of the bot's posts, apart from HousekeepingJob's: first each day's totals of the
    // posts that went, failed or may not have arrived, and of the items dropped, worked out again from the record for every
    // day it still holds whole, so a post settled late counts and a run repeated changes nothing; then the post records and
    // the settled queue of maps and lobbies after 90 days (armor's stays, the record of where each armor went), the lobbies
    // not seen for 7 days, and the lobby samples after 90 days. The record and the samples go in whole days only, so a day's
    // totals are never worked out from part of it, and a post or an item still open never goes. Every cutoff comes from the
    // bot's own clock, never the database's. Each rule goes in chunks until one comes back short, and every rule runs even
    // when one before it failed; the job then fails, naming the rules that did, with the exception's type and MariaDB's
    // error number, and the log has a line for each. Without BotConnection it does nothing, as the clean-up.
    public class PostingRetentionJob(IPostingRetentionData retention, TimeProvider clock, IConfiguration configuration, ILogger<PostingRetentionJob> logger)
    {
        internal const int ChunkSize = PostingRetentionData.ChunkLimit;
        internal const int MaxChunks = 100;

        const int KeepRecordDays = 90;
        const int KeepLobbySampleDays = 90;
        static readonly TimeSpan KeepSeenLobbies = TimeSpan.FromDays(7);

        // The totals and the four deletes, which every run runs
        const int Rules = 5;

        // After this, no rule starts a chunk but its first, so the job ends well inside JobHandler's limit of 270 seconds even
        // when every statement is slow: the chunk under way and the later rules' first chunks make up to five calls after
        // it, each inside its own limit. Short, since the run shares its pass with the notification run at 04:35.
        internal TimeSpan TimeBudget = TimeSpan.FromSeconds(60);

        [Job("33 4 * * *")] // 04:33 US Eastern every day, after the clean-up, and off the notification run's 5-minute marks
        public async Task<string> ApplyRetention()
        {
            if (StatusWriter.BotConnectionProblem(configuration) is { } problem)
                return PostingRetentionTexts.Skipped(problem);

            var now = clock.GetUtcNow().UtcDateTime;
            var today = now.Date;
            var keepRecordFrom = today.AddDays(-KeepRecordDays);
            var budget = Stopwatch.StartNew();
            var failed = new List<(string Rule, string Problem)>();
            var unfinished = false;

            // The days before today, totalled before any of their rows can go, from the first day the record still holds whole
            var days = 0;

            try
            {
                days = await retention.TotalDeliveryDays(keepRecordFrom, today);
            }
            catch (Exception e)
            {
                Failed(PostingRetentionTexts.Totals, e);
            }

            var deliveries = await Rule(PostingRetentionTexts.Deliveries, take => retention.DeleteDeliveries(keepRecordFrom, take));
            var queued = await Rule(PostingRetentionTexts.Queue, take => retention.DeleteSettledQueueRows(keepRecordFrom, take));
            var lobbies = await Rule(PostingRetentionTexts.SeenLobbies, take => retention.DeleteSeenLobbies(now - KeepSeenLobbies, take));
            var samples = await Rule(PostingRetentionTexts.LobbySamples, take => retention.DeleteLobbySamples(today.AddDays(-KeepLobbySampleDays), take));

            var detail = PostingRetentionTexts.Done(deliveries, queued, lobbies, samples, days);

            // After the failures, so a detail that runs past 300 characters loses this rather than MariaDB's number
            var more = unfinished ? PostingRetentionTexts.MoreLeft : "";

            if (failed.Count > 0)
                throw new JobFailedException(detail + PostingRetentionTexts.Failed(failed, Rules) + more);

            return detail + more;

            // Chunk after chunk until one comes back short. The ones that went through count even when a later one fails.
            async Task<int> Rule(string rule, Func<int, Task<int>> chunk)
            {
                var total = 0;

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
                logger.LogWarning("The post records clean-up's rule for {Rule} failed ({Problem})", rule, problem);
            }
        }
    }
}
