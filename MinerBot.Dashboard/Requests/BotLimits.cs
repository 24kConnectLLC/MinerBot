using DataAccessLibrary.Requests.Models;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Requests
{
    public enum BotLimitState
    {
        Available,

        // The bot has taken as many as its limit allows in the kind's window
        Limit,

        // It hasn't, but will have once it takes the requests that wait
        Waiting
    }

    // What the bot's limit says of one more request of a kind: available; at the limit, with how many the bot took in the
    // window, the oldest of them, and when another fits, as the bot words it; or full once the waiting requests are taken,
    // naming the oldest of those
    public sealed record BotLimitCheck(BotLimitState State, int Count = 0, DateTime? Oldest = null, DateTime? After = null, WaitingRow? Waiting = null);

    // The bot's limit, checked before the dashboard writes a request the bot would refuse for it, from two reads made once for
    // each page or form: every request that waits, then every request taken in the last hour, the longest window on the
    // list, so a request the bot takes between the two reads is in both rather than neither, and counted once, as taken,
    // by its ID. The bot counts the requests
    // any run took, in any state, of the kinds counted together, in the kind's window, and of the server for a kind counted
    // by server (BotRequestData.CountClaims, applied by the bot's RequestWorker), so refused and expired ones, which were
    // never taken, don't count. Both apps read the host's clock and the bot counts a moment later, so this is never more
    // lenient than the bot, apart from a request written after the waiting ones were read; the bot then refuses, in its own
    // words. A waiting request isn't counted by the bot yet, but will be once it's taken, so it counts here too.
    internal static class BotLimits
    {
        // How far back the claims read goes, which no kind's window may be longer than
        public static readonly TimeSpan Longest = TimeSpan.FromHours(1);

        // A row whose time MySql.Data can't read compares in MariaDB as a time after the window's start, so it's counted,
        // as the bot would count it, but it can't be the oldest or say when another fits
        public static BotLimitCheck Check(RequestKind kind, ulong? guildId, IReadOnlyList<ClaimRow> claims, IReadOnlyList<WaitingRow> waiting, DateTime now)
        {
            ArgumentNullException.ThrowIfNull(kind);

            var since = now - kind.Window;

            bool Counted(string word, ulong? guild) => kind.CountedWith.Contains(word) && (!kind.PerServer || guild == guildId);

            var taken = claims.Where(claim => Counted(claim.kind, claim.guild_id) && (claim.claimed_at == null || claim.claimed_at >= since)).ToList();
            var oldest = taken.Min(claim => claim.claimed_at);

            if (taken.Count >= kind.Limit)
                return new BotLimitCheck(BotLimitState.Limit, taken.Count, oldest, oldest + kind.Window);

            // A waiting row the claims hold too was taken between the two reads, and counts as taken alone
            var claimed = claims.Select(claim => claim.id).ToHashSet();
            var queued = waiting.Where(row => Counted(row.kind, row.guild_id) && !claimed.Contains(row.id)).OrderBy(row => row.id).ToList();

            if (taken.Count + queued.Count >= kind.Limit)
                return new BotLimitCheck(BotLimitState.Waiting, taken.Count + queued.Count, oldest, queued[0].expires_at, queued[0]);

            return new BotLimitCheck(BotLimitState.Available, taken.Count);
        }
    }
}
