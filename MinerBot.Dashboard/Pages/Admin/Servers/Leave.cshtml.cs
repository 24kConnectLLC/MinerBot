using System.Globalization;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Guilds.Models;
using DataAccessLibrary.Requests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Servers;
using MinerBot.Dashboard.Settings;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Pages.Admin.Servers
{
    // Asks MinerBot to leave one server, the one control nothing can take back but a new invite. The Owner's alone, on a page
    // of its own, so an Admin is refused by the framework before any handler. It says in full what a leave deletes, and takes
    // the server's name as MinerBot's copy has it at the POST, or its ID, typed to confirm, with a sign-in from the last 15
    // minutes, which the sender checks again whatever the form held. Sent leads back to the server's page, whose box follows
    // the request.
    [Authorize(Policy = Policies.Owner)]
    public sealed class LeaveServerModel(IBotGuildReads guilds, IBotRequestReads reads, TimeProvider time, ILogger<LeaveServerModel> logger) : PageModel
    {
        public ulong Id { get; private set; }
        public string IdText => Id.ToString(CultureInfo.InvariantCulture);

        // MinerBot's copy of the server, null for one it has never recorded
        public BotGuildRow? Server { get; private set; }
        public bool IsIn => Server is { left_at: null };

        public bool RequestsOn { get; private set; }

        // When this session signed in, and whether that's within the last 15 minutes, until when
        public DateTimeOffset? SignedInAt { get; private set; }
        public bool Fresh { get; private set; }
        public DateTimeOffset? FreshUntil { get; private set; }

        // The leave's last use for this server, and whether one more would be sent now
        public RequestLine Last { get; private set; } = new(null);
        public AvailabilityLine? Availability { get; private set; }

        // How the sender answered a form it didn't send on, why in its words, and the request those name
        public SendOutcome? Outcome { get; private set; }
        public string? Problem { get; private set; }
        public ulong? ProblemRequest { get; private set; }

        public string ServerLink => "/admin/servers/" + IdText;
        public string SignInAgainLink => FreshSignIn.SignInAgainLink(ServerLink + "/leave");

        // The form, only for a server MinerBot is in while requests are on, and only once the sign-in is fresh
        public ControlForm Form => !IsIn || !RequestsOn ? ControlForm.NotOffered : Fresh ? ControlForm.Offered : ControlForm.NeedsFreshSignIn;

        // A new key on every render, apart from the form sent when the database didn't confirm its save, which keeps the key
        // it was sent with, so sending it again finds the first request
        public string FormKey => FormKeys.For(Outcome);

        public async Task<IActionResult> OnGetAsync(long id)
        {
            // Every Discord ID is a positive long, which the route takes
            if (id <= 0)
                return NotFound();

            await LoadAsync((ulong)id);
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(long id, string? formkey, string? confirm)
        {
            if (id <= 0)
                return BadRequest();

            // The copy as it is now, so a server renamed since the page was drawn is refused with its new name shown
            await LoadAsync((ulong)id);

            if (!IsIn)
            {
                Problem = ServerActions.NothingToLeave + " Nothing was sent.";
                Response.StatusCode = StatusCodes.Status409Conflict;
                return Page();
            }

            var outcome = await HttpContext.RequestServices.GetRequiredService<RequestSender>()
                .SendAsync(new SendOrder(RequestKinds.GuildLeave, formkey, Id, Typed: new Confirmation.Server(confirm, Server!.name, Id)), HttpContext);

            switch (outcome)
            {
                // The server's page with the request's box, which says, once the bot has left, to reload the page a few seconds
                // later: MinerBot's copy marks the server as left when Discord's event about it comes, at the bot's next write
                // of its servers, every 5 seconds
                case SendOutcome.Sent sent:
                    return RedirectToPage("/Admin/Servers/Server", null, new { id = (long)Id, request = sent.Id }, "result");

                case SendOutcome.Refused { Why: SendRefusal.Forbidden }:
                    return Forbid();

                case SendOutcome.Refused { Why: SendRefusal.MadeUp }:
                    return BadRequest();
            }

            // The page again with why, the typed name never kept, and the form's key kept only after a save the database
            // didn't confirm
            Outcome = outcome;
            (Problem, ProblemRequest) = outcome switch
            {
                SendOutcome.SentBefore before => (RequestWords.SentBefore(before.Id), before.Id),
                SendOutcome.Refused refused => (refused.Words, refused.Waiting),
                _ => (RequestWords.Unsure, (ulong?)null)
            };

            RequestWords.Answer(Response, outcome, time.GetUtcNow().UtcDateTime);
            return Page();
        }

        // A request no handler takes would otherwise get the page as if it were a GET
        public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
        {
            if (context.HandlerMethod == null)
                context.Result = BadRequest();
        }

        async Task LoadAsync(ulong id)
        {
            Id = id;
            Server = await DatabaseCalls.Guard(() => guilds.GetGuild(Id), logger);
            RequestsOn = RequestSigning.ParseKey(HttpContext.RequestServices.GetRequiredService<IOptions<DashboardOptions>>().Value.RequestSigningKey) != null;

            var clock = time.GetUtcNow();
            SignedInAt = FreshSignIn.SignedInAt(HttpContext);
            Fresh = FreshSignIn.IsFresh(HttpContext, clock);
            FreshUntil = FreshSignIn.Until(HttpContext);

            if (!IsIn || !RequestsOn)
                return;

            var now = clock.UtcDateTime;
            var leave = RequestKinds.Find(RequestKinds.GuildLeave)!;
            var rows = await DatabaseCalls.Guard(() => reads.ListLatest([leave.Word], Id), logger);
            Last = new RequestLine(rows.FirstOrDefault() is { } row ? RequestViews.Of(row, null, now, RequestViews.Waits) : null);

            if (RequestActors.Of(HttpContext) is { } actor)
                Availability = (await LimitReads.LoadAsync(reads, actor.Id, now, logger)).Line(leave, Id, now);
        }
    }
}
