using System.Globalization;
using System.Text;

namespace MinerBot_2._0
{
    public static class UIUtils
    {
        // Host names are shown inside inline code, where Discord reads no markdown, so every script, mark, emoji and
        // symbol stays. Only what would end the code or break up the row goes: a backtick becomes an apostrophe, as in map
        // names, and control characters, line and paragraph separators, the controls that reorder text and halves of
        // surrogate pairs are removed. A letter keeps at most four marks in a row.
        public static string cleanTextForEmbed(string text)
        {
            var cleaned = new StringBuilder(text.Length);
            int marksInARow = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    if (KeepAfterMarks(CharUnicodeInfo.GetUnicodeCategory(char.ConvertToUtf32(c, text[i + 1])), ref marksInARow))
                        cleaned.Append(c).Append(text[i + 1]);

                    i++;
                }
                else if (c == '`')
                {
                    cleaned.Append('\'');
                    marksInARow = 0;
                }
                else if (!char.IsControl(c) && !char.IsSurrogate(c) && !IsLineBreakOrDirection(c) && KeepAfterMarks(char.GetUnicodeCategory(c), ref marksInARow))
                    cleaned.Append(c);
            }

            return cleaned.ToString();
        }

        // Every script needs only a few marks on a letter, and a pile of them is drawn over the rows above and below the
        // name. A character that isn't seen, such as a zero-width joiner, doesn't start a new pile.
        const int MaxMarksInARow = 4;

        static bool KeepAfterMarks(UnicodeCategory category, ref int marksInARow)
        {
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
                return ++marksInARow <= MaxMarksInARow;

            if (category != UnicodeCategory.Format)
                marksInARow = 0;

            return true;
        }

        // The embedding, override and isolate controls can reorder the rest of the row
        static bool IsLineBreakOrDirection(char c) =>
            c is '\u2028' or '\u2029' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');

        public static string? shortenTextForEmbed(string? text, int maxLength)
        {
            if (text == null || text.Length <= maxLength)
                return text;

            const string ending = "...";
            int length = maxLength - ending.Length;

            // Half of a surrogate pair isn't a valid character
            if (char.IsHighSurrogate(text[length - 1]))
                length--;

            return text.Substring(0, length) + ending;
        }
    }
}
