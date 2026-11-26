using System.Globalization;

namespace MinerBot_2._0.Jobs
{
    // The new-submission alerts' words: the direct message to the owner, what the bot's history keeps of it, and each run's
    // detail, which the Bot page shows. Counts, dates, times, Discord's numbers and the dashboard's fixed link, never an
    // armor's name or its author's, which are the submitter's own text.
    internal static class SubmissionAlertTexts
    {
        // The message, and what alert.sent or alert.failed keeps in its place

        public static string Message(int fresh, int pending, DateTime oldest) =>
            Text($"New armor is waiting for review: {Count(fresh, "submission")} since the last message, {pending} pending in all, the oldest sent on {oldest:yyyy-MM-dd} by the website's clock. Review them at {SubmissionAlertJob.PendingLink} (MinerBot sends this at most once an hour.)");

        public static string Record(int fresh, int pending) => Text($"new armor submissions: {fresh} new, {pending} pending");

        // The runs' details

        public static string Skipped(string problem) => $"skipped, because ConnectionStrings:BotConnection {problem}";

        public const string DiscordNotReady = "Discord isn't ready yet, so this run didn't look for new submissions";

        public static string WebsiteDidntAnswer(string problem) => $"couldn't read the armor waiting for a review ({problem})";

        public static string DatabaseDidntAnswer(string problem) => $"new-submission alerts wait: MinerBot's database didn't answer ({problem})";

        public static string FirstLook(int pending, ulong highest) =>
            Text($"took {Count(pending, "pending submission")} as seen, and every armor up to ID {highest} as known, so no message went");

        public static string FirstLookNotSaved(string problem) => $"MinerBot's database didn't take the first look ({problem}), so the next run looks again";

        public static string NoneNew(int pending) => Text($"no new submissions ({pending} pending)");

        public static string WaitsForTheHour(int fresh, DateTime lastTry) =>
            Text($"{Count(fresh, "new submission")} {(fresh == 1 ? "waits" : "wait")} for the hourly message (the last try was at {lastTry:HH:mm} UTC)");

        public static string TryNotSaved(string problem) => $"MinerBot's database didn't take the try ({problem}), so no message went";

        public static string Sent(int fresh, int pending) => Text($"told the owner about {Count(fresh, "new submission")} ({pending} pending)");

        public static string Failed(string problem, DateTime nextTry) => Text($"the message to the owner failed ({problem}); the next try is after {nextTry:HH:mm} UTC");

        public static string FailedNotRecorded(string problem, string databaseProblem) =>
            $"the message to the owner failed ({problem}), and MinerBot's database didn't take that ({databaseProblem}), so these submissions aren't told about again";

        public static string Unanswered(int fresh, int pending, TimeSpan limit) =>
            Text($"the message to the owner about {Count(fresh, "new submission")} got no answer within {limit.TotalSeconds:0} seconds, so it may still arrive, and isn't sent again ({pending} pending)");

        public static string NoAnswer(int fresh, int pending, string problem) =>
            Text($"Discord didn't answer the message to the owner about {Count(fresh, "new submission")} ({problem}), so it may have arrived, and isn't sent again ({pending} pending)");

        static string Count(int count, string one) => Text($"{count} {(count == 1 ? one : one + "s")}");

        static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }
}
