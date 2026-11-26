using System.Globalization;
using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Armor;
using MinerBot.Dashboard.Hosting;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Settings;
using MinerBot.Dashboard.Status;
using MinerBot.Shared.Armor;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Pages.Admin.Armor
{
    // One armor submission with everything a reviewer needs to decide: its text with the hidden tricks shown, its
    // screenshot when it's an upload on the website, its colours, how the bot will show it, its history, and the rules.
    // Its forms approve, reject, take down or move it back to pending, each through ArmorReviews, and an approval, a
    // take-down or a move back from Approved then asks the bot to clear its name cache, once the review has committed.
    // Approved armor the bot hasn't found yet can be announced now, through the same sender as every request to the bot. A
    // bot that keeps what it finds in MinerBot's database (PostingRules) has a record of where it posted the armor, which
    // this page lists, and from which Back to pending from Approved is offered only before a post; that record is read on
    // its own, so a read of it that fails costs only its part, and Back to pending from Approved, never the other actions.
    // With an older bot, Announce now isn't offered once the bot has stopped cleanly or started again since its last
    // notification run, and the words are the ones for a bot that keeps what it finds only in memory. A form that wasn't
    // done or sent is kept as a form's notice and redirects here with ?notice=, whose GET draws why with the armor as it is
    // then, never this page drawn on the POST, which a reload would send again, and which would act once the refusal's cause
    // had gone.
    public sealed class ArmorReviewModel(IArmorSubmissionData submissions, IArmorReviewData reviews, IBotRequestQueue queue, IBotRequestReads requests,
        IBotStatusReads status, IDeliveryReads deliveries, IBotGuildReads guilds, TimeProvider time, ILogger<ArmorReviewModel> logger) : PageModel
    {
        // The requests whose box this page shows when ?request= names one: the cache clear after a review, and Announce now
        static readonly IReadOnlyList<string> Kinds = [RequestKinds.CachesClear, RequestKinds.JobNotifications];

        // The forms a notice names: Announce now, and each action by its done word. What an action's notice says stopped it,
        // and that an announce's words are for the top of the page alone, as for armor that isn't approved now.
        const string AnnounceForm = "announce";
        static readonly IReadOnlyList<string> Forms = [AnnounceForm, .. Enum.GetValues<ArmorAction>().Select(action => action.DoneWord())];
        const string ChangedState = "changed", BlockedState = "blocked", UntickedState = "unticked", PostedState = "posted", PageState = "page";

        public long Id { get; private set; }
        public ArmorSubmission? Armor { get; private set; }
        public ArmorCheck? Check { get; private set; }

        // ArmorVersion's value for the armor as this page shows it, which each form sends back
        public string Version { get; private set; } = "";

        // A status that matches none of the website's words, which nothing here can change
        public bool StatusUnknown { get; private set; }

        public IReadOnlyList<ArmorAction> Actions { get; private set; } = [];
        public IReadOnlyList<ArmorReview> History { get; private set; } = [];
        public EmbedPreview? Post { get; private set; }
        public EmbedPreview? Card { get; private set; }

        // Whether an approval may bring no post, because the bot may have seen this armor approved already
        public bool MayNotPost { get; private set; }

        // Whether the bot that runs keeps what it finds in MinerBot's database, which the words and Announce now follow
        public bool KeepsPosts { get; private set; }

        // MinerBot's record of the armor, null when it couldn't be read, with each server's line and the line above them
        public ItemPosts? Record { get; private set; }
        public bool RecordUnknown { get; private set; }
        public IReadOnlyList<ArmorPostLine> PostLines { get; private set; } = [];
        public string? PostsLine { get; private set; }

        // Where it was posted, drawn for a bot that keeps a record, for approved armor or armor it has found
        public bool ShowsPosts { get; private set; }

        // Back to pending, which from Approved needs the record to show no post yet, and the words in its place when it doesn't
        public bool BackToPendingOffered { get; private set; }
        public string? BackToPendingInstead { get; private set; }

        // A message for the top of the page, from ArmorWords' fixed set, and whether it says why nothing was done
        public string? Notice { get; private set; }
        public bool NoticeIsProblem { get; private set; }

        // The database stopped answering while a change was being committed, which may or may not have landed
        public bool Unsure { get; private set; }

        // After an approval, a take-down or a move back from Approved, beside its notice: the bot was asked to clear its
        // name cache, with the request's box, or why it wasn't
        public string? FollowUp { get; private set; }

        // The box for the request ?request= names, when it's one of this page's kinds
        public RequestSection? Result { get; private set; }

        // Announce now, or what stands in its place, with whether one more would be sent now, its form's key, and why the
        // last try was refused, with the request that names
        public AnnounceView? Announce { get; private set; }
        public AvailabilityLine? AnnounceAvailability { get; private set; }
        public string AnnounceKey { get; private set; } = "";
        public string? AnnounceProblem { get; private set; }
        public ulong? AnnounceRequest { get; private set; }

        // The list the reviewer came from, its tab and search, which Back to the list returns to
        public ArmorTab? FromTab { get; private set; }
        public string? FromSearch { get; private set; }

        public bool IsApproved => Armor?.KnownStatus == ArmorStatus.Approved;

        // Stored spelt differently from the website's word it matches, such as 'approved '
        public bool SpeltDifferently => Armor is { Status: not null, KnownStatus: not null } armor && armor.Status != armor.KnownStatus;

        // Approve is offered only when the rules allow it, and the server checks them again either way
        public bool CanApprove => Actions.Contains(ArmorAction.Approve) && Check?.CanApprove == true;

        // What stops an approval, sorted by what a reviewer can do about it: a name another approved armor has, a
        // screenshot that isn't an upload, and the rest, which only the website can fix
        public bool Blocks(ArmorProblemKind kind) => Check?.Blocking.Any(problem => problem.Kind == kind) == true;
        public bool BlocksOtherwise => Check?.Blocking.Any(problem => problem.Kind is not (ArmorProblemKind.NameTaken or ArmorProblemKind.ScreenshotNotAllowed)) == true;

        public bool ScreenshotShown => ArmorRules.IsAllowedScreenshot(Armor?.ArmorScreenshot);

        // The save string the bot will show once it's approved, when it has none stored
        public string? SaveString => Armor == null ? null : ArmorColors.CreateColorArray(Armor);

        // In the game's order, as the bot lists them
        public IReadOnlyList<(string Part, int? Color)> Colors => Armor == null ? [] :
        [
            ("Helmet", Armor.Helmet), ("Visor", Armor.Visor), ("Torso", Armor.Torso), ("Shoulders", Armor.Shoulders),
            ("Biceps", Armor.Biceps), ("Forearms", Armor.Forearms), ("Hands", Armor.Hands), ("Pelvis", Armor.Pelvis),
            ("Thighs", Armor.Thighs), ("Legs", Armor.Legs), ("Boots", Armor.Boots), ("Shields", Armor.Shields), ("Other", Armor.Other)
        ];

        // The list the reviewer came from, so an action that moved the armor to another tab doesn't take them out of the
        // queue they're working through. Otherwise the tab of the armor's status, or All for one the tabs don't have.
        public string ListLink => ArmorTabs.Link(FromTab ?? ArmorTabs.Of(Armor?.KnownStatus), FromSearch);

        // This page again, still carrying the list the reviewer came from
        public string PageLink => ArmorTabs.ReviewLink(Id, FromTab, FromSearch);

        // The approved armor with a name like this one's, through the list's search, which compares names the same way but
        // for characters the collation gives no weight. None for a name the search can't take.
        public string? NameSearchLink
        {
            get
            {
                var name = Armor?.ArmorName?.Trim();

                if (string.IsNullOrEmpty(name) || name.EnumerateRunes().Count() > ArmorListModel.SearchLimit)
                    return null;

                return ArmorTabs.Link(ArmorTabs.Of(ArmorStatus.Approved), name);
            }
        }

        // A screenshot address that isn't shown, as text cut short: the website's column has no limit
        public string ScreenshotText => Cut(Armor?.ArmorScreenshot ?? "", ScreenshotTextLimit);

        public const int ScreenshotTextLimit = 300;

        public async Task<IActionResult> OnGetAsync(long id, [FromQuery] string? done, [FromQuery] string? list, [FromQuery] string? q, [FromQuery] string? request,
            [FromQuery] string? unsent, [FromQuery] string? notice)
        {
            KeepList(list, q);

            // A form's notice: Announce now's, or an action's, named by its done word
            var found = HttpContext.RequestServices.GetRequiredService<FormNotices>().Find(this, notice, Forms);
            var acted = ArmorActions.FromDoneWord(found?.Form);

            // An action whose commit the database didn't confirm: why alone, with nothing read, since the database most often
            // still doesn't answer. Not the error page, which would say nothing was changed.
            if (found is { Unsure: true } && acted != null)
            {
                Id = id;
                Unsure = true;
                RequestWords.Answer(Response, found, time.GetUtcNow().UtcDateTime);
                return Page();
            }

            if (!await LoadAsync(id))
                return NotFound();

            var action = ArmorActions.FromDoneWord(done);
            Notice = action is { } taken ? DoneNotice(taken) : null;

            // Only chooses what to show, so anything but a request's number shows nothing
            if (ulong.TryParse(request, NumberStyles.None, CultureInfo.InvariantCulture, out var named) && named > 0)
                Result = await HttpContext.RequestServices.GetRequiredService<RequestBoxes>().SectionAsync(named, Kinds, null,
                    RequestBoxes.CheckAgain(BoxLink(action, named), Request.Query));

            // What became of the cache clear, beside the notice of an action that sends one, which shows only while the armor
            // still has the status that action gave it
            if (ArmorWords.ClearsCache(Notice))
                FollowUp = Result?.View.Kind == RequestKinds.CachesClear ? ArmorWords.CacheClearing : ArmorWords.CacheNotCleared(unsent);

            if (found == null)
                return Page();

            if (acted is { } refused)
            {
                // The status the action leads to is already the armor's, by this GET's reads, as DoneNotice guards ?done=: a
                // try the dashboard couldn't confirm was saved, or someone made the same change first. Nothing was done, but
                // nothing went wrong.
                if (found.State == ChangedState && Armor!.KnownStatus == refused.Target())
                {
                    Notice = ArmorWords.Already(refused.Target());
                    NoticeIsProblem = false;
                }
                else
                {
                    // What stops an approval is listed under Rules only while something does, by this GET's reads too: once
                    // the name has been freed, or the armor approved since, the words don't point there
                    Notice = found.State switch
                    {
                        ChangedState => ArmorWords.Changed,
                        BlockedState when Check is { Blocking.Count: > 0 } => ArmorWords.Blocked,
                        BlockedState => ArmorWords.WasBlocked,
                        PostedState => ArmorWords.WasPosted,
                        _ => ArmorWords.Unticked
                    };

                    NoticeIsProblem = true;
                }
            }
            else
            {
                // Announce now's: why at the top, and by the button too unless the armor itself was why, with the form's key
                // after a save the database didn't confirm in place of the new one LoadAsync drew. Once this GET draws no
                // form, as once a notification run has started since the approval, the words don't offer it, and once it
                // draws no reason below, they don't point to one.
                Notice = found switch
                {
                    { Unsure: true } when Announce?.State != AnnounceState.Offered => RequestWords.UnsureNotOffered,
                    { Words: ArmorWords.AnnounceNotNow } when Announce?.State is null or AnnounceState.Offered => ArmorWords.AnnounceWasNotOffered,
                    _ => found.Words
                };
                NoticeIsProblem = true;

                if (found.State != PageState)
                {
                    AnnounceProblem = found.Words;
                    AnnounceRequest = found.Request;

                    if (found.Key is { } key)
                        AnnounceKey = key;
                }
            }

            RequestWords.Answer(Response, found, time.GetUtcNow().UtcDateTime);
            return Page();
        }

        public Task<IActionResult> OnPostApproveAsync(long id, string? expected, string? version, string[]? ticks, [FromQuery] string? list, [FromQuery] string? q) =>
            ActAsync(id, ArmorAction.Approve, expected, version, null, ticks, list, q);

        public Task<IActionResult> OnPostRejectAsync(long id, string? expected, string? version, string? reason, [FromQuery] string? list, [FromQuery] string? q) =>
            ActAsync(id, ArmorAction.Reject, expected, version, reason, null, list, q);

        public Task<IActionResult> OnPostTakeDownAsync(long id, string? expected, string? version, [FromQuery] string? list, [FromQuery] string? q) =>
            ActAsync(id, ArmorAction.TakeDown, expected, version, null, null, list, q);

        public Task<IActionResult> OnPostBackToPendingAsync(long id, string? expected, string? version, [FromQuery] string? list, [FromQuery] string? q) =>
            ActAsync(id, ArmorAction.BackToPending, expected, version, null, null, list, q);

        // Runs the bot's notification job now, for armor that's approved. The bot refuses it while posts are paused or a run
        // is under way, and takes it once a minute, which the sender checks first. It doesn't refuse one that would be its
        // first run since a start, which takes the armor as seen, so the form of a page drawn before then is refused here.
        public async Task<IActionResult> OnPostAnnounceAsync(long id, string? formkey, [FromQuery] string? list, [FromQuery] string? q)
        {
            KeepList(list, q);

            if (!await LoadAsync(id))
                return NotFound();

            // Refused before the sender, as things stand, with the reason the page then shows
            if (!IsApproved)
                return Keep(id, new FormNotice { Form = AnnounceForm, Status = StatusCodes.Status409Conflict, Words = ArmorWords.NotApprovedNow, State = PageState });

            if (Announce is { State: AnnounceState.Restarted or AnnounceState.Stopped })
                return Keep(id, new FormNotice { Form = AnnounceForm, Status = StatusCodes.Status409Conflict, Words = ArmorWords.AnnounceNotNow, State = PageState });

            var outcome = await HttpContext.RequestServices.GetRequiredService<RequestSender>()
                .SendAsync(new SendOrder(RequestKinds.JobNotifications, formkey), HttpContext);

            switch (outcome)
            {
                // Its own address, so reloading shows the box rather than sending the form again
                case SendOutcome.Sent sent:
                    return RedirectToPage(null, null, new { id, request = sent.Id, list = FromTab?.Word, q = FromSearch }, "result");

                case SendOutcome.Refused { Why: SendRefusal.Forbidden }:
                    return Forbid();

                case SendOutcome.Refused { Why: SendRefusal.MadeUp }:
                    return BadRequest();
            }

            // Anything else is a notice this page's GET draws, with why at the top and by the button, and the form's key
            // after a save the database didn't confirm
            return Keep(id, FormNotice.Of(AnnounceForm, outcome));
        }

        // A request no handler takes, such as a POST without one or a PUT, would otherwise get the page with nothing loaded
        public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
        {
            if (context.HandlerMethod == null)
                context.Result = BadRequest();
        }

        async Task<IActionResult> ActAsync(long id, ArmorAction action, string? expected, string? version, string? reason, string[]? ticks, string? list, string? q)
        {
            // Every field is checked before the database, so a form someone made up gets a 400, never an error the data
            // layer throws for a status, a reason or a change it can't save
            if (!ArmorForm.TryExpected(expected, out var seenStatus) || !action.StartsFrom(seenStatus) || !ArmorVersion.IsWellFormed(version)
                || !ArmorForm.TryReason(reason, out var why) || !ArmorForm.TryTicks(ticks, out var ticked))
                return BadRequest();

            KeepList(list, q);

            var outcome = await HttpContext.RequestServices.GetRequiredService<ArmorReviews>()
                .ApplyAsync(new ArmorReviewRequest(id, action, seenStatus, version!, why, ticked), HttpContext);

            switch (outcome)
            {
                // Its own address, so reloading the page shows it rather than sending the form again. The cache clear comes
                // after the commit, never inside the review's transaction, and whatever becomes of it, the review stands.
                case ArmorReviewOutcome.Done:
                    var clear = ArmorFollowUps.ClearsCaches(action, seenStatus) ? await ArmorFollowUps.ClearCachesAsync(HttpContext, logger) : new CacheClear(null, null);
                    return RedirectToPage(null, null, new { id, done = action.DoneWord(), request = clear.Request, unsent = clear.Unsent, list = FromTab?.Word, q = FromSearch },
                        clear.Request != null ? "result" : null);

                case ArmorReviewOutcome.Missing:
                    return NotFound();

                // Not the error page, which would say nothing was changed
                case ArmorReviewOutcome.Unsure:
                    return Keep(id, new FormNotice { Form = action.DoneWord(), Status = StatusCodes.Status503ServiceUnavailable, Unsure = true });
            }

            // A refusal: a notice this page's GET draws, with the armor as it is then, and what stopped the action. Nothing
            // is read here: the GET decides from its own reads whether the armor already has the status the action was for,
            // and whether what stopped an approval still does.
            return Keep(id, new FormNotice
            {
                Form = action.DoneWord(),
                Status = StatusCodes.Status409Conflict,
                State = outcome switch
                {
                    ArmorReviewOutcome.Changed => ChangedState,
                    ArmorReviewOutcome.Blocked => BlockedState,
                    ArmorReviewOutcome.Posted => PostedState,
                    _ => UntickedState
                }
            });
        }

        // This page again with a form's notice, still carrying the list the reviewer came from
        IActionResult Keep(long id, FormNotice notice) =>
            HttpContext.RequestServices.GetRequiredService<FormNotices>().Keep(this, notice, new { id, list = FromTab?.Word, q = FromSearch });

        // The whole row, since lists cut the screenshot, and the name check, which only this page makes. Approved armor is
        // checked too, so a name two approved armor already share shows here.
        async Task<bool> LoadAsync(long id)
        {
            Id = id;

            var armor = await DatabaseCalls.Guard(() => submissions.GetSubmission(id), logger);

            if (armor == null)
                return false;

            var nameTaken = await DatabaseCalls.Guard(() => submissions.IsApprovedNameTaken(armor.ArmorName ?? "", id), logger);
            var history = await DatabaseCalls.Guard(() => reviews.ListReviews(id), logger);

            Armor = armor;
            Check = ArmorRules.Check(armor, nameTaken);
            Version = ArmorVersion.Of(armor);
            StatusUnknown = !ArmorActions.TryStatusOf(armor, out var status);
            Actions = StatusUnknown ? [] : ArmorActions.From(status);
            History = history;
            Post = BotMessages.NewArmorPost(armor);
            Card = BotMessages.LookupCard(armor);

            var bot = await ReadBotAsync();
            KeepsPosts = PostingRules.KeepsPosts(bot);
            await LoadRecordAsync(id, status);

            // A bot that keeps a record may post armor it has found again only where its post still waited when it was
            // withdrawn, and one whose record can't be read may have found it. An older bot's, from the history.
            MayNotPost = KeepsPosts ? RecordUnknown || Record?.Seen != null : MayNotBePosted(status, history);

            BackToPendingOffered = Actions.Contains(ArmorAction.BackToPending) && (status != ArmorStatus.Approved || Record is { } record && !ArmorPosts.Posted(record));

            if (Actions.Contains(ArmorAction.BackToPending) && !BackToPendingOffered)
                BackToPendingInstead = Record is not { } posted ? ArmorWords.PostsUnknownNoBack
                    : posted.Seen is { state: SeenState.Baseline } && ArmorPosts.PostedServers(posted) == 0 ? ArmorWords.CountedAsPosted
                    : ArmorWords.PostedIn(ArmorPosts.PostedServers(posted));

            await LoadAnnounceAsync(bot);
            return true;
        }

        // The bot's status row, for which bot runs, and for Announce now
        Task<BotStatusRow?> ReadBotAsync() => DatabaseCalls.Guard(() => status.GetStatus(), logger);

        // MinerBot's record of where the armor went, with the servers' names from the bot's copy, on its own: a read that
        // fails leaves the rest of the page, and Back to pending from Approved isn't offered then
        async Task LoadRecordAsync(long id, string? armorStatus)
        {
            try
            {
                var record = await DatabaseCalls.Guard(() => deliveries.GetItemPosts(PostSource.Armor, (ulong)id), logger);
                var names = record.Servers.Count == 0 ? new Dictionary<ulong, string>()
                    : (await DatabaseCalls.Guard(() => guilds.ListGuilds(), logger)).ToDictionary(guild => guild.guild_id, guild => guild.name);

                Record = record;
                PostLines = ArmorPosts.Lines(record, names, time.GetUtcNow().UtcDateTime);
            }
            catch (DashboardUnavailableException)
            {
                RecordUnknown = true;
            }

            if (!KeepsPosts)
                return;

            // Armor it has found, or approved armor it will find; a record that can't be read is said to be so
            PostsLine = Record switch
            {
                null => ArmorWords.PostsUnknown,
                { Seen: { state: SeenState.Baseline } seen } => ArmorWords.Baseline(seen.first_seen_at),
                { Seen: { } seen, Servers.Count: 0 } => ArmorWords.NoServerOwed(seen.first_seen_at),
                { Seen: not null } => ArmorWords.PostsIntro,
                _ when armorStatus == ArmorStatus.Approved => ArmorWords.NotPostedYet,
                _ => null
            };

            ShowsPosts = PostsLine != null;
        }

        // Announce now, read only for approved armor the bot may post whose latest review approved it: the bot's settings,
        // the notification job's last run, the bot's status row for when its run started, and, for the button, what the
        // sender's limits would say
        async Task LoadAnnounceAsync(BotStatusRow? bot)
        {
            if (ArmorFollowUps.Approval(Armor?.KnownStatus, History, MayNotPost) is not { } approval)
                return;

            var job = RequestKinds.Find(RequestKinds.JobNotifications)!;
            var stored = await DatabaseCalls.Guard(() => queue.GetSettings(), logger);
            var jobs = await DatabaseCalls.Guard(() => status.ListJobs(), logger);
            var requestsOn = RequestSigning.ParseKey(HttpContext.RequestServices.GetRequiredService<IOptions<DashboardOptions>>().Value.RequestSigningKey) != null;

            Announce = ArmorFollowUps.Announce(approval, requestsOn, stored, jobs.FirstOrDefault(row => row.job == job.Job), bot);
            AnnounceKey = RequestFields.NewKey();

            if (Announce is { State: AnnounceState.Offered } && RequestActors.Of(HttpContext) is { } actor)
            {
                var now = time.GetUtcNow().UtcDateTime;
                AnnounceAvailability = (await LimitReads.LoadAsync(requests, actor.Id, now, logger)).Line(job, null, now);
            }
        }

        // This page with the box for the request named, keeping the action's word, so its notice still shows, and the list,
        // without a fragment: Check again's address, to which RequestBoxes.CheckAgain adds again=1, or takes it off
        string BoxLink(ArmorAction? action, ulong request)
        {
            var query = new QueryBuilder();

            if (action is { } done)
                query.Add("done", done.DoneWord());

            query.Add("request", request.ToString(CultureInfo.InvariantCulture));

            if (FromTab != null)
            {
                query.Add("list", FromTab.Word);

                if (!string.IsNullOrEmpty(FromSearch))
                    query.Add("q", FromSearch);
            }

            return "/admin/armor/" + Id.ToString(CultureInfo.InvariantCulture) + query.ToQueryString();
        }

        // What an action did, on the page its form leads to, only while the armor still has the status the action gave it,
        // so a reload after someone else's change, or a link someone made, can't say something happened that didn't. The
        // latest review is then the action's own, when the dashboard made the change.
        string? DoneNotice(ArmorAction action)
        {
            if (Armor?.KnownStatus != action.Target())
                return null;

            var latest = History.FirstOrDefault();

            return action switch
            {
                ArmorAction.Approve when KeepsPosts => MayNotPost ? ArmorWords.ApprovedAgainKept : ArmorWords.ApprovedKept,
                ArmorAction.Approve => MayNotPost ? ArmorWords.ApprovedAgain : ArmorWords.Approved,
                ArmorAction.Reject => latest?.reason != null ? ArmorWords.RejectedWithReason : ArmorWords.Rejected,
                ArmorAction.TakeDown => ArmorWords.TakenDown,
                _ when KeepsPosts => latest?.from_status == ArmorStatus.Approved ? ArmorWords.MovedBackFromApprovedKept : ArmorWords.MovedBackToPendingKept,
                _ => latest?.from_status == ArmorStatus.Approved ? ArmorWords.MovedBackFromApproved : ArmorWords.MovedBackToPending
            };
        }

        // The bot posts an armor only the first time it sees it approved since it last started, so armor approved before,
        // as far as the history shows, or coming from Not Approved, which a take-down leaves, may not be posted. For
        // approved armor it's the approval that made it so that counts, the latest review when the dashboard made it.
        static bool MayNotBePosted(string? status, IReadOnlyList<ArmorReview> history)
        {
            static bool Approval(ArmorReview review) => review.from_status == ArmorStatus.Approved || review.to_status == ArmorStatus.Approved;

            if (status == ArmorStatus.Approved && history.FirstOrDefault() is { to_status: ArmorStatus.Approved } latest)
                return latest.from_status == ArmorStatus.NotApproved || history.Skip(1).Any(Approval);

            return status == ArmorStatus.NotApproved || history.Any(Approval);
        }

        // Checked as the list checks them, but a word or a search the list wouldn't take is left out rather than refused,
        // since it only decides where Back to the list goes
        void KeepList(string? list, string? q)
        {
            FromTab = string.IsNullOrEmpty(list) ? null : ArmorTabs.FromWord(list);

            var search = (q ?? "").Trim();
            FromSearch = FromTab != null && search.Length > 0 && search.EnumerateRunes().Count() <= ArmorListModel.SearchLimit ? search : null;
        }

        // Never half a character
        static string Cut(string text, int limit)
        {
            if (text.Length <= limit)
                return text;

            var length = char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
            return text[..length] + "…";
        }
    }
}
