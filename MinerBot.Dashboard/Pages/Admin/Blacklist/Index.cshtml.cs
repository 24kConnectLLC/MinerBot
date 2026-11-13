using System.Globalization;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Blacklist;
using DataAccessLibrary.Blacklist.Models;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Blacklist;
using MinerBot.Dashboard.Security;
using MinerBot.Shared.Steam;

namespace MinerBot.Dashboard.Pages.Admin.Blacklist
{
    // The latest change the dashboard made to an entry: who, in what role, and when, in UTC
    public sealed record BlacklistChange(string? ActorName, string Role, DateTime At);

    // One entry of the list, as the table holds it. Expired says its end has passed, by UTC, and LastChange is null for an
    // entry the dashboard never changed, such as one the website added.
    public sealed record BlacklistRow(uint Account, string? Name, string? Reason, DateTime? Expires, bool ExpiresUnreadable, bool Expired,
        BlacklistChange? LastChange)
    {
        public string Shown => SteamAccounts.Shown(Account);
        public string Steam3 => SteamAccounts.Steam3(Account);
        public string Link => SteamAccounts.EntryLink(Account);
    }

    // A row whose number no account can be, with the number as its form sends it back and the version of what it shows
    public sealed record NumberRow(string Number, string? Name, string? Reason, DateTime? Expires, bool ExpiresUnreadable, string? Notes, string Version);

    // The blacklist, 25 to a page, with one search by SteamID or name, and the rows no list can show, which can only be
    // removed. Everything the address carries is checked here first, because the data layer throws for a page it can't use.
    public sealed class BlacklistListModel(IBlacklistData blacklist, IAuditLogData audit, TimeProvider time, ILogger<BlacklistListModel> logger) : PageModel
    {
        public const int PageSize = 25;

        // More than any way of writing a SteamID needs, and more than a name holds
        public const int SearchLimit = BlacklistForm.SteamIdLimit;

        public string Search { get; private set; } = "";

        // The search is a custom profile link, which only Steam can turn into an account, so it was looked for in names
        public bool SearchIsCustomLink { get; private set; }

        public int PageNumber { get; private set; } = 1;
        public long Total { get; private set; }
        public IReadOnlyList<BlacklistRow> Rows { get; private set; } = [];
        public IReadOnlyList<NumberRow> Numbers { get; private set; } = [];

        // A message for the top of the page, from BlacklistWords' fixed set, and whether it says why nothing was done
        public string? Notice { get; private set; }
        public bool NoticeIsProblem { get; private set; }

        // The database stopped answering while a row's removal was being committed
        public bool Unsure { get; private set; }

        public long PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);

        public string Summary
        {
            get
            {
                var count = Total.ToString("N0", CultureInfo.InvariantCulture);
                var entries = Total == 1 ? "entry" : "entries";

                return Search.Length > 0
                    ? $"{count} {entries} that {(Total == 1 ? "matches" : "match")} the search, by name."
                    : $"{count} {entries}, by name.";
            }
        }

        // From the query alone: Razor Pages keeps the page's own name in a route value called page, which would win
        public async Task<IActionResult> OnGetAsync([FromQuery] string? q, [FromQuery(Name = "page")] string? page, [FromQuery] string? done, [FromQuery] string? number)
        {
            var search = (q ?? "").Trim();

            // Only an address someone typed can be longer: the box stops at the limit
            if (search.EnumerateRunes().Count() > SearchLimit)
                return BadRequest();

            Search = search;

            // Pages count from 1 here and from 0 in the data layer. Anything that isn't a page number is the first page.
            PageNumber = int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 1 ? parsed : 1;

            await LoadAsync();

            // Only while no such row is left, so a link someone made can't say a row went that's still there
            if (BlacklistActions.FromDoneWord(done) == BlacklistAction.Remove && BlacklistForm.TryNumber(number, out var removed)
                && await DatabaseCalls.Guard(() => blacklist.GetOutOfRangeBan(removed), logger) == null)
                Notice = BlacklistWords.NumberRemoved;

            return Page();
        }

