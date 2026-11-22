using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using MinerBot.Dashboard.Security;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Requests
{
    // The reads the sender's limits make, once for a page that says whether each of its controls is available: every
    // request the bot took in the last hour, every one that waits, and the person's own of the last hour. The line each
    // control shows is the answer the sender would give, from the same rules, and the same order of reads: the requests
    // that wait first, so one the bot takes between the two reads is counted once rather than missed.
    internal sealed record LimitReads(IReadOnlyList<ClaimRow> Claims, IReadOnlyList<WaitingRow> Waiting, IReadOnlyList<AskedRow> Asked)
    {
        public static async Task<LimitReads> LoadAsync(IBotRequestReads reads, ulong actorId, DateTime now, ILogger logger)
        {
            var waiting = await DatabaseCalls.Guard(() => reads.ListWaiting(now), logger);
            var claims = await DatabaseCalls.Guard(() => reads.ListClaimsSince(now - BotLimits.Longest), logger);

            return new(claims, waiting, await DatabaseCalls.Guard(() => reads.ListAskedBy(actorId, now - PersonLimits.Window), logger));
        }

        public AvailabilityLine Line(RequestKind kind, ulong? guildId, DateTime now, string? botRefuses = null) =>
            ControlWords.Availability(PersonLimits.Check(kind, Asked, now), BotLimits.Check(kind, guildId, Claims, Waiting, now), botRefuses);
    }
}
