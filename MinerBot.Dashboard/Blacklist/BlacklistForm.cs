using System.Globalization;
using System.Text;
using DataAccessLibrary.Blacklist;
using DataAccessLibrary.Blacklist.Models;

namespace MinerBot.Dashboard.Blacklist
{
    // How a text field of the blacklist's forms fails, each with a message of its own
    public enum TextProblem
    {
        None,
        TooLong,
        ControlCharacter
    }

    // What an entry's form boxes hold, as the editor typed them or as the entry has them, and a message for each field
    // whose check failed. SteamId is the add form's alone. ExpiresUnreadable says the stored end date isn't a real date,
    // which the box can't show, and StoredDay is the stored end's own day, which may be outside the box's years.
    public sealed record BlacklistFormValues(string SteamId, string Name, string Reason, string Expires, string Notes, IReadOnlyDictionary<BlacklistField, string> Problems)
    {
        public static readonly BlacklistFormValues Empty = new("", "", "", "", "", new Dictionary<BlacklistField, string>());

        public bool ExpiresUnreadable { get; init; }
        public string? StoredDay { get; init; }

        public string? Problem(BlacklistField field) => Problems.TryGetValue(field, out var problem) ? problem : null;

        // An entry's own values, as its edit form starts. The box holds the stored end's day, and saving that day keeps
        // the stored value whole.
        public static BlacklistFormValues Of(BlacklistModel entry) => new("", entry.username ?? "", entry.reason ?? "", Day(entry) ?? "", entry.notes ?? "", Empty.Problems)
        {
            ExpiresUnreadable = entry.expire_date_unreadable,
            StoredDay = Day(entry)
        };

        // The entry's values as a refused edit sent them, with what the entry itself has for the date box
        public static BlacklistFormValues Sent(BlacklistModel entry, string? name, string? reason, string? expires, string? notes,
            IReadOnlyDictionary<BlacklistField, string> problems) => new("", name ?? "", reason ?? "", expires ?? "", notes ?? "", problems)
        {
            ExpiresUnreadable = entry.expire_date_unreadable,
            StoredDay = Day(entry)
        };

        static string? Day(BlacklistModel entry) => entry.expire_date?.ToString(BlacklistForm.DayFormat, CultureInfo.InvariantCulture);
    }

    // The blacklist forms' fields, checked before anything reaches the database. The data layer throws for text the
    // columns can't hold, which would be a 500, and strict mode's own refusal would look like an outage, so the editor
    // gets a 400 with a message here instead.
    internal static class BlacklistForm
    {
        // The years an end date may have: the date box's own limits, and 9998 leaves room for the day after, which the
        // audit page's filter needs too
        public const int FirstYear = 2000;
        public const int LastYear = 9998;
        public const string FirstDay = "2000-01-01";
        public const string LastDay = "9998-12-31";

        // As an <input type="date"> sends a day, whatever the browser shows
        public const string DayFormat = "yyyy-MM-dd";

        // The SteamID box and the list's search: more than any way of writing a SteamID or a profile link needs, and more
        // than any name the table holds
        public const int SteamIdLimit = 100;

        // The biggest number DECIMAL(10,0) holds, either way round
        const decimal ColumnLimit = 9999999999m;

        // The four fields both of an entry's forms have: what to save, or a message for each field whose check failed, and
        // the text boxes as they were sent. Stored is the entry as an edit's handler read it, and null for an add.
        public static bool TryFields(string? name, string? reason, string? expires, string? notes, BlacklistModel? stored, out BlacklistFields fields,
            out IReadOnlyDictionary<BlacklistField, string> problems)
        {
            var found = new Dictionary<BlacklistField, string>();
            var dated = TryExpires(expires, out var day, stored?.expire_date);

            fields = new BlacklistFields(
                Text(BlacklistField.Name, name, stored?.username, BlacklistData.UsernameLimit),
                Text(BlacklistField.Reason, reason, stored?.reason, BlacklistData.ReasonLimit),
                day,
                Text(BlacklistField.Notes, notes, stored?.notes, BlacklistData.NotesLimit))
            {
                Boxes = new BlacklistBoxes(name, reason, notes),
                ExpiresRefused = !dated
            };

            if (!dated)
                found[BlacklistField.Expires] = BlacklistWords.BadDate;

            problems = found;
            return found.Count == 0;

            string? Text(BlacklistField field, string? value, string? kept, int limit)
            {
                var problem = CheckText(value, limit, out var text);

                // The entry's own text, in a box left as the page filled it, is saved as it's stored, so a tab the website
                // saved isn't the editor's to take out of a box they didn't touch
                if (problem != TextProblem.None && !IsUntouched(kept, value))
                    found[field] = BlacklistWords.Problem(field, problem);

                return text;
            }
        }

        // A stored text as a browser's text box sends it back, before the form trims it. Razor writes a control character
        // in the box's value attribute as a numeric reference, which the HTML parser reads back changed for NUL and most
        // C1 controls, and a text box drops line breaks from its value. Nothing else changes, spaces at the ends included.
        public static string? AsBox(string? stored)
        {
            if (stored == null)
                return null;

            var box = new StringBuilder(stored.Length);

            foreach (var c in stored)
            {
                if (c is not ('\r' or '\n'))
                    box.Append(AsParsed(c));
            }

            return box.ToString();
        }

