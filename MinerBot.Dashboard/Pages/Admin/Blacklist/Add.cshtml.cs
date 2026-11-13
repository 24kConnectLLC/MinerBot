using DataAccessLibrary.Blacklist;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Blacklist;
using MinerBot.Dashboard.Security;
using MinerBot.Shared.Steam;

namespace MinerBot.Dashboard.Pages.Admin.Blacklist
{
    // A new entry: the account in any way of writing a SteamID, and its name, reason, end date and notes. Every field is
    // checked before the database, and one that fails comes back with a message and what was typed kept. The add itself
    // goes through BlacklistChanges.
    public sealed class BlacklistAddModel(IBlacklistData blacklist, ILogger<BlacklistAddModel> logger) : PageModel
    {
        public BlacklistFormValues Form { get; private set; } = BlacklistFormValues.Empty;

        // A message for the top of the page, from BlacklistWords' fixed set, whether it says why nothing was done, and the
        // entry it's about
        public string? Notice { get; private set; }
        public bool NoticeIsProblem { get; private set; }
        public string? EntryLink { get; private set; }

        // The database stopped answering while the add was being committed, which may or may not have landed. The entry
        // may not exist, so the page links the list's search for the account, which finds it if it does.
        public bool Unsure { get; private set; }
        public string? SearchLink { get; private set; }

        // A link from an entry that's no longer on the blacklist can bring its account, which is only put in the box
        public void OnGet([FromQuery] string? steamid)
        {
            var account = (steamid ?? "").Trim();

            if (account.EnumerateRunes().Count() <= BlacklistForm.SteamIdLimit)
                Form = BlacklistFormValues.Empty with { SteamId = account };
        }

        public async Task<IActionResult> OnPostAsync(string? steamid, string? name, string? reason, string? expires, string? notes)
        {
            var account = (steamid ?? "").Trim();
            BlacklistForm.TryFields(name, reason, expires, notes, null, out var fields, out var fieldProblems);
            var problems = new Dictionary<BlacklistField, string>(fieldProblems);

            // Account 0 is refused here, as every way of writing it is
            var parsed = account.EnumerateRunes().Count() > BlacklistForm.SteamIdLimit ? null : SteamIds.Parse(account);

            // What was typed goes back in the boxes. Each message is fixed text, and a custom link's name is never repeated.
            if (parsed == null)
                problems[BlacklistField.SteamId] = BlacklistWords.Problem(BlacklistField.SteamId, TextProblem.TooLong);
            else if (parsed.Kind == SteamIdKind.NeedsLookup)
                problems[BlacklistField.SteamId] = BlacklistWords.CustomLink;
            else if (parsed.Kind != SteamIdKind.AccountNumber)
                problems[BlacklistField.SteamId] = parsed.Error ?? BlacklistWords.CustomLink;

            if (parsed is not { Kind: SteamIdKind.AccountNumber } || problems.Count > 0)
            {
                Form = new BlacklistFormValues(account, name ?? "", reason ?? "", expires ?? "", notes ?? "", problems);
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return Page();
            }

            var outcome = await HttpContext.RequestServices.GetRequiredService<BlacklistChanges>().AddAsync(parsed.AccountNumber, fields, HttpContext);
            var link = SteamAccounts.EntryLink(parsed.AccountNumber);

            switch (outcome)
            {
                // The entry's own page, so reloading it shows the entry rather than sending the form again
                case BlacklistOutcome.Done:
                    return RedirectToPage("/Admin/Blacklist/Entry", new { account = (long)parsed.AccountNumber, done = BlacklistAction.Add.DoneWord() });

                // Not the error page, which would say nothing was changed
                case BlacklistOutcome.Unsure:
                    Unsure = true;
                    SearchLink = "/admin/blacklist?q=" + Uri.EscapeDataString(SteamAccounts.Steam3(parsed.AccountNumber));
                    Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return Page();
            }

            // Already on the blacklist: the form again as it was sent, and a link to the entry. An entry that already has
            // exactly these details was most likely this editor's own earlier try, which isn't a problem.
            var existing = await DatabaseCalls.Guard(() => blacklist.GetBannedPlayer(parsed.AccountNumber), logger);

            NoticeIsProblem = existing == null || !BlacklistChanges.Has(existing, fields);
            Notice = NoticeIsProblem ? BlacklistWords.Exists : BlacklistWords.AlreadyAdded;
            EntryLink = link;
            Form = new BlacklistFormValues(account, name ?? "", reason ?? "", expires ?? "", notes ?? "", problems);
            Response.StatusCode = StatusCodes.Status409Conflict;
            return Page();
        }

        // A request no handler takes, such as a PUT, would otherwise get the page as if it were a GET
        public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
        {
            if (context.HandlerMethod == null)
                context.Result = BadRequest();
        }
    }
}
