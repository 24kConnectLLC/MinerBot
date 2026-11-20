using DataAccessLibrary.Requests.Models;

namespace DataAccessLibrary.Requests
{
    // The bot's side of the dashboard's requests: reading them, deciding, taking and finishing them, and its settings that
    // only requests change. The dashboard gets IBotRequestQueue instead, which can't do any of this.
    public interface IBotRequestData
    {
        Task<bool> Claim(ulong id, ulong runId, DateTime now);
        Task<ClaimCountRow> CountClaims(IReadOnlyCollection<string> kinds, DateTime since, ulong? guildId = null);
        Task<bool> Decide(ulong id, ulong runId, string state, string result, DateTime now);
        Task<bool> Finish(ulong id, ulong runId, string state, string result, ulong? number, DateTime now);
        Task<BotRequestRow?> GetRequest(ulong id);
        Task<BotSettingsRow?> GetSettings();
        Task<IReadOnlyList<ulong>> ListOpenIds(ulong runId, int take, bool restartOnly);
        Task<IReadOnlyList<BotRequestRow>> ListUntoldRestarts(ulong runId, DateTime since);
        Task SavePause(bool paused, string? reason, DateTime changedAt, ulong changedBy, ulong requestId, DateTime now);
        Task SaveStatus(string? text, DateTime changedAt, ulong changedBy, ulong requestId, DateTime now);
        Task<bool> SetDm(ulong id, string dmState);
        Task<IReadOnlyList<SweptRequestRow>> SweepOtherRuns(ulong runId, DateTime startedAt, string result, DateTime now);
    }
}
