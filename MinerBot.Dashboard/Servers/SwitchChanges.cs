using System.Globalization;
using System.Text.Json;
using DataAccessLibrary;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Security;

namespace MinerBot.Dashboard.Servers
{
    internal enum SwitchOutcome
    {
        // Saved, with the switches' new version
        Saved,

        // What was asked for is what the server has, so nothing was written
        Unchanged,

        // The same, though the switches changed after the page was drawn: the form's earlier save landed, as after a double
        // click, Back and Resubmit, or a save the database didn't confirm, or someone made the same change first
        Already,

        // The switches changed after the page was drawn, so only the refusal's audit row was written
        Stale,

        // The person has saved switches as often as an hour allows
        Limit,

        // The database stopped answering once the work was done, while the change was being committed, so it may or may not
        // have landed
        Unsure
    }

    // How a save went, the switches' version once saved, and when the person's limit lets them save again
    internal sealed record SwitchResult(SwitchOutcome Outcome, uint? Version = null, DateTime? After = null);

    // One save from a server's page, already checked: the server, the version of its switches the page was drawn from, 0
    // when it had none, and each switch as the form sent it
    internal sealed record SwitchRequest(ulong GuildId, uint Version, bool ArmorOn, bool MapsOn, bool LobbiesOn);

    // The Owner's save of one server's switches. The person's limit is counted from their own rows in the audit log, so a
    // restart of the dashboard resets nothing. The save is one transaction on MinerBot's own connection: the switches apply
    // only while they're the version the page showed, and the audit row commits with them, or nothing does. A stale form
    // commits only its refusal's audit row, a form that asks for what the server has commits nothing, and a commit the
    // database didn't confirm gets an audit row of its own saying so. The gate before the handler refuses anyone but the Owner.
    internal sealed class SwitchChanges(IGuildSwitchData switches, IAuditLogData audit, TimeProvider time, ILogger<SwitchChanges> logger)
    {
        // A version in digits, as the page writes it, and a box as a browser sends it: "on" when ticked, nothing when not
        public static bool TryVersion(string? text, out uint version) =>
            uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out version) && text!.Length <= 10;

        public static bool TryTick(string? value, out bool ticked)
        {
            ticked = value == "on";
            return value is null or "on";
        }

        // The audit row of a refusal before any save, such as an Admin's POST, which the gate records
        public static AuditEntry Refusal(HttpContext context, ulong? guildId, string detail, DateTime now)
        {
            var user = SignedInUser.From(context.User);

            return new AuditEntry
            {
                at = now,
                actor_id = user?.Id,
                actor_name = user?.Name,
                actor_role = RoleClaims.RoleOf(context.User).Word(),
                action = AuditWords.SwitchesChange,
                outcome = AuditOutcome.Refused,
                guild_id = guildId,
                detail = detail,
                ip = context.Connection.RemoteIpAddress?.ToString(),
                request_id = context.TraceIdentifier
            };
        }

