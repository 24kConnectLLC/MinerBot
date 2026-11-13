using System.Globalization;
using System.Text.Json;
using DataAccessLibrary;
using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Security;
using MinerBot.Shared.Armor;

namespace MinerBot.Dashboard.Armor
{
    internal enum ArmorReviewOutcome
    {
        Done,

        // There's no armor with that ID
        Missing,

        // Its status, or something the page showed, changed after the reviewer opened it
        Changed,

        // A blocking rule stops the approval, such as a name another armor was approved with since
        Blocked,

        // A warning wasn't ticked
        Unticked,

        // The database stopped answering once the work was done, while the change was being committed, so it may or may
        // not have landed
        Unsure
    }

    // One reviewer's action, already checked by ArmorForm. Version is ArmorVersion's value for the armor as the page
    // showed it, and Expected the website's word for the status it showed.
    internal sealed record ArmorReviewRequest(long Id, ArmorAction Action, string? Expected, string Version, string? Reason, IReadOnlySet<string> Ticks);

    // Each review action is one transaction on the website's connection: the status changes only while the armor is as
    // the reviewer saw it, and its armor_reviews row and its audit row commit with it, or nothing does. A refusal commits
    // only its audit row. A change the database didn't confirm gets an audit row of its own saying so.
    internal sealed class ArmorReviews(IArmorSubmissionData submissions, IArmorReviewData reviews, IAuditLogData audit, TimeProvider time,
        ILogger<ArmorReviews> logger)
    {
        const string ChangedDetail = "The armor had changed since the page showed it";

        public async Task<ArmorReviewOutcome> ApplyAsync(ArmorReviewRequest request, HttpContext httpContext)
        {
            var reviewer = SignedInUser.From(httpContext.User) ?? throw new InvalidOperationException("Only a signed-in reviewer can change an armor's status");
            var action = request.Action;

            // Read outside the transaction, for the name an approval's check looks for: that check has to be the
            // transaction's first statement on the armor table, or two approvals at once could deadlock
            var seen = await DatabaseCalls.Guard(() => submissions.GetSubmission(request.Id), logger);

            if (seen == null)
                return ArmorReviewOutcome.Missing;

            // Everything else is worked out before the transaction too, because it runs again after a deadlock
            var name = seen.ArmorName ?? "";
            var now = time.GetUtcNow().UtcDateTime;
            var role = RoleClaims.RoleOf(httpContext.User).Word();
            var target = request.Id.ToString(CultureInfo.InvariantCulture);
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            var review = new ArmorReview
            {
                armor_id = request.Id,
                from_status = request.Expected,
                to_status = action.Target(),
                reason = request.Reason,
                reviewed_by = reviewer.Id,
                reviewer_name = reviewer.Name,
                reviewed_at = now
            };

            // Before is the status the locked row really has, spelt as it's stored. After is the one the reviewer chose, with
            // an approval's ticked warnings, and empty for a refusal, since nothing changed.
            AuditEntry Entry(string outcome, string? detail, string? before, string? after, IReadOnlyList<string> ticked) => new()
            {
                at = now,
                actor_id = reviewer.Id,
                actor_name = reviewer.Name,
                actor_role = role,
                action = action.AuditAction(),
                outcome = outcome,
                target_type = AuditWords.ArmorTarget,
                target_id = target,
                before_json = Json(before, []),
                after_json = after == null ? null : Json(after, ticked),
                detail = detail,
                ip = ip,
                request_id = httpContext.TraceIdentifier
            };

            // 1 once the work has returned, when the commit may be under way. Until then a failure rolls the whole
            // transaction back, and InTransaction never commits once its caller has stopped waiting. Written on the pool's
            // thread and read on this one, perhaps while the work still runs, so through Interlocked and Volatile.
            var returned = 0;
            ArmorReviewOutcome outcome;

            // The audit row of the change the last run made, for the row saying it may not have landed. A refusal has none,
            // since no change was on its way. Written before returned is, so it's settled once returned reads 1.
            AuditEntry? written = null;

            // What the last run's work decided, set before returned is too
            ArmorReviewOutcome? decided = null;

            try
            {
                outcome = await DatabaseCalls.Guard(() => submissions.RunInTransaction(async transaction =>
                {
                    // A run after a deadlock starts again, with everything the last one did rolled back
                    Interlocked.Exchange(ref returned, 0);
                    written = null;
                    decided = null;
                    var result = await Work(transaction);
                    decided = result;
                    Interlocked.Exchange(ref returned, 1);
                    return result;
                }), logger);
            }
            catch (DashboardUnavailableException) when (Volatile.Read(ref returned) == 1)
            {
                // A refusal commits only its audit row, and an armor that isn't there commits nothing, so the armor is as
                // the work found it whether or not the commit landed, and the page can say what the work decided
                if (decided is { } decision && decision != ArmorReviewOutcome.Done)
                {
                    logger.LogWarning("Armor {ArmorId}'s {Action} ended {Outcome}, which changes nothing, and the database stopped answering at its commit (request {RequestId})",
                        request.Id, action.AuditAction(), decision, httpContext.TraceIdentifier);
                    return decision;
                }

                // A limit that passed during the commit doesn't stop it, so this can't say nothing changed. Before the work
                // returned, nothing was saved, and the error page says so.
                logger.LogWarning("Armor {ArmorId}'s {Action} may or may not have been saved, because the database stopped answering (request {RequestId})",
                    request.Id, action.AuditAction(), httpContext.TraceIdentifier);

                if (written != null)
                    await UnconfirmedChanges.RecordAsync(audit, written, logger);

                return ArmorReviewOutcome.Unsure;
            }

            logger.LogInformation("Armor {ArmorId}'s {Action} ended {Outcome} (request {RequestId})", request.Id, action.AuditAction(), outcome, httpContext.TraceIdentifier);

            // Anything that reaches outside the database after a change, such as announcing it in Discord or clearing the
            // bot's name cache, belongs here, after the commit: the work can run more than once, and holds the armor
            // table's locks while it runs, which keeps the website from saving new submissions
            return outcome;

            async Task<ArmorReviewOutcome> Work(DataTransaction transaction)
            {
                // First, as IsApprovedNameTaken needs: it locks the whole armor table until the commit, so a second approval
                // of the same name waits for this one and then sees it
                var nameTaken = action == ArmorAction.Approve && await submissions.IsApprovedNameTaken(name, request.Id, transaction);

                // Locked from here to the commit, so the website can't change it between these checks and the change
                var armor = await submissions.GetSubmission(request.Id, transaction);

                if (armor == null)
                    return ArmorReviewOutcome.Missing;

                var (decision, detail, ticked) = Decide(armor, nameTaken);

                if (decision != ArmorReviewOutcome.Done)
                {
                    await audit.AddAudit(Entry(AuditOutcome.Refused, detail, armor.Status, null, []), transaction);
                    return decision;
                }

                // The row is locked with the status checked above, and this compares through the same collation, so it
                // matches. If it ever doesn't, it's a refusal.
                if (!await submissions.ChangeStatus(request.Id, request.Expected, action.Target(), transaction))
                {
                    await audit.AddAudit(Entry(AuditOutcome.Refused, ChangedDetail, armor.Status, null, []), transaction);
                    return ArmorReviewOutcome.Changed;
                }

                await reviews.AddReview(review, transaction);

                var entry = Entry(AuditOutcome.Ok, null, armor.Status, action.Target(), ticked);
                await audit.AddAudit(entry, transaction);
                written = entry;
                return ArmorReviewOutcome.Done;
            }

            // Whether the action goes ahead on the row as it is now, and for an approval, which warnings were ticked
            (ArmorReviewOutcome Decision, string? Detail, IReadOnlyList<string> Ticked) Decide(ArmorSubmission armor, bool nameTaken)
            {
                // The status as the bot reads it, as the page showed it, so armor stored as 'approved' can be taken down. The
                // name check looked for the name read before the transaction, so it only counts while the row has it.
                if (!ArmorActions.TryStatusOf(armor, out var status) || status != request.Expected || ArmorVersion.Of(armor) != request.Version
                    || (action == ArmorAction.Approve && !string.Equals(armor.ArmorName ?? "", name, StringComparison.Ordinal)))
                    return (ArmorReviewOutcome.Changed, ChangedDetail, []);

                if (action != ArmorAction.Approve)
                    return (ArmorReviewOutcome.Done, null, []);

                // The rules again, on the row this approves, whatever the form said
                var check = ArmorRules.Check(armor, nameTaken);

                if (!check.CanApprove)
                    return (ArmorReviewOutcome.Blocked, "Blocked by the approval rules: " + string.Join(", ", check.Blocking.Select(problem => problem.Kind).Distinct()), []);

                var warnings = check.Warnings.Select(ArmorForm.TickKey).ToList();
                var unticked = warnings.Where(key => !request.Ticks.Contains(key)).ToList();

                if (unticked.Count > 0)
                    return (ArmorReviewOutcome.Unticked, "Warnings that weren't ticked: " + string.Join(", ", unticked), []);

                return (ArmorReviewOutcome.Done, null, warnings);
            }
        }

        // Only what changed, in a fixed shape the audit page can read
        static string Json(string? status, IReadOnlyList<string> ticked)
        {
            var fields = new Dictionary<string, object?> { ["Status"] = status };

            if (ticked.Count > 0)
                fields["Ticked"] = ticked;

            return JsonSerializer.Serialize(fields);
        }
    }

    internal static class ArmorReviewServices
    {
        public static IServiceCollection AddArmorReview(this IServiceCollection services)
        {
            services.AddTransient<ArmorReviews>();
            return services;
        }
    }
}
