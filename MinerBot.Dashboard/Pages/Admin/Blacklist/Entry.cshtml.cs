using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Blacklist;
using DataAccessLibrary.Blacklist.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Blacklist;
using MinerBot.Dashboard.Security;

namespace MinerBot.Dashboard.Pages.Admin.Blacklist
{
    // One account's entry: every way of writing the account, the entry as the table holds it, its history on the
    // dashboard, and the forms that change or remove it, each through BlacklistChanges. An account that's no longer on the
    // blacklist still has a page while it has history.
    public sealed class BlacklistEntryModel(IBlacklistData blacklist, IAuditLogData audit, TimeProvider time, ILogger<BlacklistEntryModel> logger) : PageModel
    {
        // The history's latest rows. The audit page has the rest.
        public const int HistoryLimit = 25;

        public uint Account { get; private set; }

        // Null when the account isn't on the blacklist
        public BlacklistModel? Entry { get; private set; }

        // BlacklistVersion's value for the entry as this page shows it, which the remove form sends back
        public string Version { get; private set; } = "";

        // What the edit form sends back: the entry's version, or, when a field was refused, the version that form came
        // with. Its boxes still hold what the editor saw then, so a save from them applies only while the entry is as they
        // saw it, never over a change someone made since.
        public string EditVersion { get; private set; } = "";

        // Its end has passed, by UTC
        public bool Expired { get; private set; }

        // What the edit form's boxes hold: the entry's values, or what was sent when a field was refused
        public BlacklistFormValues Form { get; private set; } = BlacklistFormValues.Empty;

        // Never with addresses, for anyone: the audit page is where the Owner sees them
        public AuditTable History { get; private set; } = new([], false);
        public long HistoryTotal { get; private set; }

        // A message for the top of the page, from BlacklistWords' fixed set, and whether it says why nothing was done
        public string? Notice { get; private set; }
        public bool NoticeIsProblem { get; private set; }

        // The database stopped answering while a change was being committed, which may or may not have landed
        public bool Unsure { get; private set; }

        public string? SteamId64 => SteamAccounts.SteamId64(Account);
        public string Steam3 => SteamAccounts.Steam3(Account);
        public string Steam2 => SteamAccounts.Steam2(Account);
        public string? ProfileLink => SteamAccounts.ProfileLink(Account);
        public string PageLink => SteamAccounts.EntryLink(Account);

        // The whole history, on the audit page
        public string AuditLink => "/admin/audit?type=" + AuditWords.BlacklistTarget + "&id=" + SteamAccounts.TargetId(Account);

        // The add form with this account in its box. Account 0 can't be added, so it has none.
        public string? AddLink => Account == 0 ? null : "/admin/blacklist/add?steamid=" + Uri.EscapeDataString(Steam3);

        public async Task<IActionResult> OnGetAsync(long account, [FromQuery] string? done)
        {
            if (!IsAccount(account) || !await LoadAsync((uint)account))
                return NotFound();

            Notice = BlacklistActions.FromDoneWord(done) is { } action ? DoneNotice(action) : null;
            return Page();
        }

        public async Task<IActionResult> OnPostEditAsync(long account, string? expires, string? version)
        {
            if (!IsAccount(account))
                return NotFound();

            // Only a form someone made up sends a version this page didn't make
            if (!BlacklistVersion.IsWellFormed(version))
                return BadRequest();

            var number = (uint)account;

            // The text boxes exactly as the browser sent them. Model binding would hand over a box of only spaces as no
            // text, the same as a box the editor cleared, and only the first keeps the stored text.
            var form = Request.HasFormContentType ? await Request.ReadFormAsync(HttpContext.RequestAborted) : null;
            var name = Box(form, "name");
            var reason = Box(form, "reason");
            var notes = Box(form, "notes");

            // The entry as it is now, so its own texts pass the checks as a box sends them back, and its end's own day
            // whatever its year
            var stored = await DatabaseCalls.Guard(() => blacklist.GetBannedPlayer(number), logger);
            var passed = BlacklistForm.TryFields(name, reason, expires, notes, stored, out var fields, out var problems);

            // A form from before the entry changed, or was removed, goes on to EditAsync, which refuses it on its version and
            // records that. The checks here can blame the editor for a box they left alone, whose text or day the entry no
            // longer has. EditAsync never writes a text the checks refuse unless it's the locked row's own, nor fields whose
            // day was refused, which hold no end date in its place. A current form whose entry is gone by the time the page
            // would be shown goes on too, and EditAsync answers it on the row it locks.
            var stale = stored == null || BlacklistVersion.Of(stored) != version;
            var answerHere = !passed && !stale;

            // The page again, with what was sent kept in the boxes, its version with them, and a message by each field that
            // failed. The details above show the entry as it is now, which the remove form's version matches.
            if (answerHere && await LoadAsync(number) && Entry != null)
            {
                Form = BlacklistFormValues.Sent(Entry, name, reason, expires, notes, problems);
                EditVersion = version!;
                Notice = BlacklistWords.NotSaved;
                NoticeIsProblem = true;
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return Page();
            }

            var outcome = await Changes().EditAsync(number, version!, fields, HttpContext);

            if (Settled(outcome, number, BlacklistAction.Edit) is { } settled)
                return settled;

            // The page again, with the entry as it is now
            if (!await LoadAsync(number))
                return NotFound();

            if (outcome == BlacklistOutcome.Unchanged)
            {
                Notice = BlacklistWords.Unchanged;
                return Page();
            }

            // The entry already has what this edit would make it: a try the dashboard couldn't confirm was saved, or
            // someone made the same change first. Nothing was done, but nothing went wrong.
            if (outcome == BlacklistOutcome.Changed && Entry != null && BlacklistChanges.Has(Entry, fields))
            {
                Notice = BlacklistWords.AlreadySaved;
            }
            else
            {
                Notice = outcome == BlacklistOutcome.Gone ? BlacklistWords.GoneSinceOpened : BlacklistWords.Changed;
                NoticeIsProblem = true;
            }

            Response.StatusCode = StatusCodes.Status409Conflict;
            return Page();
        }

