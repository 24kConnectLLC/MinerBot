using System.Text.RegularExpressions;
using Discord;

namespace MinerBot_2._0
{
    // Text that map authors write on Steam, made into plain text for an embed, where a description could otherwise show
    // a link under deceptive text
    internal static partial class WorkshopText
    {
        // Pictures and videos can't be shown in an embed's text, so they go with what's inside them. A block can't take in
        // the opening of another, so a picture missing its closing tag doesn't take the text up to the next picture's.
        [GeneratedRegex(@"\[img(?:=[^\]]*|\s+\w+\s*=[^\]]*|\s*)\](?:(?!\[img[=\s\]]).)*?\[/img\]|\[previewyoutube(?:=[^\]]*|\s+\w+\s*=[^\]]*|\s*)\](?:(?!\[previewyoutube[=\s\]]).)*?\[/previewyoutube\]", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
        private static partial Regex MediaBlock();

        [GeneratedRegex(@"\s*\[\*\]\s*", RegexOptions.CultureInvariant)]
        private static partial Regex ListItem();

        // The rest of Steam's tags, with or without an attribute such as [url=...] or [table noborder=1], and tags in a row
        // are taken out together. Their text stays, and so does bracketed text that isn't one of them, such as [WIP] or
        // [spoiler alert], since an attribute always has an equals sign.
        [GeneratedRegex(@"(?:\[/?(?<name>h[1-3]|b|u|i|strike|spoiler|noparse|hr|list|olist|quote|code|table|tr|th|td|url|img|previewyoutube)(?:=[^\]]*|\s+\w+\s*=[^\]]*|\s*)\])+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Tags();

        // Headings, subtext and lists, which Discord only reads at the start of a line
        [GeneratedRegex(@"^([ \t]*)([#+-])", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
        private static partial Regex LineStart();

        public static string StripBBCode(string text)
        {
            text = MediaBlock().Replace(AsDiscordReadsIt(text), "");
            text = ListItem().Replace(text, "\n\u2022 ");
            return Tags().Replace(text, tags => Separator(text, tags)).Trim();
        }

        // On Steam a heading, a rule, a quote, code, a list and a table's row each start a new line, and a cell has its own
        // column, so tags taken out from between two words leave a line break or a space instead of joining the words
        private static string Separator(string text, Match tags)
        {
            int end = tags.Index + tags.Length;

            if (tags.Index == 0 || end == text.Length || char.IsWhiteSpace(text[tags.Index - 1]) || char.IsWhiteSpace(text[end]))
                return "";

            var names = tags.Groups["name"].Captures.Select(name => name.Value.ToLowerInvariant()).ToList();

            if (names.Any(name => name is "h1" or "h2" or "h3" or "hr" or "list" or "olist" or "quote" or "code" or "table" or "tr"))
                return "\n";

            return names.Any(name => name is "th" or "td") ? " " : "";
        }

        // Format.Sanitize escapes the markdown within a line, but not a masked link or what starts a line. It already
        // breaks mentions, channels, emoji, timestamps and <links> by escaping the > each ends with, and < is escaped
        // as well, since Discord's own patterns aren't public. Discord shows punctuation after a backslash as it is.
        public static string EscapeMarkdown(string text)
        {
            text = Format.Sanitize(AsDiscordReadsIt(text)).Replace("[", "\\[").Replace("<", "\\<");
            return LineStart().Replace(text, "$1\\$2");
        }

        // Discord's parser first makes every carriage return a line feed and drops form feeds, so a line can start
        // after either. The BBCode is read that way too, or a form feed beside a tag taken out would count as the space
        // between two words and then go, joining them.
        private static string AsDiscordReadsIt(string text) =>
            text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\f", "");

        // UIUtils.shortenTextForEmbed for escaped text, where a cut right after a backslash would leave it to escape the
        // first dot of the ending, so a backslash without its character goes too
        public static string? ShortenEscaped(string? text, int maxLength)
        {
            if (text == null || text.Length <= maxLength)
                return text;

            const string ending = "...";
            int length = maxLength - ending.Length;

            // Half of a surrogate pair isn't a valid character
            if (char.IsHighSurrogate(text[length - 1]))
                length--;

            // An escaped backslash is two of them, so an odd run at the cut ends on an escape that lost its character
            int backslashes = 0;
            while (backslashes < length && text[length - 1 - backslashes] == '\\')
                backslashes++;

            if (backslashes % 2 == 1)
                length--;

            return text.Substring(0, length) + ending;
        }

        // Steam's reader gives UTC without saying so, and the default when Steam sent no time. Discord shows a timestamp
        // in each reader's own zone and language.
        public static string DiscordTimestamp(DateTime time) => time == default
            ? "Unknown"
            : $"<t:{new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToUnixTimeSeconds()}:f>";
    }
}
