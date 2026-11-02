using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MinerBot_2._0
{
    public static class UIUtils
    {
        public static string cleanTextForEmbed(string text)
        {
            return new string(Array.FindAll<char>(text.ToArray(), (c => (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c == '-'))));
        }

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
