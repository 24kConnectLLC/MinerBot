using System.Globalization;
using DataAccessLibrary;
using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Armor;
using MinerBot.Dashboard.Security;
using MinerBot.Shared.Armor;

namespace MinerBot.Dashboard.Pages.Admin.Armor
{
    // One row of the list. Its markers come from the approval rules without the name check, which only the review page
    // makes, so a row with none isn't said to have no problems. KnownStatus is the website's word the table reads Status
    // as, and NameBlank says the rules find nothing in the name to show, so its link needs words of its own.
    public sealed record ArmorRow(long Id, string? Name, bool NameBlank, string Author, string? Status, string? KnownStatus, DateTime Sent,
        IReadOnlyList<string> Blocking, IReadOnlyList<string> Warnings);

    // The armor submissions, one status to a tab, 25 to a page, with a search by name or author. Everything the address
    // carries is checked here first, because the data layer throws for a status or a page it can't use.
    public sealed class ArmorListModel(IArmorSubmissionData submissions, ILogger<ArmorListModel> logger) : PageModel
    {
        public const int PageSize = 25;

        // The longest name or author the website's table holds, so a longer search can't find anything
        public const int SearchLimit = ArmorRules.NameLimit;

        public ArmorTab Tab { get; private set; } = ArmorTabs.Default;
        public string Search { get; private set; } = "";
        public int PageNumber { get; private set; } = 1;
        public long Total { get; private set; }
        public IReadOnlyList<ArmorRow> Rows { get; private set; } = [];

        public long PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);

        public string Summary
        {
            get
            {
                var count = Total.ToString("N0", CultureInfo.InvariantCulture);
                var matching = Search.Length > 0 ? " that match the search" : "";

                return Tab.Status switch
                {
                    ArmorStatus.Pending => $"{count} armor waiting for a review{matching}, oldest first.",
                    ArmorStatus.Approved => $"{count} approved armor{matching}, newest first.",
                    ArmorStatus.NotApproved => $"{count} armor not approved{matching}, newest first.",
                    _ => $"{count} armor in all{matching}, newest first."
                };
            }
        }

        public static IReadOnlyList<ArmorTab> Tabs => ArmorTabs.All;

        // From the query alone: Razor Pages keeps the page's own name in a route value called page, which would win
        public async Task<IActionResult> OnGetAsync([FromQuery] string? status, [FromQuery] string? q, [FromQuery(Name = "page")] string? page)
        {
            if (ArmorTabs.FromWord(status) is not { } tab)
                return NotFound();

            var search = (q ?? "").Trim();

            // Only an address someone typed can be longer: the box stops at the limit
            if (search.EnumerateRunes().Count() > SearchLimit)
                return BadRequest();

            // Pages count from 1 here and from 0 in the data layer. Anything that isn't a page number is the first page.
            var number = int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 1 ? parsed : 1;

            Tab = tab;
            Search = search;
            PageNumber = number;

            var found = await DatabaseCalls.Guard(() => search.Length == 0
                ? submissions.ListByStatus(tab.Status, number - 1, PageSize)
                : submissions.Search(search, tab.Status, number - 1, PageSize), logger);

            Total = found.Total;
            Rows = found.Rows.Select(Row).ToList();
            return Page();
        }

        // Nothing here takes a form, and a request no handler takes would otherwise get the page as if it were a GET
        public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
        {
            if (context.HandlerMethod == null)
                context.Result = BadRequest();
        }

        // A link to this list with the tab and the page given, the search kept unless it's cleared, and the first page
        // unless one is given
        public string Link(ArmorTab? tab = null, int? page = null, bool keepSearch = true)
        {
            var query = new QueryBuilder { { "status", (tab ?? Tab).Word } };

            if (keepSearch && Search.Length > 0)
                query.Add("q", Search);

            if ((page ?? 1) > 1)
                query.Add("page", page!.Value.ToString(CultureInfo.InvariantCulture));

            return "/admin/armor" + query.ToQueryString();
        }

        // An armor's page, which carries this tab and search, so its Back to the list comes back here, even after an action
        // has moved the armor to another tab
        public string ReviewLink(ArmorRow row) => ArmorTabs.ReviewLink(row.Id, Tab, Search);

        static ArmorRow Row(ArmorSubmission armor)
        {
            var check = ArmorRules.Check(armor, nameTaken: false);

            // The rules' own test for a name with nothing to show, spaces and no-break spaces included, which would leave
            // the row's link with nothing to click
            var blank = check.Blocking.Any(problem => problem.Kind == ArmorProblemKind.NameEmpty);

            return new ArmorRow(armor.ID, armor.ArmorName, blank, armor.Author, armor.Status, armor.KnownStatus, armor.CreatedAt,
                Labels(check.Blocking), Labels(check.Warnings));
        }

        // Each kind once, since one kind can be on the name, the description and the author
        static IReadOnlyList<string> Labels(IEnumerable<ArmorProblem> problems) => problems.Select(problem => ArmorWords.Label(problem.Kind)).Distinct().ToList();
    }
}
