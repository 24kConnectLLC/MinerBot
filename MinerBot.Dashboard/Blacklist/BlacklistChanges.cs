using DataAccessLibrary;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Blacklist;
using DataAccessLibrary.Blacklist.Models;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Security;

namespace MinerBot.Dashboard.Blacklist
{
    internal enum BlacklistOutcome
    {
        Done,

        // An edit whose values the entry already has, so nothing was written or recorded
        Unchanged,

        // An add for an account the blacklist already has
        Exists,

        // The entry, or the row, isn't as the page showed it
        Changed,

        // The entry, or the row, has no row any more
        Gone,

        // The database stopped answering once the work was done, while the change was being committed, so it may or may
        // not have landed
        Unsure
    }

    // What an editor typed, already checked by BlacklistForm: each text trimmed, or null for none, and the end date at
    // 00:00 of its day
    internal sealed record BlacklistFields(string? Name, string? Reason, DateTime? Expires, string? Notes)
    {
        // The text boxes as the form sent them, which BlacklistForm.TryFields always sets. Null only for fields made
        // without a form, whose texts are then compared as the form makes them.
        public BlacklistBoxes? Boxes { get; init; }

        // The date box held a day the checks refused, so Expires is none only for want of a day, not because the editor
        // cleared the box. The edit handler sends such fields on only for a form from before the entry changed or was
        // removed, or one whose entry is gone by the time its page would be shown, and EditAsync only ever refuses them.
        public bool ExpiresRefused { get; init; }
    }

    // The three text boxes as the form sent them, before trimming. A box the editor left alone and one they took a space
    // off the ends of trim to the same text, so only these tell them apart.
    internal sealed record BlacklistBoxes(string? Name, string? Reason, string? Notes);

    // Each blacklist change is one transaction on the website's connection: the locked read, the check that the entry is
    // as the editor saw it, the write and its audit row commit together, or nothing does. A refusal commits only its audit
    // row, and an edit that changes nothing commits nothing.
    internal sealed class BlacklistChanges(IBlacklistData blacklist, IAuditLogData audit, TimeProvider time, ILogger<BlacklistChanges> logger)
    {
        // Fixed, and never what an editor typed
        const string ExistsDetail = "Already on the blacklist";
        const string ChangedDetail = "The entry had changed since the page showed it";
        const string GoneDetail = "The entry had been removed since the page showed it";
        const string NotAnAccountDetail = "The row's number wasn't a Steam account number";

        public Task<BlacklistOutcome> AddAsync(uint account, BlacklistFields fields, HttpContext httpContext)
        {
            var added = new BlacklistModel { steam_id = account, username = fields.Name, reason = fields.Reason, expire_date = fields.Expires, notes = fields.Notes };
            var after = BlacklistJson.Whole(added);

            return RunAsync(BlacklistAction.Add, SteamAccounts.TargetId(account), httpContext, async (change, transaction) =>
            {
                // For an account with no row this locks the gap the row goes in, so a second add of the same account at
                // the same moment waits for this one, or deadlocks and runs again, and finds this one's row either way
                var existing = await blacklist.GetBannedPlayer(account, transaction);

                if (existing != null)
                    return await change.RefuseAsync(BlacklistOutcome.Exists, ExistsDetail, BlacklistJson.Whole(existing), transaction);

                await blacklist.InsertPlayerBan(added, transaction);
                await change.WriteAsync(null, after, null, transaction);
                return BlacklistOutcome.Done;
            });
        }

        public Task<BlacklistOutcome> EditAsync(uint account, string version, BlacklistFields fields, HttpContext httpContext) =>
            RunAsync(BlacklistAction.Edit, SteamAccounts.TargetId(account), httpContext, async (change, transaction) =>
            {
                // Locked from here to the commit, so nobody can change the entry between the check and the write
                var entry = await blacklist.GetBannedPlayer(account, transaction);

                if (entry == null)
                    return await change.RefuseAsync(BlacklistOutcome.Gone, GoneDetail, null, transaction);

                if (BlacklistVersion.Of(entry) != version)
                    return await change.RefuseAsync(BlacklistOutcome.Changed, ChangedDetail, BlacklistJson.Whole(entry), transaction);

                // A day the checks refused gets this far only when the handler found the entry changed or gone, or found it
                // as the page showed it and then had no entry to show the day's message with. Either way the row is back as
                // the page showed it. The fields hold no end in that day's place, and the editor didn't clear it.
                if (fields.ExpiresRefused)
                    return await change.RefuseAsync(BlacklistOutcome.Changed, ChangedDetail, BlacklistJson.Whole(entry), transaction);

                var edited = Edited(entry, fields);

                // A text the form would refuse, such as one with a tab, gets this far in two ways: let through as the entry's
                // own when the handler read the entry, or refused and sent on by the handler, in a form from before the entry
                // changed or one whose entry was gone by the time its page would be shown. Either way the row is back as the
                // page showed it, so the entry changed in between, and a text that isn't the locked row's own isn't written.
                if (!BlacklistForm.Writable(entry, edited))
                    return await change.RefuseAsync(BlacklistOutcome.Changed, ChangedDetail, BlacklistJson.Whole(entry), transaction);

                var (before, after) = BlacklistJson.Changed(entry, edited);

                // UpdatePlayerBan would say it applied, and there'd be a change on record that changed nothing
                if (before == null)
                    return BlacklistOutcome.Unchanged;

                // The row is locked, so it's there. If it ever isn't, it's a refusal.
                if (!await blacklist.UpdatePlayerBan(edited, transaction))
                    return await change.RefuseAsync(BlacklistOutcome.Gone, GoneDetail, null, transaction);

                await change.WriteAsync(before, after, null, transaction);
                return BlacklistOutcome.Done;
            });

