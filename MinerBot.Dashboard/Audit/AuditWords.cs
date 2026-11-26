using DataAccessLibrary.Audit;
using MinerBot.Shared.Requests;

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

        // A session the Owner ended on the Sessions page, and every other session at once
        public const string Ended = "auth.ended";
        public const string EndedOthers = "auth.ended_others";

        public const string ArmorApprove = "armor.approve";
        public const string ArmorReject = "armor.reject";
        public const string ArmorTakeDown = "armor.takedown";
        public const string ArmorPending = "armor.pending";

        public const string BlacklistAdd = "blacklist.add";
        public const string BlacklistEdit = "blacklist.edit";
        public const string BlacklistRemove = "blacklist.remove";

        // The Owner's save of a server's switches, with the server's ID in guild_id, so it shows under the server's changes
        public const string SwitchesChange = "switches.change";

        // What a row is about, its target_type
        public const string SessionTarget = "session";
        public const string ArmorTarget = "armor";
        public const string BlacklistTarget = "blacklist";

        // A request to the bot, by its ID in bot_commands. Each kind of request is an action of its own, with the kind's
        // own word from MinerBot.Shared's request list, so the filter can tell a restart from a cache clear.
        public const string RequestTarget = "request";

        public static readonly IReadOnlyList<AuditWord> Actions =
        [
            new(SignIn, "Signed in"),
            new(SignOut, "Signed out"),
            new(Refused, "Refused or ended a session"),
            new(Ended, "Ended a session"),
            new(EndedOthers, "Ended every other session"),
            new(ArmorApprove, "Approved armor"),
            new(ArmorReject, "Rejected armor"),
            new(ArmorTakeDown, "Took armor down"),
            new(ArmorPending, "Moved armor back to pending"),
            new(BlacklistAdd, "Added to the blacklist"),
            new(BlacklistEdit, "Changed a blacklist entry"),
            new(BlacklistRemove, "Removed from the blacklist"),
            new(SwitchesChange, "Changed a server's switches"),
            .. RequestKinds.All.Select(kind => new AuditWord(kind.Word, RequestLabel(kind)))
        ];

        public static readonly IReadOnlyList<AuditWord> Targets =
        [
            new(ArmorTarget, "Armor"),
            new(BlacklistTarget, "Blacklist entry"),
            new(SessionTarget, "Session"),
            new(RequestTarget, "Bot request")
        ];

        // What the audit log calls a request: who asked the bot for what
        static string RequestLabel(RequestKind kind) => kind.Word switch
        {
            RequestKinds.CachesClear => "Asked the bot to clear its caches",
            RequestKinds.JobNotifications => "Asked the bot to run the notification job",
            RequestKinds.JobColors => "Asked the bot to run the colour job",
            RequestKinds.SteamRenew => "Asked the bot to renew the Steam token",
            RequestKinds.PostsPause => "Asked the bot to pause posts",
            RequestKinds.PostsResume => "Asked the bot to resume posts",
            RequestKinds.StatusSet => "Asked the bot to set its status text",
            RequestKinds.SteamReconnect => "Asked the bot to reconnect Steam",
            RequestKinds.GuildTestMessage => "Asked the bot to send a test message",
            RequestKinds.GuildRefresh => "Asked the bot to refresh a server's channels",
            RequestKinds.SteamLookup => "Asked the bot to look up a Steam profile link",
            RequestKinds.GuildLeave => "Asked the bot to leave a server",
            RequestKinds.BotRestart => "Asked the bot to restart",
            RequestKinds.OwnerSigninAlert => "Asked the bot to tell the owner about a sign-in",
            _ => "Asked the bot: " + kind.Label
        };

        // Null for a word that isn't on the list, so a filter never passes on one it doesn't know
        public static AuditWord? Action(string? word) => Actions.FirstOrDefault(action => action.Word == word);

        public static AuditWord? Target(string? word) => Targets.FirstOrDefault(target => target.Word == word);

        // A word the list doesn't have, such as a later version's, is shown as it's stored: AddAudit allows only small
        // letters, dots and underscores in an action, and the page encodes it
        public static string ActionLabel(string action) => Action(action)?.Label ?? action;

        public static string TargetLabel(string? target) => Target(target)?.Label ?? target ?? "";

        // A request's row records the asking, not what the bot then did, which its page shows: it was sent, or the commit
        // wasn't confirmed, so it may not have been
        public static string OutcomeLabel(string outcome, string? target = null) => outcome switch
        {
            AuditOutcome.Ok when target == RequestTarget => "Sent to the bot",
            AuditOutcome.Unknown when target == RequestTarget => "Not sure it was sent",
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
