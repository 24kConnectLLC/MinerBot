using System.Text.RegularExpressions;

namespace MinerBot.Dashboard.Tests.Support
{
    // The bot's own lists of its event kinds, read as text: BotEvents' constants and SteamEvents', which the bot's list
    // names by constant, so both files hold every kind between them; and its jobs. The test's build copies the files next
    // to it, which adds no reference to the bot.
    internal static partial class BotSource
    {
        static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "bot-source");

        public static IReadOnlyList<string> EventKinds()
        {
            return new[] { "BotEvents.cs", "SteamStatus.cs" }
                .SelectMany(file => KindConstant().Matches(File.ReadAllText(Path.Combine(Folder, file))).Select(match => match.Groups[1].Value))
                .Distinct()
                .Order(StringComparer.Ordinal)
                .ToList();
        }

        // Each key of BotEvents' list of the kinds and their levels, the list the bot records by, as written: BotStarted, or
        // SteamEvents.LoggedOn for another class's constant, which may live in a file not read here
        public static IReadOnlyList<string> ListedKinds() =>
            LevelKey().Matches(File.ReadAllText(Path.Combine(Folder, "BotEvents.cs"))).Select(match => match.Groups[1].Value).ToList();

        // The bot's jobs as JobHandler finds and names them, <Class>.<Method>, each with its cron: every [Job("cron")] method in
        // the bot's Jobs folder, where every job is
        public static IReadOnlyList<(string Job, string Cron)> Jobs()
        {
            var jobs = new List<(string Job, string Cron)>();

            foreach (var file in Directory.GetFiles(Path.Combine(Folder, "Jobs"), "*.cs"))
            {
                var text = File.ReadAllText(file);
                var name = ClassName().Match(text).Groups[1].Value;

                jobs.AddRange(JobMethod().Matches(text).Select(match => ($"{name}.{match.Groups[2].Value}", match.Groups[1].Value)));
            }

            return [.. jobs.OrderBy(job => job.Job, StringComparer.Ordinal)];
        }

        [GeneratedRegex("public const string [A-Za-z0-9_]+ = \"([a-z_]+\\.[a-z_]+)\";")]
        private static partial Regex KindConstant();

        [GeneratedRegex(@"public class (\w+)")]
        private static partial Regex ClassName();

        // The attribute at a line's start, so one that's commented out isn't a job, and the method declared on the next line
        [GeneratedRegex(@"^[ \t]*\[Job\(""([^""]+)""\)\][^\n]*\n[ \t]*public [^(\n]*?\b(\w+)\(", RegexOptions.Multiline)]
        private static partial Regex JobMethod();

        [GeneratedRegex(@"\[([^\]\r\n]+)\]\s*=\s*EventLevel\.")]
        private static partial Regex LevelKey();
    }
}
