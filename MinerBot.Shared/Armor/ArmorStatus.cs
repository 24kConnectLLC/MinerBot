namespace MinerBot.Shared.Armor
{
    // The words in the Status column of the website's armor table. The website saves every submission as Pending,
    // and the bot only shows Approved ones.
    public static class ArmorStatus
    {
        public const string Pending = "Pending Approval";
        public const string Approved = "Approved";
        public const string NotApproved = "Not Approved";

        public static IReadOnlyList<string> All { get; } = [Pending, Approved, NotApproved];

        // Exact, so nothing saves a fourth spelling of the same word
        public static bool IsKnown(string? status) => status is Pending or Approved or NotApproved;
    }
}
