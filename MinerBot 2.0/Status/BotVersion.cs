using System.Text.RegularExpressions;

namespace MinerBot_2._0.Status
{
    // The commit the bot's image was built from: deploy.sh gives it to the build, and the Dockerfile puts it in
    // MINERBOT_REVISION
    public static class BotVersion
    {
        public const string Setting = "MINERBOT_REVISION";

        static readonly Regex Commit = new(@"^[0-9a-fA-F]{7,40}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        // 7 to 40 hex digits, in small letters, or null for anything else, such as an image built without one
        public static string? Parse(string? value) => value != null && Commit.IsMatch(value) ? value.ToLowerInvariant() : null;
    }
}
