using System.Globalization;

namespace MinerBot.Dashboard.Settings
{
    // A list of Discord user IDs from the settings, separated by commas, semicolons or white space. Each is a number of 17
    // to 20 ASCII digits, so a user name, a mention or a typo gets caught rather than matching nobody.
    internal static class DiscordIds
    {
        const int MinDigits = 17;
        const int MaxDigits = 20;

        // The problem never repeats the text, so it can go into a start-up message as it is
        public static bool TryParse(string? text, out IReadOnlySet<ulong> ids, out string? problem)
        {
            var found = new HashSet<ulong>();
            var entry = 0;

            foreach (var part in (text ?? "").Replace(',', ' ').Replace(';', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                entry++;

                if (part.Length is < MinDigits or > MaxDigits || !part.All(char.IsAsciiDigit)
                    || !ulong.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
                {
                    ids = new HashSet<ulong>();
                    problem = $"entry {entry} isn't a Discord user ID, which is {MinDigits} to {MaxDigits} digits";
                    return false;
                }

                found.Add(id);
            }

            ids = found;
            problem = null;
            return true;
        }
    }
}
