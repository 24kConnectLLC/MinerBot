using System.Globalization;
using System.Text.RegularExpressions;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using MinerBot.Dashboard.Status;

namespace MinerBot.Dashboard.Servers
{
    // How old the bot's copy of its servers can be: current, or as of a time, with the reason in words, and the page's time.
    // AsOf is null when there's no telling.
    public sealed record CopyFreshness(bool Current, DateTime? AsOf, string Reason, DateTime Now);

    // The bot writes a server only when it changes, and goes over every one every 15 minutes, so a row's updated_at is when
    // it was last written, not when it was last checked. The copy is current while the bot runs and is connected to
    // Discord, apart from the 5 minutes after MinerBot's database was unreachable, while the bot's writer backs off: then
    // it can still date from the outage's start. Otherwise it's as old as the first drop from Discord since the last
    // connection, the end of the run that made that connection if it has ended, or the bot's last check-in, whichever
    // came first.
    internal static partial class CopyFreshnesses
    {
        // The longest the bot's server writer waits before it tries again after MinerBot's database failed
        public static readonly TimeSpan CatchUp = TimeSpan.FromMinutes(5);

        // What the reads behind this need, in order: the latest outage only while the bot runs and is connected, and
        // otherwise the first drop, clean stop and start since the last connection, and the first unclean end since that start
        public static bool Connected(BotStatusRow? status, BotState state) => status != null && state == BotState.Online && status.discord_state == DiscordState.Connected;

        public static CopyFreshness Of(BotStatusRow? status, BotState state, DateTime now, BotEventRow? lastOutage, BotEventRow? firstDrop, DateTime? runEnded = null)
        {
            if (status == null)
                return new CopyFreshness(false, null, "MinerBot hasn't recorded its status, so there's no telling how current this copy is.", now);

            if (Connected(status, state))
            {
                if (lastOutage == null || now - lastOutage.at >= CatchUp)
                    return new CopyFreshness(true, null, "MinerBot is running and connected to Discord, so this copy is current.", now);

                var started = OutageStart(lastOutage.detail);

                return new CopyFreshness(false, started,
                    $"MinerBot's database was unreachable until {TimeWords.Utc(lastOutage.at)}, and the bot can take up to 5 minutes after that to catch up on its servers" +
                    (started != null ? ", so this copy can still date from the outage's start." : "."), now);
            }

            // The bot's last check-in, unless it's running and only Discord is down
            DateTime? lastSeen = state == BotState.Online ? null : status.stopped_at ?? status.heartbeat_at;

            // The first drop since the last connection, or the end of the run that made it, if that came first: a run that
            // stops or crashes while connected records no drop, and a later run that never connects writes nothing to the
            // copy, though its failed attempts are drops. Otherwise when the row says Discord went, when neither was recorded.
            DateTime? dropped = Earliest(firstDrop?.at, runEnded) ?? (status.discord_state == DiscordState.Connected ? null : status.discord_since);

            var asOf = dropped is { } drop && (lastSeen is not { } seen || drop < seen) ? dropped : lastSeen;

            var reason = state switch
            {
                BotState.Stopped => "MinerBot is stopped",
                BotState.Online => "MinerBot isn't connected to Discord",
                _ => "MinerBot isn't checking in"
            };

            return new CopyFreshness(false, asOf, asOf == null
                ? reason + ", so there's no telling how current this copy is."
                : reason + ", so this copy is as of when it last heard from Discord.", now);
        }

        // A server Discord reports unavailable keeps its channels as they were before the outage, which nothing dates, so its
        // own page can't call them current, whatever the rest of the copy is
        public static CopyFreshness ForServer(CopyFreshness copy, bool inOutage) => inOutage && copy.Current
            ? copy with { Current = false, Reason = "Discord reports this server unavailable, so MinerBot's copy of its channels is as it was before the outage. The rest of the copy is current." }
            : copy;

        // When the run that made the last connection to Discord was last known to be running, once it has ended: the first
        // clean stop and the first start of a run since that connection, and the last heartbeat that the next run's unclean
        // end names, whichever came first. That heartbeat is the last time the run was seen, not its end, which came after
        // it. Null when none came, as while that run goes on.
        public static DateTime? RunEnded(BotEventRow? stop, BotEventRow? start, BotEventRow? uncleanEnd) =>
            Earliest(Earliest(stop?.at, start?.at), LastHeartbeat(uncleanEnd?.detail));

        static DateTime? Earliest(DateTime? first, DateTime? second) => first is { } a && second is { } b ? (a <= b ? a : b) : first ?? second;

        // The last heartbeat of a run that never stopped cleanly, from the next run's detail: the run that started at
        // yyyy-MM-dd HH:mm:ss UTC never stopped cleanly; its last heartbeat was at yyyy-MM-dd HH:mm:ss UTC
        internal static DateTime? LastHeartbeat(string? detail)
        {
            if (detail == null || UncleanEnd().Match(detail) is not { Success: true } match)
                return null;

            return DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var heartbeat) ? heartbeat : null;
        }

        // When an outage began, from the bot's detail: from yyyy-MM-dd HH:mm:ss to yyyy-MM-dd HH:mm:ss UTC
        internal static DateTime? OutageStart(string? detail)
        {
            if (detail == null || Outage().Match(detail) is not { Success: true } match)
                return null;

            return DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var start) ? start : null;
        }

        [GeneratedRegex(@"^from ([0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}) to [0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2} UTC\z", RegexOptions.CultureInvariant)]
        private static partial Regex Outage();

        [GeneratedRegex(@"^the run that started at [0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2} UTC never stopped cleanly; its last heartbeat was at ([0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}) UTC\z", RegexOptions.CultureInvariant)]
        private static partial Regex UncleanEnd();
    }
}
