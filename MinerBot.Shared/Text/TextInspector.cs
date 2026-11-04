using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MinerBot.Shared.Text
{
    // A character that shows nothing, or changes the direction of what follows. Index counts UTF-16 characters, like a
    // string's own indexes, so a character outside the Basic Multilingual Plane takes two.
    public sealed record HiddenCharacter(int Index, int CodePoint, string Name, bool ChangesDirection)
    {
        // As Reveal shows it, such as U+200B
        public string Code => $"U+{CodePoint:X4}";
    }

    public sealed record TextMatch(int Index, string Text);

    public sealed record TextFindings(IReadOnlyList<HiddenCharacter> Hidden, IReadOnlyList<TextMatch> Mentions, IReadOnlyList<TextMatch> Links)
    {
        public bool HasHidden => Hidden.Count > 0;

        public bool HasDirectionChanges => Hidden.Any(character => character.ChangesDirection);
    }

    // Finds what a reviewer can't see or might miss in text someone else wrote: characters that show nothing or turn the
    // text around, Discord's mention syntax, and links
    public static class TextInspector
    {
        static readonly TimeSpan MatchLimit = TimeSpan.FromSeconds(1);

        // [0-9] rather than \d, which also takes other scripts' digits, and Discord only reads these ones as an ID
        static readonly Regex Mention = new(@"@(?:everyone|here)|<@[!&]?[0-9]+>|<#[0-9]+>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchLimit);

        // A masked link comes first, so it's found whole rather than as the address inside it
        static readonly Regex Link = new(@"\[[^\]\r\n]*\]\([^)\r\n]*\)|[a-z][a-z0-9+.\-]*://[^\s<>]+|\bwww\.[^\s<>]+|\bdiscord\.gg/[^\s<>]+|\bdiscord(?:app)?\.com/invite/[^\s<>]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchLimit);

        static readonly TextFindings Nothing = new([], [], []);

        // Code points that draw nothing but aren't format characters: the rest of Unicode's default-ignorable ones, which are
        // marks such as the combining grapheme joiner and the variation selectors, the Hangul fillers, and the code points
        // kept unassigned so that whatever goes there later draws nothing either. And the blank Braille pattern, which isn't
        // one of them but looks the same.
        static readonly (int First, int Last)[] Blanks =
        [
            (0x034F, 0x034F), (0x115F, 0x1160), (0x17B4, 0x17B5), (0x180B, 0x180F), (0x2065, 0x2065), (0x2800, 0x2800),
            (0x3164, 0x3164), (0xFE00, 0xFE0F), (0xFFA0, 0xFFA0), (0xFFF0, 0xFFF8), (0xE0000, 0xE0FFF)
        ];

        // Variation selectors 15 and 16, which pick the text or the emoji look of the character before them
        const int TextStyle = 0xFE0E;
        const int EmojiStyle = 0xFE0F;

        static readonly HashSet<int> DirectionChanges = [0x061C, 0x200E, 0x200F, 0x202A, 0x202B, 0x202C, 0x202D, 0x202E, 0x2066, 0x2067, 0x2068, 0x2069];

        static readonly Dictionary<int, string> Names = new()
        {
            [0x00AD] = "soft hyphen",
            [0x034F] = "combining grapheme joiner",
            [0x061C] = "Arabic letter mark",
            [0x115F] = "Hangul choseong filler",
            [0x1160] = "Hangul jungseong filler",
            [0x17B4] = "Khmer inherent vowel",
            [0x17B5] = "Khmer inherent vowel",
            [0x180E] = "Mongolian vowel separator",
            [0x200B] = "zero-width space",
            [0x200C] = "zero-width non-joiner",
            [0x200D] = "zero-width joiner",
            [0x200E] = "left-to-right mark",
            [0x200F] = "right-to-left mark",
            [0x202A] = "left-to-right embedding",
            [0x202B] = "right-to-left embedding",
            [0x202C] = "pop directional formatting",
            [0x202D] = "left-to-right override",
            [0x202E] = "right-to-left override",
            [0x2060] = "word joiner",
            [0x2061] = "function application",
            [0x2062] = "invisible times",
            [0x2063] = "invisible separator",
            [0x2064] = "invisible plus",
            [0x2066] = "left-to-right isolate",
            [0x2067] = "right-to-left isolate",
            [0x2068] = "first strong isolate",
            [0x2069] = "pop directional isolate",
            [0x2800] = "Braille pattern blank",
            [0x3164] = "Hangul filler",
            [0xFE0E] = "text style selector",
            [0xFE0F] = "emoji style selector",
            [0xFEFF] = "zero-width no-break space",
            [0xFFA0] = "halfwidth Hangul filler",
            [0xE0001] = "language tag"
        };

        public static TextFindings Inspect(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return Nothing;

            return new TextFindings(FindHidden(text), Matches(Mention, text), Matches(Link, text));
        }

        // Each hidden character becomes its code, such as a[U+200B]b, so a reviewer sees where it is
        public static string Reveal(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            var hidden = FindHidden(text);

            if (hidden.Count == 0)
                return text;

            var revealed = new StringBuilder(text.Length + hidden.Count * 8);
            var next = 0;

            foreach (var character in hidden)
            {
                revealed.Append(text, next, character.Index - next).Append('[').Append(character.Code).Append(']');
                next = character.Index + (character.CodePoint > 0xFFFF ? 2 : 1);
            }

            return revealed.Append(text, next, text.Length - next).ToString();
        }

        // Stops at the first one, so a long text costs no more than it has to
        internal static bool HasHidden(string text) => Hidden(text).Any();

        // Unicode's format characters (zero-width ones, direction controls, the byte order mark, soft hyphens and the tag
        // characters), control characters other than tab and line ends, the blanks above, and a half of a broken pair.
        // A style selector counts wherever it is, which is what telling whether a text shows anything needs. Inspect leaves
        // out one that styles the character before it.
        internal static bool IsHidden(int codePoint)
        {
            if (codePoint is '\t' or '\r' or '\n')
                return false;

            foreach (var (first, last) in Blanks)
            {
                if (codePoint >= first && codePoint <= last)
                    return true;
            }

            return CharUnicodeInfo.GetUnicodeCategory(codePoint) is UnicodeCategory.Format or UnicodeCategory.Control or UnicodeCategory.Surrogate;
        }

        static List<HiddenCharacter> FindHidden(string text) => Hidden(text).ToList();

        // Read one code point at a time, so a character outside the Basic Multilingual Plane, such as a tag, is one character
        static IEnumerable<HiddenCharacter> Hidden(string text)
        {
            var previous = -1;

            for (var index = 0; index < text.Length; index += char.IsSurrogatePair(text, index) ? 2 : 1)
            {
                var codePoint = char.IsSurrogatePair(text, index) ? char.ConvertToUtf32(text[index], text[index + 1]) : text[index];
                var styles = (codePoint is TextStyle or EmojiStyle) && previous >= 0 && TakesStyle(previous);

                if (IsHidden(codePoint) && !styles)
                    yield return new HiddenCharacter(index, codePoint, NameOf(codePoint), DirectionChanges.Contains(codePoint));

                previous = codePoint;
            }
        }

        // Symbols and punctuation have a text and an emoji look, as a heart does, and so do the digits, # and * that keycaps
        // are made from. The information sign is a letter to Unicode, but has both looks too. After anything else, such as
        // a letter or a space, a style selector changes nothing and shows nothing.
        static bool TakesStyle(int codePoint) =>
            codePoint is (>= '0' and <= '9') or '#' or '*' or 0x2139
            || CharUnicodeInfo.GetUnicodeCategory(codePoint) is UnicodeCategory.OtherSymbol or UnicodeCategory.MathSymbol
                or UnicodeCategory.OtherPunctuation or UnicodeCategory.DashPunctuation;

        static string NameOf(int codePoint)
        {
            if (Names.TryGetValue(codePoint, out var name))
                return name;

            if (codePoint is >= 0xE0020 and <= 0xE007F)
                return "tag character";

            if (codePoint is (>= 0xFE00 and <= 0xFE0D) or (>= 0xE0100 and <= 0xE01EF))
                return "variation selector";

            if (codePoint is (>= 0x180B and <= 0x180D) or 0x180F)
                return "Mongolian variation selector";

            return CharUnicodeInfo.GetUnicodeCategory(codePoint) switch
            {
                UnicodeCategory.Format => "invisible formatting character",
                UnicodeCategory.Control => "control character",
                UnicodeCategory.Surrogate => "broken character",
                UnicodeCategory.OtherNotAssigned => "unassigned invisible character",
                _ => "blank character"
            };
        }

        static List<TextMatch> Matches(Regex regex, string text) =>
            regex.Matches(text).Select(match => new TextMatch(match.Index, match.Value)).ToList();
    }
}
