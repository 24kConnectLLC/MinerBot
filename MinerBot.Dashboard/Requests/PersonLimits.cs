using DataAccessLibrary.Requests.Models;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Requests
{
    // A person's count against the dashboard's own limit: whether it's reached, for the kind's share or for the hourly
    // total, how many they asked for, the limit, and when they may ask again
    public sealed record PersonLimitCheck(bool Reached, bool ForKind, int Count, int Limit, DateTime? After);

    // The dashboard's limits on each person, so one person, or a stolen session, can't spend what everyone shares: the bot's
    // limits are for each kind, for everyone. The kinds an Admin can ask for have an hourly share of the bot's budget, and
    // every kind but the sign-in alert counts toward 60 an hour in all, which also bounds what an Owner's session can queue,
    // the requests the bot refuses without counting them included. Counted from the person's own rows in bot_commands, in
    // any state, since each was sent, so a restart of the dashboard resets nothing and request.sh's rows count too. Only
    // requests to the bot: armor and blacklist changes aren't requests.
    internal static class PersonLimits
    {
        public static readonly TimeSpan Window = TimeSpan.FromHours(1);

        public const int Total = 60;

        // Each Admin kind's share of what the bot takes for everyone: 30 cache clears an hour, the notification job once a
        // minute, 3 test messages a server every 10 minutes, a refresh a minute for each server, and 60 lookups an hour
        static readonly Dictionary<string, int> Shares = new(StringComparer.Ordinal)
        {
            [RequestKinds.CachesClear] = 20,
            [RequestKinds.JobNotifications] = 10,
            [RequestKinds.GuildTestMessage] = 10,
            [RequestKinds.GuildRefresh] = 20,
            [RequestKinds.SteamLookup] = 20
        };

        // The kind's own share, or null for one with only the total: the Owner's kinds, which the bot's own limits bound
        public static int? Share(RequestKind kind) => Shares.TryGetValue(kind.Word, out var share) ? share : null;

        // Whether one more request of the kind would go over the person's share of it, or their total, from the rows they
        // asked for in the last hour. Its time is the row's that leaves the hour soonest after which they're back under. A
        // row whose time MySql.Data can't read compares in MariaDB as a time after the hour's start, so it counts, but never
        // leaves.
        public static PersonLimitCheck Check(RequestKind kind, IReadOnlyList<AskedRow> asked, DateTime now)
        {
            ArgumentNullException.ThrowIfNull(kind);

            var since = now - Window;
            var counted = asked.Where(row => row.kind != RequestKinds.OwnerSigninAlert && (row.created_at == null || row.created_at >= since)).ToList();

            if (Share(kind) is { } share)
            {
                var ofKind = counted.Where(row => row.kind == kind.Word).ToList();

                if (ofKind.Count >= share)
                    return new PersonLimitCheck(true, true, ofKind.Count, share, Again(ofKind, share));
            }

            if (counted.Count >= Total)
                return new PersonLimitCheck(true, false, counted.Count, Total, Again(counted, Total));

            return new PersonLimitCheck(false, false, counted.Count, Total, null);
        }

        // When enough of the rows have left the hour that one more fits: the one whose leaving brings the count under the
        // limit. Null when that one's time can't be read.
        static DateTime? Again(List<AskedRow> rows, int limit)
        {
            var times = rows.Where(row => row.created_at != null).Select(row => row.created_at!.Value).Order().ToList();
            var leaving = rows.Count - limit;

            return leaving < times.Count ? times[leaving] + Window : null;
        }
    }
}