        public async Task<SwitchResult> SaveAsync(SwitchRequest request, HttpContext httpContext)
        {
            var owner = SignedInUser.From(httpContext.User) ?? throw new InvalidOperationException("Only a signed-in Owner can change switches");

            // Everything the audit rows need is worked out before the transaction, because it runs again after a deadlock
            var now = time.GetUtcNow().UtcDateTime;
            var role = RoleClaims.RoleOf(httpContext.User).Word();
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            AuditEntry Entry(string outcome, string? detail, string? before, string? after) => new()
            {
                at = now,
                actor_id = owner.Id,
                actor_name = owner.Name,
                actor_role = role,
                action = AuditWords.SwitchesChange,
                outcome = outcome,
                guild_id = request.GuildId,
                before_json = before,
                after_json = after,
                detail = detail,
                ip = ip,
                request_id = httpContext.TraceIdentifier
            };

            // The person's own saves in the last hour, and those whose commit wasn't confirmed, which may have landed. The 30th
            // newest leaves the hour at the time they may save again.
            var saves = await DatabaseCalls.Guard(() => audit.ListActionTimes(owner.Id, AuditWords.SwitchesChange, [AuditOutcome.Ok, AuditOutcome.Unknown],
                now - SwitchLimits.Window, SwitchLimits.PerHour), logger);

            if (saves.Count >= SwitchLimits.PerHour)
            {
                await DatabaseCalls.Guard(() => audit.AddAudit(Entry(AuditOutcome.Refused, SwitchWords.LimitDetail, null, null)), logger);
                return new SwitchResult(SwitchOutcome.Limit, After: saves[SwitchLimits.PerHour - 1] + SwitchLimits.Window);
            }

            var change = new SwitchChange
            {
                GuildId = request.GuildId,
                ArmorOn = request.ArmorOn,
                MapsOn = request.MapsOn,
                LobbiesOn = request.LobbiesOn,
                Version = request.Version,
                At = now,
                ChangedBy = owner.Id,
                Role = role
            };

            // 1 once the work has returned, when the commit may be under way. Until then a failure rolls the whole
            // transaction back, and InTransaction never commits once its caller has stopped waiting. Written on the pool's
            // thread and read on this one, perhaps while the work still runs, so through Interlocked and Volatile.
            var returned = 0;
            SwitchSave saved;

            // The audit row of the change the last run made, and what that run decided, both set before returned is
            AuditEntry? written = null;
            SwitchSave? decided = null;

            try
            {
                saved = await DatabaseCalls.Guard(() => switches.RunInTransaction(async transaction =>
                {
                    // A run after a deadlock starts again, with everything the last one did rolled back
                    Interlocked.Exchange(ref returned, 0);
                    written = null;
                    decided = null;

                    var save = await switches.SaveSwitches(change, transaction);

                    if (save.Outcome == SwitchSaveOutcome.Saved)
                    {
                        var entry = Entry(AuditOutcome.Ok, null, Json(save.Before, request, before: true), Json(save.Before, request, before: false));
                        await audit.AddAudit(entry, transaction);
                        written = entry;
                    }
                    else if (save.Outcome == SwitchSaveOutcome.Stale)
                    {
                        // The switches as the locked read found them, those this form would have changed, and nothing after
                        await audit.AddAudit(Entry(AuditOutcome.Refused, SwitchWords.StaleDetail, Json(save.Before, request, before: true), null), transaction);
                    }

                    decided = save;
                    Interlocked.Exchange(ref returned, 1);
                    return save;
                }), logger);
            }
            catch (DashboardUnavailableException) when (Volatile.Read(ref returned) == 1)
            {
                // A stale form commits only its audit row, and one that changes nothing commits nothing, so the switches are as
                // the work found them whether or not the commit landed, and the page can say what the work decided
                if (decided is { Outcome: not SwitchSaveOutcome.Saved } decision)
                {
                    logger.LogWarning("Server {GuildId}'s switches ended {Outcome}, which changes nothing, and the database stopped answering at its commit (request {RequestId})",
                        request.GuildId, decision.Outcome, httpContext.TraceIdentifier);
                    return Result(decision, request.Version);
                }

                logger.LogWarning("Server {GuildId}'s switches may or may not have been saved, because the database stopped answering (request {RequestId})",
                    request.GuildId, httpContext.TraceIdentifier);

                if (written != null)
                    await UnconfirmedChanges.RecordAsync(audit, written, logger);

                return new SwitchResult(SwitchOutcome.Unsure);
            }

            logger.LogInformation("Server {GuildId}'s switches ended {Outcome} (request {RequestId})", request.GuildId, saved.Outcome, httpContext.TraceIdentifier);
            return Result(saved, request.Version);
        }

        // A form that asks for what the server has, from switches that aren't the version it was drawn from, finds its own
        // change made: an earlier send of it was saved, or someone made the same change first, as the blacklist's edit says
        static SwitchResult Result(SwitchSave save, uint drawn) => save.Outcome switch
        {
            SwitchSaveOutcome.Saved => new SwitchResult(SwitchOutcome.Saved, save.After!.version),
            SwitchSaveOutcome.Unchanged => new SwitchResult((save.Before?.version ?? 0) == drawn ? SwitchOutcome.Unchanged : SwitchOutcome.Already),
            _ => new SwitchResult(SwitchOutcome.Stale)
        };

        // Only the switches the form changes from what the server has, such as {"armor_on":true}: as they were, or as asked
        static string Json(GuildSwitches? current, SwitchRequest asked, bool before)
        {
            var fields = new Dictionary<string, bool>();

            void Field(string key, bool now, bool wanted)
            {
                if (now != wanted)
                    fields[key] = before ? now : wanted;
            }

            Field(SwitchWords.ArmorKey, SwitchWords.ArmorOn(current), asked.ArmorOn);
            Field(SwitchWords.MapsKey, SwitchWords.MapsOn(current), asked.MapsOn);
            Field(SwitchWords.LobbiesKey, SwitchWords.LobbiesOn(current), asked.LobbiesOn);

            return JsonSerializer.Serialize(fields);
        }
    }

    internal static class SwitchChangeServices
    {
        public static IServiceCollection AddSwitchChanges(this IServiceCollection services)
        {
            services.AddTransient<SwitchChanges>();
            return services;
        }
    }
}