        public Task<BlacklistOutcome> RemoveAsync(uint account, string version, HttpContext httpContext) =>
            RunAsync(BlacklistAction.Remove, SteamAccounts.TargetId(account), httpContext, async (change, transaction) =>
            {
                var entry = await blacklist.GetBannedPlayer(account, transaction);

                if (entry == null)
                    return await change.RefuseAsync(BlacklistOutcome.Gone, GoneDetail, null, transaction);

                if (BlacklistVersion.Of(entry) != version)
                    return await change.RefuseAsync(BlacklistOutcome.Changed, ChangedDetail, BlacklistJson.Whole(entry), transaction);

                if (!await blacklist.RemovePlayerBan(account, transaction))
                    return await change.RefuseAsync(BlacklistOutcome.Gone, GoneDetail, null, transaction);

                await change.WriteAsync(BlacklistJson.Whole(entry), null, null, transaction);
                return BlacklistOutcome.Done;
            });

        // A row whose number no account can be, which no list shows: a removal too, told apart by its target, the number
        // as digits, and its detail
        public Task<BlacklistOutcome> RemoveNumberAsync(decimal number, string version, HttpContext httpContext) =>
            RunAsync(BlacklistAction.Remove, BlacklistForm.NumberText(number), httpContext, async (change, transaction) =>
            {
                var row = await blacklist.GetOutOfRangeBan(number, transaction);

                if (row == null)
                    return await change.RefuseAsync(BlacklistOutcome.Gone, GoneDetail, null, transaction);

                if (BlacklistVersion.Of(row) != version)
                    return await change.RefuseAsync(BlacklistOutcome.Changed, ChangedDetail, BlacklistJson.Whole(row), transaction);

                if (!await blacklist.RemoveOutOfRangeSteamId(number, transaction))
                    return await change.RefuseAsync(BlacklistOutcome.Gone, GoneDetail, null, transaction);

                await change.WriteAsync(BlacklistJson.Whole(row), null, NotAnAccountDetail, transaction);
                return BlacklistOutcome.Done;
            });

        // The entry as an edit leaves it. A text box left as the page filled it keeps the stored value, which the box may
        // have shown otherwise, as BlacklistForm.AsBox says, so a save never rewrites what the editor didn't change. A box
        // the editor changed, if only by a space at either end, saves its trimmed text. Likewise a day the date box sent
        // that is the stored end's own day keeps the stored value, so a time of day written elsewhere isn't lost to a box
        // that only holds days.
        internal static BlacklistModel Edited(BlacklistModel entry, BlacklistFields fields) => new()
        {
            steam_id = entry.steam_id,
            username = Kept(entry.username, fields.Name, fields.Boxes, fields.Boxes?.Name),
            reason = Kept(entry.reason, fields.Reason, fields.Boxes, fields.Boxes?.Reason),
            expire_date = fields.Expires is { } day && !entry.expire_date_unreadable && entry.expire_date is { } stored && stored.Date == day.Date
                ? stored
                : fields.Expires,
            notes = Kept(entry.notes, fields.Notes, fields.Boxes, fields.Boxes?.Notes)
        };

        static string? Kept(string? stored, string? text, BlacklistBoxes? boxes, string? box) =>
            (boxes == null ? BlacklistForm.IsAsStored(stored, text) : BlacklistForm.IsUntouched(stored, box)) ? stored : text;

        // Whether the entry already is what these fields would make it, such as after a try the dashboard couldn't confirm.
        // Never for a refused day, whose place the fields hold no end in.
        internal static bool Has(BlacklistModel entry, BlacklistFields fields) =>
            !fields.ExpiresRefused && BlacklistJson.Changed(entry, Edited(entry, fields)).Before == null;

