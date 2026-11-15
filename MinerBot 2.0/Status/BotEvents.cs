using DataAccessLibrary.Status;
using SteamWebAPIAccess;

namespace MinerBot_2._0.Status
{
    // Every kind of event the bot records, each with how much it matters, as one list. BotRecorderTests fails for a kind that
    // doesn't fit the table (EveryKindFitsTheTableAndHasALevel) or that the code records and isn't here
    // (EveryKindTheCodeRecordsIsOnTheList). A detail is the bot's own wording, made from types, codes, Steam's answers, job
    // names, counts, durations and dates: never a library's message, a setting's value, a name, a token, a SteamID or a
    // connection string.
    public static class BotEvents
    {
        // With the version, or that there's none
        public const string BotStarted = "bot.started";

        // The host started stopping, on a signal or a failure
        public const string BotStopping = "bot.stopping";

        // The run before ended without a clean stop, such as a crash, a kill or a reboot, with its last heartbeat
        public const string UncleanEnd = "bot.unclean_end";

        // MinerBot's database didn't take the status and history from when to when, and what the limits dropped meanwhile
        public const string DatabaseUnreachable = "database.unreachable";
        public const string RecordsDropped = "records.dropped";

        // With how long the bot was down and since when, or that it's the run's first connection
        public const string DiscordConnected = "discord.connected";

        // With the exception's type, and the close code when Discord closed the connection
        public const string DiscordDisconnected = "discord.disconnected";

        public const string JobFailed = "job.failed";
        public const string JobCutOff = "job.cut_off";

        // A direct message to the owner, with its text, which is the bot's own
        public const string AlertSent = "alert.sent";
        public const string AlertFailed = "alert.failed";

        static readonly Dictionary<string, string> Levels = new()
        {
            [BotStarted] = EventLevel.Info,
            [BotStopping] = EventLevel.Info,
            [UncleanEnd] = EventLevel.Warning,
            [DatabaseUnreachable] = EventLevel.Warning,
            [RecordsDropped] = EventLevel.Warning,
            [DiscordConnected] = EventLevel.Info,
            [DiscordDisconnected] = EventLevel.Warning,
            [JobFailed] = EventLevel.Error,
            [JobCutOff] = EventLevel.Warning,
            [AlertSent] = EventLevel.Info,
            [AlertFailed] = EventLevel.Error,
            [SteamEvents.LoggedOn] = EventLevel.Info,
            [SteamEvents.Offline] = EventLevel.Warning,
            [SteamEvents.LoggedOff] = EventLevel.Warning,
            [SteamEvents.LogOnFailed] = EventLevel.Warning,
            [SteamEvents.NeedsSignIn] = EventLevel.Error,
            [SteamEvents.TokenSaved] = EventLevel.Info,
            [SteamEvents.TokenRenewed] = EventLevel.Info
        };

        public static IReadOnlyCollection<string> All => Levels.Keys;

        // Null for a kind that isn't on the list, which the recorder drops
        public static string? LevelOf(string? kind) => kind != null && Levels.TryGetValue(kind, out var level) ? level : null;
    }
}
