using System.Globalization;
using System.Text.Json;
using DataAccessLibrary.Blacklist.Models;

namespace MinerBot.Dashboard.Blacklist
{
    // The audit log's JSON for a blacklist entry, under fixed keys the audit page reads: the whole entry for an add, a
    // removal or a refusal, and only what changed for an edit. The end date is written yyyy-MM-dd HH:mm:ss, in UTC, or as
    // the word unreadable for a stored value that isn't a real date.
    internal static class BlacklistJson
    {
        public const string Name = "Name";
        public const string Reason = "Reason";
        public const string Expires = "Expires";
        public const string Notes = "Notes";
        public const string Unreadable = "unreadable";

        public static string Whole(BlacklistModel entry) => Whole(entry.username, entry.reason, entry.expire_date, entry.expire_date_unreadable, entry.notes);

        public static string Whole(OutOfRangeBan row) => Whole(row.username, row.reason, row.expire_date, row.expire_date_unreadable, row.notes);

        // Only the fields that differ, before and after, in the same order as the whole entry's, or two nulls when none
        // does. An end date that wasn't a real date always differs, since saving replaces it.
        public static (string? Before, string? After) Changed(BlacklistModel before, BlacklistModel after)
        {
            var was = new Dictionary<string, object?>();
            var now = new Dictionary<string, object?>();

            Compare(Name, before.username, after.username);
            Compare(Reason, before.reason, after.reason);
            Compare(Expires, ExpiresText(before.expire_date, before.expire_date_unreadable), ExpiresText(after.expire_date, after.expire_date_unreadable));
            Compare(Notes, before.notes, after.notes);

            return was.Count == 0 ? (null, null) : (JsonSerializer.Serialize(was), JsonSerializer.Serialize(now));

            void Compare(string key, string? old, string? changed)
            {
                if (string.Equals(old, changed, StringComparison.Ordinal))
                    return;

                was[key] = old;
                now[key] = changed;
            }
        }

        public static string? ExpiresText(DateTime? expires, bool unreadable) =>
            unreadable ? Unreadable : expires?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        static string Whole(string? name, string? reason, DateTime? expires, bool unreadable, string? notes) =>
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [Name] = name,
                [Reason] = reason,
                [Expires] = ExpiresText(expires, unreadable),
                [Notes] = notes
            });
    }
}
