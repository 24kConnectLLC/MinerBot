namespace DataAccessLibrary.Status
{
    // The words of the bot's status and history tables. The event kinds are the bot's own list, BotEvents, and only their
    // form is checked here: words in small letters, joined by one dot, such as discord.connected.

    // How much an event matters
    public static class EventLevel
    {
        public const string Info = "info";
        public const string Warning = "warning";
        public const string Error = "error";

        public static IReadOnlyList<string> All { get; } = [Info, Warning, Error];

        public static bool IsKnown(string? level) => level is Info or Warning or Error;
    }

    // How a job's run went. Cut off is a run that the time limit stopped waiting for, which may still end later.
    // Skipped is a run that was due while the one that was cut off still ran.
    public static class JobResult
    {
        public const string Ok = "ok";
        public const string Failed = "failed";
        public const string CutOff = "cut_off";
        public const string Skipped = "skipped";

        public static IReadOnlyList<string> All { get; } = [Ok, Failed, CutOff, Skipped];

        public static bool IsKnown(string? result) => result is Ok or Failed or CutOff or Skipped;
    }

    // The bot's connection to Discord's gateway. Connected means the bot is up on it.
    public static class DiscordState
    {
        public const string Connected = "connected";
        public const string Connecting = "connecting";
        public const string Disconnected = "disconnected";

        public static IReadOnlyList<string> All { get; } = [Connected, Connecting, Disconnected];

        public static bool IsKnown(string? state) => state is Connected or Connecting or Disconnected;
    }

    // The bot's logon to Steam. Needs sign-in lasts from a rejected or missing token until the next good logon.
    public static class SteamState
    {
        public const string LoggedOn = "logged_on";
        public const string Offline = "offline";
        public const string NeedsSignIn = "needs_sign_in";

        public static IReadOnlyList<string> All { get; } = [LoggedOn, Offline, NeedsSignIn];

        public static bool IsKnown(string? state) => state is LoggedOn or Offline or NeedsSignIn;
    }
}