        async Task<BlacklistOutcome> RunAsync(BlacklistAction action, string target, HttpContext httpContext, Func<Change, DataTransaction, Task<BlacklistOutcome>> work)
        {
            var editor = SignedInUser.From(httpContext.User) ?? throw new InvalidOperationException("Only a signed-in editor can change the blacklist");

            // Everything the audit rows need is worked out before the transaction, because it runs again after a deadlock
            var change = new Change(audit, action, target, editor, RoleClaims.RoleOf(httpContext.User).Word(), time.GetUtcNow().UtcDateTime,
                httpContext.Connection.RemoteIpAddress?.ToString(), httpContext.TraceIdentifier);

            // 1 once the work has returned, when the commit may be under way. Until then a failure rolls the whole
            // transaction back, and InTransaction never commits once its caller has stopped waiting. Written on the pool's
            // thread and read on this one, perhaps while the work still runs, so through Interlocked and Volatile.
            var returned = 0;
            BlacklistOutcome outcome;

            // What the last run's work decided, set before returned is, so it's settled once returned reads 1
            BlacklistOutcome? decided = null;

            try
            {
                outcome = await DatabaseCalls.Guard(() => blacklist.RunInTransaction(async transaction =>
                {
                    // A run after a deadlock starts again, with everything the last one did rolled back
                    Interlocked.Exchange(ref returned, 0);
                    change.Restart();
                    decided = null;
                    var result = await work(change, transaction);
                    decided = result;
                    Interlocked.Exchange(ref returned, 1);
                    return result;
                }), logger);
            }
            catch (DashboardUnavailableException) when (Volatile.Read(ref returned) == 1)
            {
                // A refusal commits only its audit row, and an edit that changes nothing commits nothing, so the entry is as
                // the work found it whether or not the commit landed, and the page can say what the work decided
                if (decided is { } decision && decision != BlacklistOutcome.Done)
                {
                    logger.LogWarning("Blacklist entry {Target}'s {Action} ended {Outcome}, which changes nothing, and the database stopped answering at its commit (request {RequestId})",
                        target, action.AuditAction(), decision, httpContext.TraceIdentifier);
                    return decision;
                }

                // A limit that passed during the commit doesn't stop it, so this can't say nothing changed. Before the work
                // returned, nothing was saved, and the error page says so.
                logger.LogWarning("Blacklist entry {Target}'s {Action} may or may not have been saved, because the database stopped answering (request {RequestId})",
                    target, action.AuditAction(), httpContext.TraceIdentifier);

                if (change.Written is { } written)
                    await UnconfirmedChanges.RecordAsync(audit, written, logger);

                return BlacklistOutcome.Unsure;
            }

            logger.LogInformation("Blacklist entry {Target}'s {Action} ended {Outcome} (request {RequestId})", target, action.AuditAction(), outcome, httpContext.TraceIdentifier);
            return outcome;
        }

        // One change's audit rows, from what was worked out before the transaction. Never what an editor typed in a detail.
        sealed class Change(IAuditLogData audit, BlacklistAction action, string target, SignedInUser editor, string role, DateTime now, string? ip, string requestId)
        {
            // The row the last run wrote for the change itself, which the row saying it may not have landed repeats. A
            // refusal has none, since no change was on its way.
            public AuditEntry? Written { get; private set; }

            public void Restart() => Written = null;

            // Before is the row as the locked read found it, never as the page showed it, and nothing after, since
            // nothing changed
            public async Task<BlacklistOutcome> RefuseAsync(BlacklistOutcome outcome, string detail, string? before, DataTransaction transaction)
            {
                await audit.AddAudit(Entry(AuditOutcome.Refused, detail, before, null), transaction);
                return outcome;
            }

            public async Task WriteAsync(string? before, string? after, string? detail, DataTransaction transaction)
            {
                var entry = Entry(AuditOutcome.Ok, detail, before, after);
                await audit.AddAudit(entry, transaction);
                Written = entry;
            }

            AuditEntry Entry(string outcome, string? detail, string? before, string? after) => new()
            {
                at = now,
                actor_id = editor.Id,
                actor_name = editor.Name,
                actor_role = role,
                action = action.AuditAction(),
                outcome = outcome,
                target_type = AuditWords.BlacklistTarget,
                target_id = target,
                before_json = before,
                after_json = after,
                detail = detail,
                ip = ip,
                request_id = requestId
            };
        }
    }

    internal static class BlacklistChangeServices
    {
        public static IServiceCollection AddBlacklistChanges(this IServiceCollection services)
        {
            services.AddTransient<BlacklistChanges>();
            return services;
        }
    }
}