        // How the HTML parser reads a numeric reference to this character in an attribute, which is how Razor writes
        // every control character: NUL is the replacement character, and 27 of the 32 C1 controls are read as the
        // Windows-1252 characters with those codes (the HTML Standard's table, the same in every browser). 0x81, 0x8D,
        // 0x8F, 0x90 and 0x9D, and every other character, come back as they are.
        internal static char AsParsed(char c) => c switch
        {
            '\u0000' => '\uFFFD',
            '\u0080' => '\u20AC',
            '\u0082' => '\u201A',
            '\u0083' => '\u0192',
            '\u0084' => '\u201E',
            '\u0085' => '\u2026',
            '\u0086' => '\u2020',
            '\u0087' => '\u2021',
            '\u0088' => '\u02C6',
            '\u0089' => '\u2030',
            '\u008A' => '\u0160',
            '\u008B' => '\u2039',
            '\u008C' => '\u0152',
            '\u008E' => '\u017D',
            '\u0091' => '\u2018',
            '\u0092' => '\u2019',
            '\u0093' => '\u201C',
            '\u0094' => '\u201D',
            '\u0095' => '\u2022',
            '\u0096' => '\u2013',
            '\u0097' => '\u2014',
            '\u0098' => '\u02DC',
            '\u0099' => '\u2122',
            '\u009A' => '\u0161',
            '\u009B' => '\u203A',
            '\u009C' => '\u0153',
            '\u009E' => '\u017E',
            '\u009F' => '\u0178',
            _ => c
        };

        // A stored text as the form makes it from a box the editor left alone: as the box sends it back, trimmed, and
        // empty as none. The characters are read as the box sends them before the trim, because .NET trims U+0085 and the
        // box sends U+2026 in its place.
        public static string? AsSent(string? stored)
        {
            var text = AsBox(stored)?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        // Whether a box's text, checked and trimmed, is the stored text as the form makes it from its box. Taking a space
        // off the ends passes this too, so an edit asks IsUntouched of the box as it was sent instead.
        public static bool IsAsStored(string? stored, string? sent) => string.Equals(AsSent(stored), sent, StringComparison.Ordinal);

        // Whether a box, as it was sent, is the stored text as the page filled it: exactly what the page's box sends back.
        // So taking a space off the ends is a change the editor made, and so is clearing a box that showed only spaces. An
        // empty box, or none, is untouched when the page's box showed nothing, such as for no reason or an empty one. The
        // edit handler reads the boxes from the form as they were sent, since model binding hands over a box of only
        // spaces as no text.
        public static bool IsUntouched(string? stored, string? box) =>
            string.Equals(AsBox(stored) ?? "", box ?? "", StringComparison.Ordinal);

        // Trimmed, and empty is none, which the table keeps as NULL. Longer than the column only comes from a form someone
        // made up, since the box stops at the limit in UTF-16 units, which never hold more characters. A line break can't
        // come from a one-line box, but a pasted tab can, and any control character would reach the website's pages as it is.
        public static TextProblem CheckText(string? value, int limit, out string? text)
        {
            text = value?.Trim();

            if (string.IsNullOrEmpty(text))
            {
                text = null;
                return TextProblem.None;
            }

            if (text.Any(char.IsControl))
                return TextProblem.ControlCharacter;

            return text.EnumerateRunes().Count() <= limit ? TextProblem.None : TextProblem.TooLong;
        }

        // A day as the date box sends it, from which the entry no longer applies, at 00:00 that day in UTC, which is
        // how it's stored. Empty is no end. Past days are allowed, for an entry kept on record. The stored end's own day
        // passes whatever its year, so an entry written elsewhere with an end such as 9999-12-31 can still be saved,
        // which keeps that end as it is.
        public static bool TryExpires(string? value, out DateTime? expires, DateTime? stored = null)
        {
            expires = null;

            if (string.IsNullOrEmpty(value))
                return true;

            if (!DateOnly.TryParseExact(value, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return false;

            if (day.Year is < FirstYear or > LastYear && (stored is not { } kept || DateOnly.FromDateTime(kept) != day))
                return false;

            expires = day.ToDateTime(TimeOnly.MinValue);
            return true;
        }

        // Whether a day the date box holds is inside its limits, which an end written elsewhere may not be. Days in
        // yyyy-MM-dd sort as text.
        public static bool WithinLimits(string day) =>
            string.CompareOrdinal(day, FirstDay) >= 0 && string.CompareOrdinal(day, LastDay) <= 0;

        // The number of a row that holds no account number, as the list's form sends it back: a whole number the column
        // can hold, outside the account numbers. decimal.Parse would throw for anything else, and AllowLeadingSign takes
        // no point, spaces or digit groups.
        public static bool TryNumber(string? value, out decimal number) =>
            decimal.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number)
            && number == decimal.Truncate(number) && Math.Abs(number) <= ColumnLimit && (number < 0 || number > uint.MaxValue);

        // How the list writes such a number, and the audit log its target_id: its digits alone, whatever scale the decimal
        // carries, so one row always has one ID
        public static string NumberText(decimal number) => number.ToString("0", CultureInfo.InvariantCulture);

        // Whether every text an edit writes passes the checks, or is the entry's own, unchanged, and its end too: inside the
        // date box's years, or the entry's own. TryFields lets a text or a day it would refuse through only when it's the
        // stored one, as the handler read the entry before the transaction. The edit handler also passes EditAsync a text it
        // refused, in a form from before the entry changed or one whose entry is gone by the time its page would be shown.
        public static bool Writable(BlacklistModel entry, BlacklistModel edited) =>
            Writable(entry.username, edited.username, BlacklistData.UsernameLimit)
            && Writable(entry.reason, edited.reason, BlacklistData.ReasonLimit)
            && Writable(entry.notes, edited.notes, BlacklistData.NotesLimit)
            && (edited.expire_date is not { } end || end == entry.expire_date || end.Year is >= FirstYear and <= LastYear);

        static bool Writable(string? stored, string? written, int limit) =>
            string.Equals(stored, written, StringComparison.Ordinal) || CheckText(written, limit, out _) == TextProblem.None;
    }
}
