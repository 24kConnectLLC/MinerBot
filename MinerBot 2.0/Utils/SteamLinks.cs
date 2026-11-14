using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Web;
using SteamKit2;

namespace MinerBot_2._0
{
    // Reads what users type into /maps lookup and /more steam_id, so only Steam's own links and plain IDs reach Steam
    internal static class SteamLinks
    {
        // A Workshop link, or a map's ID on its own
        public static bool TryParseWorkshopId(string? input, out ulong id)
        {
            id = 0;
            string text = Unwrap(input);

            if (IsDigits(text))
                return TryParseId(text, out id);

            if (!TryReadSteamCommunityLink(text, out var uri))
                return false;

            // Steam links a map from both pages
            if (!IsPath(uri.AbsolutePath, "/sharedfiles/filedetails") && !IsPath(uri.AbsolutePath, "/workshop/filedetails"))
                return false;

            return TryParseId(HttpUtility.ParseQueryString(uri.Query)["id"], out id);
        }

        // A profile link by custom URL or by SteamID64, or a SteamID64 on its own. A custom URL gives its name, which Steam
        // still has to look up.
        public static bool TryParseProfile(string? input, out string? vanityName, out ulong steamId64)
        {
            vanityName = null;
            steamId64 = 0;
            string text = Unwrap(input);

            if (IsDigits(text))
                return TryParseIndividual(text, out steamId64);

            if (!TryReadSteamCommunityLink(text, out var uri))
                return false;

            // The segments are "/", "id/" and "name/", and anything after the name, such as games/, is ignored
            var segments = uri.Segments;
            if (segments.Length < 3)
                return false;

            string kind = segments[1].TrimEnd('/');
            string value = Uri.UnescapeDataString(segments[2].TrimEnd('/'));

            if (kind.Equals("id", StringComparison.OrdinalIgnoreCase) && IsVanityName(value))
            {
                vanityName = value;
                return true;
            }

            return kind.Equals("profiles", StringComparison.OrdinalIgnoreCase) && TryParseIndividual(value, out steamId64);
        }

        // Discord users write <link> to keep a link from showing its preview
        static string Unwrap(string? input)
        {
            string text = input?.Trim() ?? "";

            if (text.Length >= 2 && text[0] == '<' && text[^1] == '>')
                text = text[1..^1].Trim();

            return text;
        }

        // http or https in any case, with or without www., and a link without a scheme is read as https. The host is
        // compared whole, so steamcommunity.com.example.com and example.com/steamcommunity.com don't pass.
        static bool TryReadSteamCommunityLink(string text, [NotNullWhen(true)] out Uri? uri)
        {
            if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                text = "https://" + text;

            return Uri.TryCreate(text, UriKind.Absolute, out uri)
                && (uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.steamcommunity.com", StringComparison.OrdinalIgnoreCase));
        }

        static bool IsPath(string path, string expected) =>
            path.Equals(expected, StringComparison.OrdinalIgnoreCase) || path.Equals(expected + "/", StringComparison.OrdinalIgnoreCase);

        static bool IsDigits(string text) => text.Length > 0 && text.All(char.IsAsciiDigit);

        // Digits only, so a sign, spaces or another script's digits aren't read as an ID
        static bool TryParseId(string? text, out ulong id) =>
            ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;

        // Steam's rule for a custom URL. Only ASCII, because char.IsLetterOrDigit takes other scripts and a regex that
        // ignores case takes the Kelvin sign for a k.
        static bool IsVanityName(string name) =>
            name.Length is >= 1 and <= 32 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

        // A valid individual account in the public universe. IsIndividualAccount also takes console accounts, so the
        // type is compared instead.
        static bool TryParseIndividual(string text, out ulong steamId64)
        {
            if (TryParseId(text, out steamId64))
            {
                var steamId = new SteamID(steamId64);

                if (steamId.IsValid && steamId.AccountType == EAccountType.Individual && steamId.AccountUniverse == EUniverse.Public)
                    return true;
            }

            steamId64 = 0;
            return false;
        }
    }
}
