using DataAccessLibrary.Audit;

namespace MinerBot.Dashboard.Audit
{
    // A word the audit log holds, with what the pages call it
    public sealed record AuditWord(string Word, string Label);

    // Every action word the dashboard writes, each with its label, as one list, so the audit page's filter offers every
    // one of them: AuditWordsTests fails for a word the code writes that isn't here. The words are fixed, because the
    // filter and the rows already written use them. Also the labels of the outcomes, the roles and what a row is about.
    internal static class AuditWords
    {
        public const string SignIn = "auth.signin";
        public const string SignOut = "auth.signout";

        // A sign-in that was refused, and the sessions ended for someone taken off the lists
        public const string Refused = "auth.refused";

        public const string ArmorApprove = "armor.approve";
        public const string ArmorReject = "armor.reject";
        public const string ArmorTakeDown = "armor.takedown";
        public const string ArmorPending = "armor.pending";

        public const string BlacklistAdd = "blacklist.add";
        public const string BlacklistEdit = "blacklist.edit";
        public const string BlacklistRemove = "blacklist.remove";

        // What a row is about, its target_type
        public const string SessionTarget = "session";
        public const string ArmorTarget = "armor";
        public const string BlacklistTarget = "blacklist";

        public static readonly IReadOnlyList<AuditWord> Actions =
        [
            new(SignIn, "Signed in"),
            new(SignOut, "Signed out"),
            new(Refused, "Refused or ended a session"),
            new(ArmorApprove, "Approved armor"),
            new(ArmorReject, "Rejected armor"),
            new(ArmorTakeDown, "Took armor down"),
            new(ArmorPending, "Moved armor back to pending"),
            new(BlacklistAdd, "Added to the blacklist"),
            new(BlacklistEdit, "Changed a blacklist entry"),
            new(BlacklistRemove, "Removed from the blacklist")
        ];

        public static readonly IReadOnlyList<AuditWord> Targets =
        [
            new(ArmorTarget, "Armor"),
            new(BlacklistTarget, "Blacklist entry"),
            new(SessionTarget, "Session")
        ];

        // Null for a word that isn't on the list, so a filter never passes on one it doesn't know
        public static AuditWord? Action(string? word) => Actions.FirstOrDefault(action => action.Word == word);

        public static AuditWord? Target(string? word) => Targets.FirstOrDefault(target => target.Word == word);

        // A word the list doesn't have, such as a later version's, is shown as it's stored: AddAudit allows only small
        // letters, dots and underscores in an action, and the page encodes it
        public static string ActionLabel(string action) => Action(action)?.Label ?? action;

        public static string TargetLabel(string? target) => Target(target)?.Label ?? target ?? "";

        public static string OutcomeLabel(string outcome) => outcome switch
        {
            AuditOutcome.Ok => "Done",
            AuditOutcome.Refused => "Refused",
            AuditOutcome.Failed => "Failed",
            AuditOutcome.Unknown => "Not sure it was saved",
            _ => outcome
        };

        public static string RoleLabel(string role) => role switch
        {
            AuditRole.Owner => "Owner",
            AuditRole.Admin => "Admin",
            AuditRole.ServerAdmin => "Server admin",
            AuditRole.SlashCommand => "Slash command",
            AuditRole.Bot => "The bot",
            AuditRole.System => "The system",
            AuditRole.Visitor => "On no list",
            _ => role
        };
    }
}
