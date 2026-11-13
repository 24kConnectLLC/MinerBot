using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using MinerBot.Dashboard.Security;

namespace MinerBot.Dashboard.Audit
{
    // A change the database didn't confirm: the work had run and its transaction was committing when the database stopped
    // answering, so it may or may not have landed, and its own audit row with it
    internal static class UnconfirmedChanges
    {
        public const string Detail = "The database didn't confirm the save, so it may or may not have happened";

        // A row that says so, with the change's own action, target and before and after, written on MinerBot's own
        // connection, outside the transaction whose end is unknown. Best effort: the page says the same either way, so a
        // failure here is only logged.
        public static async Task RecordAsync(IAuditLogData audit, AuditEntry change, ILogger logger)
        {
            var unknown = new AuditEntry
            {
                at = change.at,
                actor_id = change.actor_id,
                actor_name = change.actor_name,
                actor_role = change.actor_role,
                action = change.action,
                outcome = AuditOutcome.Unknown,
                guild_id = change.guild_id,
                target_type = change.target_type,
                target_id = change.target_id,
                before_json = change.before_json,
                after_json = change.after_json,
                detail = Detail,
                ip = change.ip,
                request_id = change.request_id
            };

            try
            {
                await DatabaseCalls.Guard(() => audit.AddAudit(unknown), logger);
            }
            catch (DashboardUnavailableException)
            {
                logger.LogWarning("The audit row saying {Action} may not have been saved couldn't be written either (request {RequestId})", change.action, change.request_id);
            }
        }
    }
}
