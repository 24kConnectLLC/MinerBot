using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;

namespace MinerBot.Dashboard.Status
{
    // Whether the bot is up, from its status row, which it writes every 30 seconds while it runs
    public enum BotState
    {
        Online,
        Late,
        Offline,
        Stopped
    }

    // The rules the status box and the Overview's banners read the status row by, each taking now from the page's clock
    internal static class BotStates
    {
        // Three writes missed is late, and 20 is offline
        public static readonly TimeSpan OnlineWithin = TimeSpan.FromSeconds(90);
        public static readonly TimeSpan LateWithin = TimeSpan.FromMinutes(10);

        // How long the bot may stay stopped, or off Discord while it runs, before the Overview says so: a deploy's swap
        // stops it for seconds, and Discord drops it now and then
        public static readonly TimeSpan BannerAfter = TimeSpan.FromMinutes(5);

        // The Steam token renews every day at noon Eastern, so it should always end further off than this
        public const int TokenWarningDays = 30;

        // Stopped when the run's clean stop is its last write, whatever its age. Otherwise by the heartbeat's age, and
        // offline when there's no row at all.
        public static BotState Of(BotStatusRow? status, DateTime now)
        {
            if (status == null)
                return BotState.Offline;

            if (status.stopped_at is { } stopped && stopped >= status.heartbeat_at)
                return BotState.Stopped;

            var age = now - status.heartbeat_at;

            if (age <= OnlineWithin)
                return BotState.Online;

            return age <= LateWithin ? BotState.Late : BotState.Offline;
        }

        public static string Label(BotState state) => state switch
        {
            BotState.Online => "Online",
            BotState.Late => "Late",
            BotState.Stopped => "Stopped",
            _ => "Offline"
        };

        // What the state means, after its label
        public static string Meaning(BotState state, BotStatusRow? status) => state switch
        {
            _ when status == null => "MinerBot hasn't checked in yet: there's no status row.",
            BotState.Online => "MinerBot checks in every 30 seconds, and it has.",
            BotState.Late => "MinerBot checks in every 30 seconds, and it has missed a few. It may be busy, restarting, or unable to reach its database.",
            BotState.Stopped => "MinerBot stopped cleanly, as a deploy's swap stops it.",
            _ => "MinerBot hasn't checked in for more than 10 minutes."
        };

        public static string DiscordLabel(string state) => state switch
        {
            DiscordState.Connected => "Connected",
            DiscordState.Connecting => "Connecting",
            DiscordState.Disconnected => "Disconnected",
            _ => state
        };

        public static string SteamLabel(string state) => state switch
        {
            SteamState.LoggedOn => "Logged on",
            SteamState.Offline => "Offline",
            SteamState.NeedsSignIn => "Needs a sign-in",
            _ => state
        };

        // Days from today to the token's end, by UTC days, 0 on its last day and below 0 once it has passed. Null when no end
        // is recorded, such as once Steam has rejected the token.
        public static int? TokenDaysLeft(BotStatusRow? status, DateTime now) =>
            status?.steam_token_expires is { } end ? (int)(end.Date - now.Date).TotalDays : null;

        public static bool TokenEndsSoon(BotStatusRow? status, DateTime now) => TokenDaysLeft(status, now) is <= TokenWarningDays;

        // Stopped for longer than a swap takes
        public static bool StoppedTooLong(BotStatusRow? status, BotState state, DateTime now) =>
            state == BotState.Stopped && status?.stopped_at is { } stopped && now - stopped > BannerAfter;

        // Off Discord for longer than a reconnect takes, while the bot itself runs
        public static bool DiscordDownTooLong(BotStatusRow? status, BotState state, DateTime now) =>
            state == BotState.Online && status != null && status.discord_state != DiscordState.Connected
            && now - (status.discord_since ?? status.started_at) > BannerAfter;

        public static bool SteamNeedsSignIn(BotStatusRow? status) => status?.steam_state == SteamState.NeedsSignIn;

        // A link to the commit the bot runs, only for a version in the form the bot writes, a commit's hex digits
        public static string? CommitLink(string? version) =>
            version is { Length: >= 7 and <= 40 } && version.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
                ? "https://github.com/24kConnectLLC/MinerBot/commit/" + version
                : null;

        public static string ShortCommit(string version) => version.Length > 7 ? version[..7] : version;
    }
}
