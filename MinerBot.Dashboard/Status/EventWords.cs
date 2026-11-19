using DataAccessLibrary.Status;

namespace MinerBot.Dashboard.Status
{
    // A kind of the bot's events, with what the pages call it
    public sealed record EventWord(string Kind, string Label);

    // Every kind of event the bot records, each with a label in plain words, as one list, so the events' filter offers each
    // one. The dashboard references nothing of the bot's, so StatusRulesTests reads the bot's own lists, BotEvents and
    // SteamEvents, from its source and fails for a kind that isn't here, and AuditWordsTests lets these words, and only
    // these, stand as bare strings in this file. A kind a later bot records is shown as it's stored.
    internal static class EventWords
    {
        public const string Started = "bot.started";
        public const string Stopping = "bot.stopping";
        public const string UncleanEnd = "bot.unclean_end";
        public const string DatabaseUnreachable = "database.unreachable";
        public const string RecordsDropped = "records.dropped";
        public const string DiscordConnected = "discord.connected";
        public const string DiscordDisconnected = "discord.disconnected";
        public const string JobFailed = "job.failed";
        public const string JobCutOff = "job.cut_off";
        public const string AlertSent = "alert.sent";
        public const string AlertFailed = "alert.failed";
        public const string SteamLoggedOn = "steam.logged_on";
        public const string SteamOffline = "steam.offline";
        public const string SteamLoggedOff = "steam.logged_off";
        public const string SteamLogOnFailed = "steam.logon_failed";
        public const string SteamNeedsSignIn = "steam.needs_sign_in";
        public const string SteamTokenSaved = "steam.token_saved";
        public const string SteamTokenRenewed = "steam.token_renewed";

        public static readonly IReadOnlyList<EventWord> Kinds =
        [
            new(Started, "Started"),
            new(Stopping, "Stopping"),
            new(UncleanEnd, "Ended without a clean stop"),
            new(DatabaseUnreachable, "MinerBot's database was unreachable"),
            new(RecordsDropped, "Records dropped"),
            new(DiscordConnected, "Connected to Discord"),
            new(DiscordDisconnected, "Disconnected from Discord"),
            new(JobFailed, "A job failed"),
            new(JobCutOff, "A job was cut off"),
            new(AlertSent, "Message sent to the owner"),
            new(AlertFailed, "Message to the owner failed"),
            new(SteamLoggedOn, "Logged on to Steam"),
            new(SteamOffline, "Steam went offline"),
            new(SteamLoggedOff, "Steam logged the bot off"),
            new(SteamLogOnFailed, "Steam refused a logon"),
            new(SteamNeedsSignIn, "Steam needs a sign-in"),
            new(SteamTokenSaved, "Steam token saved"),
            new(SteamTokenRenewed, "Steam token renewed")
        ];

        // Discord asking the bot to reconnect, its op 7, which the bot does at once. Shown as routine, never as a warning.
        public const string RoutineLabel = "Discord asked the bot to reconnect";

        // Null for a kind that isn't on the list, so a filter never passes on one it doesn't know
        public static EventWord? Kind(string? kind) => Kinds.FirstOrDefault(word => word.Kind == kind);

        // A routine reconnect in its own words, a kind on the list by its label, and any other as it's stored, which the
        // page encodes
        public static string Label(string kind, string? detail) => RoutineReconnect.Is(kind, detail) ? RoutineLabel : Kind(kind)?.Label ?? kind;

        public static bool IsRoutine(string kind, string? detail) => RoutineReconnect.Is(kind, detail);

        // A routine reconnect is routine whatever level the bot gave it, and every other event keeps its own
        public static string LevelLabel(string level, string kind, string? detail) => RoutineReconnect.Is(kind, detail) ? "Routine" : level switch
        {
            EventLevel.Info => "Info",
            EventLevel.Warning => "Warning",
            EventLevel.Error => "Error",
            _ => level
        };

        public static IReadOnlyList<EventWord> Levels { get; } =
        [
            new(EventLevel.Info, "Info"),
            new(EventLevel.Warning, "Warning"),
            new(EventLevel.Error, "Error")
        ];
    }
}
