using System.Globalization;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using MinerBot.Dashboard.Status;
using MinerBot.Shared.Delivery;

namespace MinerBot.Dashboard.Servers
{
    // The pages' words for the bot's record of its posts, which only the bot writes: each server's last post and failure of
    // each kind, its latest messages, and the Overview's banner for posts that keep failing. A message's link is built from
    // the three numbers, never from text. A reason is DeliveryReasons' plain words with Discord's numbers after them, never
    // Discord's own text, which the bot never keeps.
    internal static class DeliveryWords
    {
        // Posts of a kind that failed this many tries in a row, and still fail, keep failing: Has problems counts them, and so
        // does the Overview's banner, for a kind that's on in a server the bot is in
        public const uint FailingTries = 3;

        // How many of a server's latest messages its page lists
        public const int RecentShown = 25;

        public static bool KeepsFailing(DeliveryStatusRow status) => status.failing_since != null && status.failures >= FailingTries;

        // A kind's record as the pages draw it beside its mark. A kind that's off isn't tried, so it isn't failing, though its
        // record says so until the bot's next run that isn't paused clears it, as Has problems already reads it: only its last
        // post is drawn then, never its failing or its next try.
        public static DeliveryStatusRow? Shown(DeliveryStatusRow? status, SettingState state) => status is { failing_since: not null } && !SettingStatuses.IsOn(state)
            ? status with { failing_since = null, failures = 0, next_try_at = null, waiting_on = null }
            : status;

        public static string MessageLink(ulong guildId, ulong channelId, ulong messageId) =>
            $"https://discord.com/channels/{Number(guildId)}/{Number(channelId)}/{Number(messageId)}";

        // A status's last post, when the bot recorded its message
        public static string? LastPostLink(DeliveryStatusRow status) =>
            status is { last_sent_channel_id: { } channel, last_message_id: { } message } ? MessageLink(status.guild_id, channel, message) : null;

        public const string OpenIt = "Open it in Discord";

        // The servers list, under a kind's mark: the last post, or since when its posts fail
        public static string? Line(DeliveryStatusRow? status, DateTime now) => status switch
        {
            { failing_since: { } since } => "Failing since " + TimeWords.Day(since),
            { last_sent_at: { } sent } => "Last post " + TimeWords.Ago(sent, now),
            _ => null
        };

        // A server's page, the Last post column of each kind
        public const string NotRecorded = "Not recorded";
        public const string NoPostYet = "No post recorded yet";

        public static string Posted(DateTime at) => $"Posted {Minute(at)} UTC";

        public static string CouldntPost(DeliveryStatusRow status) =>
            $"Couldn't post since {Minute(status.failing_since!.Value)} UTC: {Reason(status.last_reason, status.last_missing, status.last_http_status, status.last_discord_code)} "
            + $"({Tries(status.failures)} in a row).";

        // While it waits after a failure, which a change of the channel or of the bot's permissions there ends early
        public static string? NextTry(DeliveryStatusRow status, DateTime now) => status.next_try_at is { } next && next > now
            ? $"MinerBot tries again at {next.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC, or as soon as the channel or its permissions change."
            : null;

        // A server's latest messages and refusals
        public const string RecentIntro = "The last 25 posts MinerBot made here, or couldn't make, newest first. MinerBot keeps them for 90 days. Times are UTC.";
        public const string RecentNone = "No post recorded here yet.";

        // What a message carried, such as 3 armor styles
        public static string Items(string source, int count) => source switch
        {
            PostSource.Armor => Count(count, "armor style", "armor styles"),
            PostSource.Maps => Count(count, "map", "maps"),
            PostSource.Lobbies => Count(count, "lobby", "lobbies"),
            _ => Count(count, "item", "items")
        };

        // What became of a message, by its state, with its link only once it's sent
        public static string Outcome(DeliveryRow row) => row.state switch
        {
            DeliveryState.Sent => "Posted",
            DeliveryState.Unconfirmed => "May not have arrived: MinerBot didn't learn whether it went and couldn't read the channel to check, so it counts it as posted and never sends it again",
            DeliveryState.InDoubt => "Being checked: MinerBot didn't learn whether it went, so it looks for it in the channel at its next run",
            DeliveryState.Sending => "Being sent",
            DeliveryState.Failed => "Couldn't post: " + Reason(row.reason, row.missing, row.http_status, row.discord_code),
            _ => "In a state this version of the dashboard doesn't know"
        };

        public static string? Link(DeliveryRow row) =>
            row is { state: DeliveryState.Sent, message_id: { } message } ? MessageLink(row.guild_id, row.channel_id, message) : null;

        // The Overview's banner, for the servers the bot is in with a kind that's on and keeps failing
        public static string Banner(int servers) =>
            $"{(servers == 1 ? "1 server's" : servers.ToString("N0", CultureInfo.InvariantCulture) + " servers'")} posts keep failing: MinerBot couldn't post there in {FailingTries} or more tries in a row.";

        public static string Reason(string? reason, byte? missing, ushort? httpStatus, uint? discordCode) =>
            DeliveryReasons.Describe(reason, missing, httpStatus, discordCode);

        static string Tries(uint count) => count == 1 ? "1 try" : count.ToString("N0", CultureInfo.InvariantCulture) + " tries";

        static string Count(int count, string one, string many) => count == 1 ? "1 " + one : count.ToString("N0", CultureInfo.InvariantCulture) + " " + many;

        static string Minute(DateTime time) => time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        static string Number(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
