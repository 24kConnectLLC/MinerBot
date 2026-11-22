namespace DataAccessLibrary.Dashboard.Models
{
    // The dashboard's database login as it reads itself: its rights in deploy/rights-list.sql's lines, the lines this
    // version expects with this install's database names, both sorted the same way, and its two limits as its session has
    // them, each null when MariaDB didn't give it as a number
    public sealed record OwnRights(IReadOnlyList<string> Lines, IReadOnlyList<string> Expected, long? Connections, decimal? StatementSeconds)
    {
        // Rights this version uses that the login lacks
        public IReadOnlyList<string> Missing => [.. Expected.Except(Lines, StringComparer.Ordinal)];

        // Rights the login has that this version doesn't use, which aren't wrong in themselves: deploy.sh keeps a previous
        // version's rights for a deploy or two, so a rollback still has them
        public IReadOnlyList<string> Extra => [.. Lines.Except(Expected, StringComparer.Ordinal)];
    }
}
