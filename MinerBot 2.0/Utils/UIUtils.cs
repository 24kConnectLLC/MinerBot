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
    }
}
