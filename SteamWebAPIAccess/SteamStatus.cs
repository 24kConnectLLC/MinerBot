using SteamKit2;

namespace SteamWebAPIAccess
{
    public enum SteamConnection
    {
        // From the start until the first logon, and after any disconnect
        Offline,

        LoggedOn,

        // From a missing, expired or rejected token until the next good logon, which needs someone to sign in
        NeedsSignIn
    }

    // What the bot's status shows about Steam, without a secret: no account name, SteamID or token. A new one replaces the
    // old one whole at each change, so it can be read from any thread. Since is UTC.
    public sealed record SteamStatus(SteamConnection State, DateTimeOffset Since, EResult? LastLogOnResult = null, DateOnly? TokenExpires = null);

    // The kinds of the Steam events SteamAPIAccess reports through RecordEvent. They're in the bot's list of every event
    // kind too, which its tests check.
    public static class SteamEvents
    {
        // Logged on, with how long the bot was offline
        public const string LoggedOn = "steam.logged_on";

        // A logged-on connection ended
        public const string Offline = "steam.offline";

        // Steam logged the bot off, with its reason
        public const string LoggedOff = "steam.logged_off";

        // A logon Steam refused, once for each answer in one spell offline
        public const string LogOnFailed = "steam.logon_failed";

        public const string NeedsSignIn = "steam.needs_sign_in";

        // A new sign-in's token, and one Steam issued in place of the old one, each with its expiry date
        public const string TokenSaved = "steam.token_saved";
        public const string TokenRenewed = "steam.token_renewed";

        public static IReadOnlyList<string> All { get; } = [LoggedOn, Offline, LoggedOff, LogOnFailed, NeedsSignIn, TokenSaved, TokenRenewed];
    }
}
