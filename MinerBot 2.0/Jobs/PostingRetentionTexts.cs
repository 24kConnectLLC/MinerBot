using System.Globalization;

namespace MinerBot_2._0.Jobs
{
    // The post records clean-up's words: each run's detail, which the Bot page shows. Counts, the rules' names, and the
    // exception's type with MariaDB's number, never its message.
    internal static class PostingRetentionTexts
    {
        public static string Skipped(string problem) => $"skipped, because ConnectionStrings:BotConnection {problem}";

        public static string Done(int deliveries, int queued, int lobbies, int samples, int days) =>
            Text($"deleted {Count(deliveries, "post record")}, {Count(queued, "queued item")}, {Count(lobbies, "seen lobby", "seen lobbies")} and {Count(samples, "lobby sample")}; totalled {Count(days, "day")}");

        public const string MoreLeft = "; more is left for the next run";

        // The rules that failed the same way go together, such as "post records and queued items (MySqlException, MySQL
        // error 1042)", or "every rule" when all of them did, as on a night the database is down. So on a night when every
        // rule that failed failed the same way, MariaDB's number stays inside the details' 300 characters, the event's with
        // the job's name before it.
        public static string Failed(IReadOnlyCollection<(string Rule, string Problem)> failed, int rules) =>
            "; failed: " + string.Join("; ", failed.GroupBy(failure => failure.Problem)
                .Select(same => $"{(same.Count() == rules ? "every rule" : And(same.Select(failure => failure.Rule)))} ({same.Key})"));

        // The rules' names, in the details and the log
        public const string Totals = "daily totals";
        public const string Deliveries = "post records";
        public const string Queue = "queued items";
        public const string SeenLobbies = "seen lobbies";
        public const string LobbySamples = "lobby samples";

        static string And(IEnumerable<string> parts)
        {
            var list = parts.ToList();
            return list.Count <= 1 ? string.Concat(list) : $"{string.Join(", ", list.Take(list.Count - 1))} and {list[^1]}";
        }

        static string Count(int count, string one, string? many = null) => Text($"{count} {(count == 1 ? one : many ?? one + "s")}");

        static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }
}
