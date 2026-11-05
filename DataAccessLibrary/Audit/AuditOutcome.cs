namespace DataAccessLibrary.Audit
{
    // How a change went. Unknown is a write that timed out, which may still land.
    public static class AuditOutcome
    {
        public const string Ok = "ok";
        public const string Failed = "failed";
        public const string Refused = "refused";
        public const string Unknown = "unknown";

        public static IReadOnlyList<string> All { get; } = [Ok, Failed, Refused, Unknown];

        public static bool IsKnown(string? outcome) => outcome is Ok or Failed or Refused or Unknown;
    }
}