        public async Task<IActionResult> OnPostRemoveNumberAsync(string? number, string? version)
        {
            // Only a form someone made up sends anything else, and the data layer would throw for some of it
            if (!BlacklistForm.TryNumber(number, out var parsed) || !BlacklistVersion.IsWellFormed(version))
                return BadRequest();

            var outcome = await HttpContext.RequestServices.GetRequiredService<BlacklistChanges>().RemoveNumberAsync(parsed, version!, HttpContext);

            switch (outcome)
            {
                // Its own address, so reloading the page shows it rather than sending the form again
                case BlacklistOutcome.Done:
                    return RedirectToPage(new { done = BlacklistAction.Remove.DoneWord(), number = BlacklistForm.NumberText(parsed) });

                // Not the error page, which would say nothing was changed
                case BlacklistOutcome.Unsure:
                    Unsure = true;
                    Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return Page();
            }

            // A refusal: the list again, as it is now. A row that's gone was removed already, by an earlier try the
            // dashboard couldn't confirm or by someone else, which isn't a problem.
            await LoadAsync();

            NoticeIsProblem = outcome != BlacklistOutcome.Gone;
            Notice = NoticeIsProblem ? BlacklistWords.NumberChanged : BlacklistWords.NumberAlreadyRemoved;
            Response.StatusCode = StatusCodes.Status409Conflict;
            return Page();
        }

        // A request no handler takes, such as a POST without one or a PUT, would otherwise get the page as if it were a GET
        public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
        {
            if (context.HandlerMethod == null)
                context.Result = BadRequest();
        }

        // A link to this list at a page, the search kept unless it's cleared, and the first page unless one is given
        public string Link(int? page = null, bool keepSearch = true)
        {
            var query = new QueryBuilder();

            if (keepSearch && Search.Length > 0)
                query.Add("q", Search);

            if ((page ?? 1) > 1)
                query.Add("page", page!.Value.ToString(CultureInfo.InvariantCulture));

            return "/admin/blacklist" + query.ToQueryString();
        }

        async Task LoadAsync()
        {
            // A SteamID in any form is searched as an account number and as part of a name, since either is enough. A
            // custom profile link, or anything else, is a name only.
            var parsed = Search.Length > 0 ? SteamIds.Parse(Search) : null;
            uint? account = parsed is { Kind: SteamIdKind.AccountNumber } ? parsed.AccountNumber : null;
            var name = Search.Length > 0 ? Search : null;

            SearchIsCustomLink = parsed is { Kind: SteamIdKind.NeedsLookup };

            var found = await DatabaseCalls.Guard(() => blacklist.SearchBlacklist(account, name, PageNumber - 1, PageSize), logger);

            // One call for the page's entries, and none for an empty page
            var targets = found.Rows.Select(entry => SteamAccounts.TargetId(entry.steam_id)).ToList();
            IReadOnlyList<AuditEntry> changes = targets.Count == 0 ? [] : await DatabaseCalls.Guard(() => audit.LatestChanges(AuditWords.BlacklistTarget, targets), logger);
            var latest = changes.Where(change => change.target_id != null).ToDictionary(change => change.target_id!, StringComparer.Ordinal);

            var numbers = await DatabaseCalls.Guard(() => blacklist.ListOutOfRangeBans(), logger);
            var now = time.GetUtcNow().UtcDateTime;

            Total = found.Total;
            Rows = found.Rows.Select(entry => Row(entry, latest.GetValueOrDefault(SteamAccounts.TargetId(entry.steam_id)), now)).ToList();
            Numbers = numbers.Select(row => new NumberRow(BlacklistForm.NumberText(row.steam_id), row.username, row.reason, row.expire_date,
                row.expire_date_unreadable, row.notes, BlacklistVersion.Of(row))).ToList();
        }

        static BlacklistRow Row(BlacklistModel entry, AuditEntry? change, DateTime now) => new(
            entry.steam_id,
            entry.username,
            entry.reason,
            entry.expire_date,
            entry.expire_date_unreadable,
            IsExpired(entry.expire_date, now),
            change == null ? null : new BlacklistChange(change.actor_name, AuditWords.RoleLabel(change.actor_role), DateTime.SpecifyKind(change.at, DateTimeKind.Utc)));

        // The table's end dates are read as UTC, and the entry no longer applies from that moment on
        internal static bool IsExpired(DateTime? expires, DateTime now) => expires is { } end && DateTime.SpecifyKind(end, DateTimeKind.Utc) <= now;
    }
}
