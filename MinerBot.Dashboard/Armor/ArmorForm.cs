using DataAccessLibrary.Armor;
using MinerBot.Shared.Armor;

namespace MinerBot.Dashboard.Armor
{
    // The review forms' fields, checked before anything reaches the database. The data layer throws for a status, a reason
    // or an action it can't save, which would be a 500, so a form someone made up gets a 400 here instead.
    internal static class ArmorForm
    {
        // More than any form holds: one tick for each warning, and there are twenty-three at most
        public const int MaxTicks = 32;

        static readonly HashSet<string> Kinds = [.. Enum.GetNames<ArmorProblemKind>()];
        static readonly HashSet<string> Fields = [.. Enum.GetNames<ArmorField>()];

        // A warning's tick, as its checkbox sends it. A kind can be on more than one field, so the pair is the key.
        public static string TickKey(ArmorProblem problem) => $"{problem.Kind}:{problem.Field}";

        // The status the reviewer saw, which the change requires the armor still to have: one of the website's words, as
        // ArmorActions.TryStatusOf reads it, or none for armor with no status, which the form sends as an empty value
        public static bool TryExpected(string? value, out string? expected)
        {
            expected = string.IsNullOrEmpty(value) ? null : value;
            return expected == null || ArmorStatus.IsKnown(expected);
        }

        // Browsers send each line end in a box as two characters, so they're made one again before the reason is counted,
        // the way the box counted it. An empty reason is none. Longer than the review row holds only comes from a form
        // someone made up, since the box stops at the limit.
        public static bool TryReason(string? value, out string? reason)
        {
            reason = value?.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

            if (string.IsNullOrEmpty(reason))
            {
                reason = null;
                return true;
            }

            return reason.EnumerateRunes().Count() <= ArmorReviewData.ReasonLimit;
        }

        // Each tick a kind and a field by their exact names, such as HiddenCharacters:Name
        public static bool TryTicks(string[]? values, out IReadOnlySet<string> ticks)
        {
            values ??= [];
            ticks = values.OfType<string>().ToHashSet(StringComparer.Ordinal);

            return values.Length <= MaxTicks && values.All(value =>
                value?.Split(':') is [var kind, var field] && Kinds.Contains(kind) && Fields.Contains(field));
        }
    }
}
