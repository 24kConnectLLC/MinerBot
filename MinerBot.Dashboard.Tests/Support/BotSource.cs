using System.Text.RegularExpressions;

namespace MinerBot.Dashboard.Tests.Support
{
    // The bot's own lists of its event kinds, read as text: BotEvents' constants and SteamEvents', which the bot's list
    // names by constant, so both files hold every kind between them. The test's build copies the two files next to it,
    // which adds no reference to the bot.
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

        [GeneratedRegex("public const string [A-Za-z0-9_]+ = \"([a-z_]+\\.[a-z_]+)\";")]
        private static partial Regex KindConstant();

        [GeneratedRegex(@"\[([^\]\r\n]+)\]\s*=\s*EventLevel\.")]
        private static partial Regex LevelKey();
    }
}
