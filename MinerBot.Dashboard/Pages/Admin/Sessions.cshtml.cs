using System.Globalization;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Dashboard.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Settings;
using MinerBot.Dashboard.Status;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Pages.Admin
{
    // Sessions and security, the Owner's alone: who is signed in and from where, ending one session or every other one, the
    // changes to sessions, and the dashboard's checks of itself. Ending a session acts on other people, so it needs a sign-in
    // from the last 15 minutes, checked here at the POST, and a stale stolen cookie can't sign everyone out; a try without
    // one is refused and recorded. A session ends as one transaction with its audit row, whose detail never holds the ended
    // session's address or browser, and its browser is signed out at its next request, since every request reads the row.
    [Authorize(Policy = Policies.Owner)]
    public sealed class SessionsModel(IDashboardSessionData sessions, IAuditLogData audit, TimeProvider time, ILogger<SessionsModel> logger) : PageModel
    {
        public const int ChangesShown = 25;

        public IReadOnlyList<SessionView> Sessions { get; private set; } = [];
        public AuditTable Changes { get; private set; } = new([], true);
        public long ChangeTotal { get; private set; }
        public IReadOnlyList<SelfCheck> Checks { get; private set; } = [];
        public DateTime Now { get; private set; }

        // When this session signed in, and whether that's within the last 15 minutes, until when
        public DateTimeOffset? SignedInAt { get; private set; }
        public bool Fresh { get; private set; }
        public DateTimeOffset? FreshUntil { get; private set; }

        // Whether the dashboard can sign requests, without which a sign-in sends the owner no message
        public bool RequestsOn { get; private set; }

        // What ?done= says, or why a form was refused, with the way to sign in again when that's why
        public string? Done { get; private set; }
        public string? Problem { get; private set; }

        public string SignInAgainLink => FreshSignIn.SignInAgainLink(RequestBanners.SessionsLink);

        // Under the way to sign in again, which ending sessions needs whether requests are on or not
        public string SignInAgainNote => RequestsOn ? ControlWords.SignInAgainNote : SessionWords.SignInAgainNoMessage;

        public async Task OnGetAsync([FromQuery] string? done)
        {
            Done = SessionWords.Done(done);
            await LoadAsync();
        }

        // One session, named by its hash: 64 small-letter hex digits, so a form someone made up is a 400
        public async Task<IActionResult> OnPostEndAsync(string? session)
        {
            if (session is not { Length: DashboardSessionData.HashLength * 2 } hex || !hex.All(char.IsAsciiHexDigitLower))
                return BadRequest();

            var hash = Convert.FromHexString(hex);

            if (SessionKeys.Current(HttpContext) is { } current && DashboardSessionData.HashOf(current).AsSpan().SequenceEqual(hash))
                return await AgainAsync(StatusCodes.Status409Conflict, SessionWords.YourOwn);

            if (await NotFreshAsync(AuditWords.Ended) is { } refused)
                return refused;

            var ended = await DatabaseCalls.Guard(() => sessions.RunInTransaction(async transaction =>
            {
                var row = await sessions.DeleteSessionByHash(hash, transaction);

                if (row != null)
                    await audit.AddAudit(Entry(AuditWords.Ended, AuditOutcome.Ok, SessionWords.Ended(row)), transaction);

                return row;
            }), logger);

            return RedirectToPage(null, null, new { done = ended == null ? "gone" : "ended" }, null);
        }

        // Every session but this one, everyone's, the Owner's other devices included
        public async Task<IActionResult> OnPostEndOthersAsync()
        {
            if (SessionKeys.Current(HttpContext) is not { } current)
                return BadRequest();

            if (await NotFreshAsync(AuditWords.EndedOthers) is { } refused)
                return refused;

            await DatabaseCalls.Guard(() => sessions.RunInTransaction(async transaction =>
            {
                var ended = await sessions.DeleteSessionsBut(current, transaction);

                if (ended.Count > 0)
                    await audit.AddAudit(Entry(AuditWords.EndedOthers, AuditOutcome.Ok, SessionWords.EndedOthers(ended)), transaction);

                return ended.Count;
            }), logger);

            return RedirectToPage(null, null, new { done = "ended-others" }, null);
        }

        // A request no handler takes would otherwise get the page as if it were a GET
        public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
        {
            if (context.HandlerMethod == null)
                context.Result = BadRequest();
        }

        // Refused and recorded while the sign-in is older than 15 minutes, or when there's no time to tell, and null otherwise
        async Task<IActionResult?> NotFreshAsync(string action)
        {
            if (FreshSignIn.IsFresh(HttpContext, time.GetUtcNow()))
                return null;

            await DatabaseCalls.Guard(() => audit.AddAudit(Entry(action, AuditOutcome.Refused, RequestWords.NotFreshDetail)), logger);
            return await AgainAsync(StatusCodes.Status403Forbidden, SessionWords.NotFresh(FreshSignIn.SignedInAt(HttpContext)));
        }

        async Task<IActionResult> AgainAsync(int status, string problem)
        {
            Problem = problem;
            await LoadAsync();
            Response.StatusCode = status;
            return Page();
        }

        // The Owner, from where they are, about sessions, with no ID: a session's ID is its key's hash, which no page shows
        AuditEntry Entry(string action, string outcome, string detail)
        {
            var user = SignedInUser.From(User);

            return new AuditEntry
            {
                at = time.GetUtcNow().UtcDateTime,
                actor_id = user?.Id,
                actor_name = user?.Name,
                actor_role = RoleClaims.RoleOf(User).Word(),
                action = action,
                outcome = outcome,
                target_type = AuditWords.SessionTarget,
                detail = detail,
                ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                request_id = HttpContext.TraceIdentifier
            };
        }

        async Task LoadAsync()
        {
            var clock = time.GetUtcNow();
            Now = clock.UtcDateTime;
            SignedInAt = FreshSignIn.SignedInAt(HttpContext);
            Fresh = FreshSignIn.IsFresh(HttpContext, clock);
            FreshUntil = FreshSignIn.Until(HttpContext);
            RequestsOn = RequestSigning.ParseKey(HttpContext.RequestServices.GetRequiredService<IOptions<DashboardOptions>>().Value.RequestSigningKey) != null;

            // What RetrieveAsync would still take: before its 12 hours are up, and used within the idle limit
            var rows = await DatabaseCalls.Guard(() => sessions.ListSessions(Now, Now - DbTicketStore.IdleLimit), logger);
            var current = SessionKeys.Current(HttpContext) is { } key ? DashboardSessionData.HashOf(key) : null;
            var roles = HttpContext.RequestServices.GetRequiredService<RoleLists>();

            Sessions = [.. rows.Select(row => new SessionView(
                Convert.ToHexStringLower(row.id_hash),
                row.user_id,
                row.user_name,
                SessionWords.Role(roles.RoleOf(row.user_id)),
                row.created_at,
                row.created_at <= Now && Now - row.created_at < FreshSignIn.Window,
                row.last_seen_at,
                row.last_seen_at + DbTicketStore.IdleLimit < row.expires_at ? row.last_seen_at + DbTicketStore.IdleLimit : row.expires_at,
                row.ip,
                row.user_agent,
                current != null && row.id_hash.AsSpan().SequenceEqual(current)))];

            var changes = await DatabaseCalls.Guard(() => audit.ListAudit(new AuditQuery
            {
                TargetType = AuditWords.SessionTarget,
                PageSize = ChangesShown,
                IncludeIp = true
            }), logger);

            ChangeTotal = changes.Total;
            Changes = AuditRows.Table(changes.Rows, showIp: true, labelledBy: "changes");

            Checks = await HttpContext.RequestServices.GetRequiredService<SelfCheckReads>().RunAsync();
        }
    }
}
