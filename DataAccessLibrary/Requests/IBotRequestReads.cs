using DataAccessLibrary.Requests.Models;

namespace DataAccessLibrary.Requests
{
    // The dashboard's reads of its requests to the bot for its pages: one request, the latest of each kind, the bot's last
    // direct message to the owner, the sign-in alerts, and what the dashboard counts before it sends one. Reads only, apart
    // from IBotRequestQueue, so that interface's test doubles stay as they are. None returns a request's key or signature,
    // which no page needs, and none can be stopped by a time MySql.Data can't read. The bot never calls it.
    public interface IBotRequestReads
    {
        Task<long> CountAlertsSince(DateTime since);
        Task<RequestPageRow?> GetRequestView(ulong id);
        Task<RequestPageRow?> LatestDm();
        Task<RequestPageRow?> LatestRefusedAlert();
        Task<IReadOnlyList<AskedRow>> ListAskedBy(ulong actorId, DateTime since);
        Task<IReadOnlyList<ClaimRow>> ListClaimsSince(DateTime since);
        Task<IReadOnlyList<RequestPageRow>> ListLatest(IReadOnlyCollection<string> kinds, ulong? guildId = null);
        Task<IReadOnlyList<WaitingRow>> ListWaiting(DateTime now);
    }
}
