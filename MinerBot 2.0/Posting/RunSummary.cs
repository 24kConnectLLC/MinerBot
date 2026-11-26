using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;

namespace MinerBot_2._0.Posting
{
    // The detail of a run that delivered, which the Overview's last notification run and Announce now show: the baselines it
    // took, an armor list that couldn't be read, then what it posted by kind, such as "1 armor: posted in 5 of 6 servers, 1
    // failed; 2 maps: posted in 6 of 6 servers", what still waits, or "nothing new to post", and why it stopped posting before
    // it was through. Never empty, so an older bot's runs, which have no detail, are told apart, and at most 300 characters,
    // as job_runs keeps them.
    public static class RunSummary
    {
        public const int Limit = 300;

        public static string Of(IReadOnlyList<TakenBaseline> baselines, string? armorProblem, DeliveryReport report, bool foundNew)
        {
            var posted = PostSource.Posted
                .Where(source => report.Kinds.TryGetValue(source, out var tally) && tally.Tried > 0)
                .Select(source => Posted(source, report.Kinds[source]))
                .ToList();

            var (items, servers) = Waiting(report.Waiting);
            var parts = new List<string>();

            if (baselines.Count > 0)
                parts.Add(posted.Count == 0 ? $"{PostingTexts.Baselines(baselines)} and {PostingTexts.PostedNothing}" : PostingTexts.Baselines(baselines));

            if (armorProblem != null)
                parts.Add(PostingTexts.ArmorUnreadable(armorProblem));

            if (posted.Count > 0 || baselines.Count > 0)
            {
                parts.AddRange(posted);

                if (items > 0)
                    parts.Add(PostingTexts.Waiting(items, servers));
            }
            else if (items == 0)
                parts.Add(PostingTexts.NothingNew);
            else
                parts.Add(foundNew ? PostingTexts.Waiting(items, servers) : PostingTexts.StillWait(items, servers));

            // Why it stopped posting before it was through
            if (report.Stopped is { } stop)
                parts.Add(PostingTexts.Stopped(stop, report.Problem));

            return Clip(string.Join("; ", parts));
        }

        static string Posted(string source, KindTally tally) => PostingTexts.Posted(source, tally.Items.Count, tally.Tried, tally.Posted, tally.Failed, tally.Unanswered);

        // The items that wait for a server, each counted once however many servers it waits for, and those servers
        public static (int Items, int Servers) Waiting(IEnumerable<QueueRow> rows)
        {
            var list = rows.ToList();
            return (list.Select(row => (row.source, row.item_id)).Distinct().Count(), list.Select(row => row.guild_id).Distinct().Count());
        }

        public static string Clip(string detail) => detail.Length <= Limit ? detail : detail[..(Limit - 3)] + "...";
    }
}
