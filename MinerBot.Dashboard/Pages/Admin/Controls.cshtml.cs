using System.Globalization;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Status;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Settings;
using MinerBot.Dashboard.Status;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Pages.Admin
{
    // Whether a control's form is drawn: not for someone whose role can't ask for it, nor while requests are off, and not
    // until the sign-in is fresh for a control that needs one, which then shows the way to sign in again instead
    public enum ControlForm
    {
        NotOffered,
        Offered,
        NeedsFreshSignIn
    }

    // A control's part above its form: its label, what it does, what the bot last recorded of it, the bot's limit, its last
    // use, whether one more would be sent now, and why the last try from it was refused. While the sign-in isn't fresh
    // enough for it, the line that says so, the way to sign in again, and what was typed into it, read-only. Its form's key,
    // and the word it needs typed, if any.
    public sealed record ControlView(string Handler, string Label, string Help, string Limit, RequestLine Last, ControlForm Form, string FormKey)
    {
        public string HelpId => "help-" + Handler;

        public string? Note { get; init; }
        public string? Word { get; init; }
        public AvailabilityLine? Availability { get; init; }
        public string? Problem { get; init; }
        public ulong? ProblemRequest { get; init; }
        public string? FreshLine { get; init; }
        public string? SignInAgain { get; init; }
        public string? Typed { get; init; }

        // The text box of a form drawn alone by _ControlForm, as the page draws the pause's and the status text's
        public ControlTextBox? TextBox { get; init; }
    }

    // A form's text box: its ID and field name, its label and hint, its limit, and what was typed into it
    public sealed record ControlTextBox(string Id, string Name, string Label, string Hint, int Limit, string? Value)
    {
        public string HintId => Id + "-hint";
    }

    // The bot's controls, each asking MinerBot to do something through a signed request: the Owner's every control, and an
    // Admin's two, the cache clear and the notification job. A gate runs before any handler: a POST to a handler that isn't
    // on the map is a 400, and one whose kind the person's role can't ask for is refused, recorded and sent to /denied
    // without reaching the sender, which checks the role again. Each handler sends its map entry's kind, never one from the
    // form, through RequestSender, which checks the form's key, the fresh sign-in, the typed word, the rules and both limits
    // again, whatever the page showed. Sent redirects here with ?request=, whose box follows the request; anything else draws
    // the page again with why, the text typed kept and the typed word not.
    public sealed class ControlsModel(IBotRequestReads reads, IBotStatusReads status, IAuditLogData audit, TimeProvider time, ILogger<ControlsModel> logger)
        : PageModel
    {
        public const string Address = "/admin/bot/controls";

        // Every handler and the one kind it sends: the gate's map
        internal static readonly IReadOnlyDictionary<string, string> Handlers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ControlWords.ClearCaches] = RequestKinds.CachesClear,
            [ControlWords.RunNotifications] = RequestKinds.JobNotifications,
            [ControlWords.RunColors] = RequestKinds.JobColors,
            [ControlWords.Pause] = RequestKinds.PostsPause,
            [ControlWords.Resume] = RequestKinds.PostsResume,
            [ControlWords.SetStatus] = RequestKinds.StatusSet,
            [ControlWords.DefaultStatus] = RequestKinds.StatusSet,
            [ControlWords.ReconnectSteam] = RequestKinds.SteamReconnect,
            [ControlWords.RenewToken] = RequestKinds.SteamRenew,
            [ControlWords.Restart] = RequestKinds.BotRestart
        };

        // The page's kinds, for each control's last use and the box ?request= names
        internal static readonly IReadOnlyList<string> Kinds = [.. Handlers.Values.Distinct()];

        // The forms whose text box goes back filled after a refusal: a pause's reason and a status text
        static readonly HashSet<string> WithText = new(StringComparer.Ordinal) { ControlWords.Pause, ControlWords.SetStatus };

        public StatusBox Box { get; private set; } = null!;
        public IReadOnlyList<Banner> Banners { get; private set; } = [];

        // The box for the request ?request= names, when it's one of this page's
        public RequestSection? Result { get; private set; }

        public bool IsOwner { get; private set; }
        public bool RequestsOn { get; private set; }

        // When this session signed in, and whether that's within the last 15 minutes, until when
        public DateTimeOffset? SignedInAt { get; private set; }
        public bool Fresh { get; private set; }
        public DateTimeOffset? FreshUntil { get; private set; }

        public bool Paused { get; private set; }
        public string PauseLine { get; private set; } = "";
        public StatusLine StatusLine { get; private set; } = null!;
        public string NotificationsRun { get; private set; } = "";
        public string ColorsRun { get; private set; } = "";
        public string SteamState { get; private set; } = "";
        public string RestartWhen { get; private set; } = "";

        // A form sent and not sent on: its handler, how the sender answered, why in its words, the request those name, and
        // the text typed into its box
        public string? Posted { get; private set; }
        public SendOutcome? Outcome { get; private set; }
        public string? Problem { get; private set; }
        public ulong? ProblemRequest { get; private set; }
        public string? Typed { get; private set; }

        // After a save the database didn't confirm, when the page's own reads failed too: the form sent, drawn alone with its
        // key kept, since nothing else can be read. Null otherwise.
        public ControlView? Unread { get; private set; }

        IReadOnlyDictionary<string, RequestView> latest = new Dictionary<string, RequestView>();
        LimitReads limits = new([], [], []);
        DateTime now;
        string role = "";

        public async Task<IActionResult> OnGetAsync([FromQuery] string? request)
        {
            // Only chooses what to show, so anything but a request's number shows nothing
            await LoadAsync(ulong.TryParse(request, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null);
            return Page();
        }

        public Task<IActionResult> OnPostClearCachesAsync(string? formkey) => SendAsync(ControlWords.ClearCaches, formkey);

        public Task<IActionResult> OnPostRunNotificationsAsync(string? formkey) => SendAsync(ControlWords.RunNotifications, formkey);

        public Task<IActionResult> OnPostRunColorsAsync(string? formkey) => SendAsync(ControlWords.RunColors, formkey);

        public Task<IActionResult> OnPostPauseAsync(string? formkey, string? reason, string? confirm) =>
            SendAsync(ControlWords.Pause, formkey, reason, new Confirmation.Word(confirm));

        public Task<IActionResult> OnPostResumeAsync(string? formkey) => SendAsync(ControlWords.Resume, formkey);

        // The status text's own form, which is never sent empty: only Back to the default sends no text
        public Task<IActionResult> OnPostSetStatusAsync(string? formkey, string? text) => SendAsync(ControlWords.SetStatus, formkey, text, new Confirmation.StatusText());

        public Task<IActionResult> OnPostDefaultStatusAsync(string? formkey) => SendAsync(ControlWords.DefaultStatus, formkey);

        public Task<IActionResult> OnPostReconnectSteamAsync(string? formkey, string? confirm) =>
            SendAsync(ControlWords.ReconnectSteam, formkey, typed: new Confirmation.Word(confirm));

        public Task<IActionResult> OnPostRenewTokenAsync(string? formkey, string? confirm) => SendAsync(ControlWords.RenewToken, formkey, typed: new Confirmation.Word(confirm));

        public Task<IActionResult> OnPostRestartAsync(string? formkey, string? confirm) => SendAsync(ControlWords.Restart, formkey, typed: new Confirmation.Word(confirm));

        // The gate, before any handler. The antiforgery check and the origin check have run already, so a forged POST can't
        // write its audit row.
        public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(next);

            // 1. A request no handler takes, such as a POST without one, would otherwise get the page as if it were a GET
            if (context.HandlerMethod is not { } handler)
            {
                context.Result = BadRequest();
                return;
            }

            if (!string.Equals(handler.HttpMethod, HttpMethods.Get, StringComparison.OrdinalIgnoreCase))
            {
                // 2. A handler that isn't on the map never runs, so one added without an entry sends nothing
                if (handler.Name is not { } name || !Handlers.TryGetValue(name, out var word) || RequestKinds.Find(word) is not { } kind)
                {
                    context.Result = BadRequest();
                    return;
                }

                // 3. A kind the person's role can't ask for: one audit row, as the sender writes for it, and /denied. The
                // handler never runs, so the sender is never reached.
                if (!kind.Allows(RoleClaims.RoleOf(User).Word()))
                {
                    var refusal = RequestSender.Refusal(HttpContext, kind, RequestActors.Of(HttpContext), null, RequestWords.ForbiddenDetail(kind), time.GetUtcNow());
                    await DatabaseCalls.Guard(() => audit.AddAudit(refusal), logger);
                    context.Result = Forbid();
                    return;
                }
            }

            // 4. The handler, which sends its own entry's kind
            await next();
        }

        // One control above its form, for whoever may use it, with the way to sign in again in place of the form while a
        // control that needs a fresh sign-in hasn't one, at the page's anchor given
        public ControlView Control(string handler, string anchor)
        {
            var text = ControlWords.Of(handler);
            var kind = Kind(handler);
            var posted = handler == Posted || (handler == ControlWords.SetStatus && Posted == ControlWords.DefaultStatus);
            var form = Form(handler);

            // The status text's two forms share one part above them, and Back to the default needs no fresh sign-in
            var available = form == ControlForm.Offered || (handler == ControlWords.SetStatus && Form(ControlWords.DefaultStatus) == ControlForm.Offered);

            return new ControlView(handler, text.Label, text.Help, RequestWords.Limit(kind), new RequestLine(latest.GetValueOrDefault(kind.Word)), form, KeyFor(handler))
            {
                Note = handler switch
                {
                    ControlWords.RunNotifications => NotificationsRun,
                    ControlWords.RunColors => ColorsRun,
                    _ => null
                },
                Word = Confirmations.WordFor(kind),
                Availability = available ? limits.Line(kind, null, now, BotRefuses(handler)) : null,
                Problem = posted ? Problem : null,
                ProblemRequest = posted ? ProblemRequest : null,
                FreshLine = form == ControlForm.NeedsFreshSignIn ? ControlWords.NeedsFresh(SignedInAt) : null,
                SignInAgain = form == ControlForm.NeedsFreshSignIn ? FreshSignIn.SignInAgainLink(Address + "#" + anchor) : null,
                Typed = form == ControlForm.NeedsFreshSignIn && handler == Posted ? Typed : null
            };
        }

        public ControlForm Form(string handler)
        {
            var kind = Kind(handler);

            if (!RequestsOn || !kind.Allows(role))
                return ControlForm.NotOffered;

            return FreshSignIn.Needs(kind, WithText.Contains(handler) ? "" : null) && !Fresh ? ControlForm.NeedsFreshSignIn : ControlForm.Offered;
        }

        // A new key for every form on every render, refusals included, apart from the form sent when the database didn't
        // confirm its save, which keeps the key it was sent with, so sending it again finds the first request
        public string KeyFor(string handler) => handler == Posted ? FormKeys.For(Outcome) : RequestFields.NewKey();

        // What was typed into the form's text box, back in it after a refusal; the typed word never is
        public string? Kept(string handler) => handler == Posted ? Typed : null;

        public string SignInAgainLink => FreshSignIn.SignInAgainLink(Address);

        // What the page's own reads show the bot refusing now, by the bot's own rules, for the availability line: the
        // notification job while posts are paused, and a restart in a run's first 5 minutes
        string? BotRefuses(string handler) => handler switch
        {
            ControlWords.RunNotifications when Paused => ControlWords.RefusedWhilePaused,
            ControlWords.Restart when Box.Status is { } status && now < status.started_at + ControlWords.RestartAfterStart =>
                ControlWords.RestartFrom(status.started_at + ControlWords.RestartAfterStart),
            _ => null
        };

        static RequestKind Kind(string handler) => RequestKinds.Find(Handlers[handler])!;

        // The section of the page each control is drawn in, as Controls.cshtml gives it to Control, for the way to sign in
        // again from the form drawn alone, which comes back there as each control's own does
        static string Section(string handler) => handler switch
        {
            ControlWords.Pause or ControlWords.Resume => "posts",
            ControlWords.SetStatus or ControlWords.DefaultStatus => "status-text",
            ControlWords.RunNotifications or ControlWords.RunColors => "jobs",
            ControlWords.ClearCaches => "caches",
            ControlWords.ReconnectSteam or ControlWords.RenewToken => "steam",
            _ => "restart"
        };

        async Task<IActionResult> SendAsync(string handler, string? formkey, string? argument = null, Confirmation? typed = null)
        {
            var outcome = await HttpContext.RequestServices.GetRequiredService<RequestSender>()
                .SendAsync(new SendOrder(Handlers[handler], formkey, Argument: argument, Typed: typed), HttpContext);

            switch (outcome)
            {
                // Its own address, so reloading shows the box rather than sending the form again
                case SendOutcome.Sent sent:
                    return RedirectToPage(null, null, new { request = sent.Id }, "result");

                case SendOutcome.Refused { Why: SendRefusal.Forbidden }:
                    return Forbid();

                case SendOutcome.Refused { Why: SendRefusal.MadeUp }:
                    return BadRequest();
            }

            Posted = handler;
            Outcome = outcome;
            Typed = argument;
            (Problem, ProblemRequest) = outcome switch
            {
                SendOutcome.SentBefore before => (RequestWords.SentBefore(before.Id), before.Id),
                SendOutcome.Refused refused => (refused.Words, refused.Waiting),
                _ => (RequestWords.Unsure, (ulong?)null)
            };

            try
            {
                await LoadAsync(null);
            }
            // A save the database didn't confirm, which may have written the request, most often because the database stopped
            // answering, so the reads after it fail too. Not the error page, which would say nothing was changed: why, and the
            // form sent, alone, with its key. Anything else sent nothing, and the error page says so truly.
            catch (DashboardUnavailableException) when (outcome is SendOutcome.Unsure)
            {
                var clock = time.GetUtcNow();
                now = clock.UtcDateTime;
                LoadPerson(clock);
                Unread = Alone(handler);
            }

            // The sign-in passed its 15 minutes while the save was awaited, so the form sent isn't drawn again, and the words
            // don't offer it
            if (outcome is SendOutcome.Unsure && Form(handler) == ControlForm.NeedsFreshSignIn)
                Problem = RequestWords.UnsureNotFresh;

            // Nor is a pause's or a resume's once the page's reads show posts as it would leave them, as when the bot took it
            // meanwhile: the other one's control is drawn in its place, and the line under Posts is where to look
            if (outcome is SendOutcome.Unsure && Unread == null && handler == (Paused ? ControlWords.Pause : ControlWords.Resume))
                Problem = RequestWords.UnsurePostsLine;

            RequestWords.Answer(Response, outcome, now);
            return Page();
        }

        // The form sent, drawn alone without the page's reads: its key kept, what was typed into its box, and, should the
        // sign-in have gone stale meanwhile, the way to sign in again in its place, back to the control's section
        ControlView Alone(string handler)
        {
            var text = ControlWords.Of(handler);
            var kind = Kind(handler);
            var form = Form(handler);

            return new ControlView(handler, text.Label, text.Help, RequestWords.Limit(kind), new RequestLine(null), form, KeyFor(handler))
            {
                Word = Confirmations.WordFor(kind),
                FreshLine = form == ControlForm.NeedsFreshSignIn ? ControlWords.NeedsFresh(SignedInAt) : null,
                SignInAgain = form == ControlForm.NeedsFreshSignIn ? FreshSignIn.SignInAgainLink(Address + "#" + Section(handler)) : null,
                Typed = form == ControlForm.NeedsFreshSignIn ? Typed : null,
                TextBox = handler switch
                {
                    ControlWords.Pause => new ControlTextBox("pause-reason", "reason", ControlWords.ReasonLabel, ControlWords.ReasonHint, RequestRules.ReasonLimit, Typed),
                    ControlWords.SetStatus => new ControlTextBox("new-status-text", "text", ControlWords.StatusTextLabel, ControlWords.StatusTextHint, RequestRules.StatusTextLimit, Typed),
                    _ => null
                }
            };
        }

        // What the page knows without the database: who's asking, whether requests are on, and the sign-in, by the clock given
        void LoadPerson(DateTimeOffset clock)
        {
            var person = RoleClaims.RoleOf(User);
            role = person.Word();
            IsOwner = person == DashboardRole.Owner;
            RequestsOn = RequestSigning.ParseKey(HttpContext.RequestServices.GetRequiredService<IOptions<DashboardOptions>>().Value.RequestSigningKey) != null;

            SignedInAt = FreshSignIn.SignedInAt(HttpContext);
            Fresh = FreshSignIn.IsFresh(HttpContext, clock);
            FreshUntil = FreshSignIn.Until(HttpContext);
        }

        async Task LoadAsync(ulong? request)
        {
            Box = await HttpContext.RequestServices.GetRequiredService<StatusBoxes>().LoadAsync();
            now = Box.Now;

            var person = RoleClaims.RoleOf(User);
            LoadPerson(time.GetUtcNow());

            var facts = await HttpContext.RequestServices.GetRequiredService<BannerReads>().LoadAsync(Box.Status);
            Banners = RequestBanners.Of(facts, person, now);
            Paused = facts.Settings?.posts_paused == true;
            PauseLine = SettingsWords.PauseLine(facts.Settings, facts.PausedBy);
            StatusLine = SettingsWords.StatusLine(facts.Settings);

            // The jobs as the bot names them on the request list
            var jobs = await DatabaseCalls.Guard(() => status.ListJobs(), logger);
            var notifications = Kind(ControlWords.RunNotifications).Job!;
            var colors = Kind(ControlWords.RunColors).Job!;
            NotificationsRun = ControlWords.LastRun(notifications, jobs.FirstOrDefault(job => job.job == notifications), Box.Status);
            ColorsRun = ControlWords.LastRun(colors, jobs.FirstOrDefault(job => job.job == colors), Box.Status);
            SteamState = ControlWords.SteamState(Box.Status, now);
            RestartWhen = ControlWords.RestartWhen(Box.Status);

            var rows = await DatabaseCalls.Guard(() => reads.ListLatest(Kinds), logger);
            latest = rows.ToDictionary(row => row.kind, row => RequestViews.Of(row, Box.Status, now, RequestViews.Waits), StringComparer.Ordinal);

            if (RequestActors.Of(HttpContext) is { } actor)
                limits = await LimitReads.LoadAsync(reads, actor.Id, now, logger);

            if (request is { } named)
                Result = await HttpContext.RequestServices.GetRequiredService<RequestBoxes>().SectionAsync(named, Kinds, null,
                    $"{Address}?request={named.ToString(CultureInfo.InvariantCulture)}#result");
        }
    }
}
