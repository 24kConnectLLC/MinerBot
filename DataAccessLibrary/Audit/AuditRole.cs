namespace DataAccessLibrary.Audit
{
    // Who made a change: someone on the dashboard's lists, a server's own admin, a slash command, or the bot itself, and
    // who was refused
    public static class AuditRole
    {
        public const string Owner = "owner";
        public const string Admin = "admin";
        public const string ServerAdmin = "server_admin";
        public const string SlashCommand = "slash_command";
        public const string Bot = "bot";
        public const string System = "system";

        // Someone who signed in with Discord but is on none of the dashboard's lists
        public const string Visitor = "visitor";

        public static IReadOnlyList<string> All { get; } = [Owner, Admin, ServerAdmin, SlashCommand, Bot, System, Visitor];

        public static bool IsKnown(string? role) => role is Owner or Admin or ServerAdmin or SlashCommand or Bot or System or Visitor;
    }
}
