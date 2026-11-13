using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DataAccessLibrary.Blacklist.Models;

namespace MinerBot.Dashboard.Blacklist
{
    // Everything an entry's page shows of it, as one short value its forms send back. A change applies only while the
    // locked row still gives the same value, so an entry someone else changed after the editor opened it is a conflict,
    // never a change to something they didn't see. Worked out from the row as it's read back, never from what a form
    // sent: the column cuts fractions of a second, for one.
    internal static class BlacklistVersion
    {
        public const int Length = 64;

        public static string Of(BlacklistModel entry) => Of(entry.username, entry.reason, entry.expire_date, entry.expire_date_unreadable, entry.notes);

        public static string Of(OutOfRangeBan row) => Of(row.username, row.reason, row.expire_date, row.expire_date_unreadable, row.notes);

        static string Of(string? name, string? reason, DateTime? expires, bool unreadable, string? notes)
        {
            var shown = new StringBuilder();

            Add(shown, name);
            Add(shown, reason);
            Add(shown, expires?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            Add(shown, unreadable ? "unreadable" : "readable");
            Add(shown, notes);

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shown.ToString())));
        }

        // Exactly what Of makes: 64 hex digits in small letters
        public static bool IsWellFormed(string? version) => version is { Length: Length } && version.All(char.IsAsciiHexDigitLower);

        // Each value with its length in front, so no two rows run together into the same text, and null apart from empty
        static void Add(StringBuilder shown, string? value)
        {
            if (value == null)
                shown.Append("-;");
            else
                shown.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
        }
    }
}