        // Removing needs no typed confirmation: it can be undone by adding the entry again, and its history keeps what it held
        public async Task<IActionResult> OnPostRemoveAsync(long account, string? version)
        {
            if (!IsAccount(account))
                return NotFound();

            if (!BlacklistVersion.IsWellFormed(version))
                return BadRequest();

            var number = (uint)account;
            var outcome = await Changes().RemoveAsync(number, version!, HttpContext);

            if (Settled(outcome, number, BlacklistAction.Remove) is { } settled)
                return settled;

            if (!await LoadAsync(number))
                return NotFound();

            // Gone already: by an earlier try the dashboard couldn't confirm, or by someone else, which is what was asked
            NoticeIsProblem = outcome != BlacklistOutcome.Gone;
            Notice = NoticeIsProblem ? BlacklistWords.RemoveChanged : BlacklistWords.AlreadyRemoved;
            Response.StatusCode = StatusCodes.Status409Conflict;
            return Page();
        }

        // A request no handler takes, such as a POST without one or a PUT, would otherwise get the page with nothing loaded
        public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
        {
            if (context.HandlerMethod == null)
                context.Result = BadRequest();
        }

        // The route takes any long, and the table's account numbers are the uints
        static bool IsAccount(long account) => account is >= 0 and <= uint.MaxValue;

        // A box's first value as it was sent, or null when the form has none
        static string? Box(IFormCollection? form, string key) =>
            form != null && form.TryGetValue(key, out var values) && values.Count > 0 ? values[0] : null;

        BlacklistChanges Changes() => HttpContext.RequestServices.GetRequiredService<BlacklistChanges>();

        // A change that went through goes to the entry's own address, so reloading shows it rather than sending the form
        // again. One the database didn't confirm gets this page saying so, not the error page, which would say nothing
        // was changed. Anything else is a refusal, which the caller shows.
        IActionResult? Settled(BlacklistOutcome outcome, uint account, BlacklistAction action)
        {
            switch (outcome)
            {
                case BlacklistOutcome.Done:
                    return RedirectToPage(new { account = (long)account, done = action.DoneWord() });

                case BlacklistOutcome.Unsure:
                    Account = account;
                    Unsure = true;
                    Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return Page();

                default:
                    return null;
            }
        }

        // The entry and its history. False for an account with neither, which is a 404 like any page that isn't there.
        async Task<bool> LoadAsync(uint account)
        {
            Account = account;

            var entry = await DatabaseCalls.Guard(() => blacklist.GetBannedPlayer(account), logger);
            var history = await DatabaseCalls.Guard(() => audit.ListAudit(new AuditQuery
            {
                TargetType = AuditWords.BlacklistTarget,
                TargetId = SteamAccounts.TargetId(account),
                PageSize = HistoryLimit
            }), logger);

            if (entry == null && history.Total == 0)
                return false;

            Entry = entry;
            Version = entry == null ? "" : BlacklistVersion.Of(entry);
            EditVersion = Version;
            Expired = BlacklistListModel.IsExpired(entry?.expire_date, time.GetUtcNow().UtcDateTime);
            Form = entry == null ? BlacklistFormValues.Empty : BlacklistFormValues.Of(entry);
            History = AuditRows.Table(history.Rows, showIp: false);
            HistoryTotal = history.Total;
            return true;
        }

        // What an action did, on the page its form leads to, only while the entry is as the action left it, so a reload
        // after someone else's change, or a link someone made, can't say something happened that didn't
        string? DoneNotice(BlacklistAction action) => action switch
        {
            BlacklistAction.Add when Entry != null => BlacklistWords.Added,
            BlacklistAction.Edit when Entry != null => BlacklistWords.Saved,
            BlacklistAction.Remove when Entry == null => BlacklistWords.Removed,
            _ => null
        };
    }
}
