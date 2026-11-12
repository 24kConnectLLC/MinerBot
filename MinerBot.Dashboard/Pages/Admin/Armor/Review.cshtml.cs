using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Armor;
using MinerBot.Dashboard.Security;
using MinerBot.Shared.Armor;

namespace MinerBot.Dashboard.Pages.Admin.Armor
{
    // One armor submission with everything a reviewer needs to decide: its text with the hidden tricks shown, its
    // screenshot when it's an upload on the website, its colours, how the bot will show it, its history, and the rules.
    // Its forms approve, reject, take down or move it back to pending, each through ArmorReviews.
    public sealed class ArmorReviewModel(IArmorSubmissionData submissions, IArmorReviewData reviews, ILogger<ArmorReviewModel> logger) : PageModel
    {
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

        // A message for the top of the page, from ArmorWords' fixed set, and whether it says why nothing was done
        public string? Notice { get; private set; }
        public bool NoticeIsProblem { get; private set; }

        // The database stopped answering while a change was being committed, which may or may not have landed
        public bool Unsure { get; private set; }

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

        public async Task<IActionResult> OnGetAsync(long id, [FromQuery] string? done, [FromQuery] string? list, [FromQuery] string? q)
        {
            KeepList(list, q);

            if (!await LoadAsync(id))
                return NotFound();

            Notice = ArmorActions.FromDoneWord(done) is { } action ? DoneNotice(action) : null;
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
                // Its own address, so reloading the page shows it rather than sending the form again
                case ArmorReviewOutcome.Done:
                    return RedirectToPage(new { id, done = action.DoneWord(), list = FromTab?.Word, q = FromSearch });

                case ArmorReviewOutcome.Missing:
                    return NotFound();

                // Not the error page, which would say nothing was changed
                case ArmorReviewOutcome.Unsure:
                    Id = id;
                    Unsure = true;
                    Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return Page();
            }

            // A refusal: the page again, with the armor as it is now, and what stopped the action
            if (!await LoadAsync(id))
                return NotFound();

            // The status this action leads to is already the armor's: a try the dashboard couldn't confirm was saved, or
            // someone made the same change first. Nothing was done, but nothing went wrong.
            if (outcome == ArmorReviewOutcome.Changed && Armor!.KnownStatus == action.Target())
            {
                Notice = ArmorWords.Already(action.Target());
            }
            else
            {
                Notice = outcome switch
                {
                    ArmorReviewOutcome.Changed => ArmorWords.Changed,
                    ArmorReviewOutcome.Blocked => ArmorWords.Blocked,
                    _ => ArmorWords.Unticked
                };

                NoticeIsProblem = true;
            }

            Response.StatusCode = StatusCodes.Status409Conflict;
            return Page();
        }

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
            MayNotPost = MayNotBePosted(status, history);
            Post = BotMessages.NewArmorPost(armor);
            Card = BotMessages.LookupCard(armor);
            return true;
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
                ArmorAction.Approve => MayNotPost ? ArmorWords.ApprovedAgain : ArmorWords.Approved,
                ArmorAction.Reject => latest?.reason != null ? ArmorWords.RejectedWithReason : ArmorWords.Rejected,
                ArmorAction.TakeDown => ArmorWords.TakenDown,
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
