using System.Globalization;
using DataAccessLibrary.Blacklist;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Blacklist;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Settings;
using MinerBot.Shared.Requests;
using MinerBot.Shared.Steam;

namespace MinerBot.Dashboard.Pages.Admin.Blacklist
{
    // A new entry: the account in any way of writing a SteamID, and its name, reason, end date and notes. Every field is
    // checked before the database, and one that fails comes back with a message and what was typed kept. The add itself
    // goes through BlacklistChanges. A custom profile link, which only Steam can turn into an account, is first looked up
    // by the bot: the page comes back with every box kept and the lookup's box, carrying only the request's number, and the
    // next press adds the account the bot's answer names, read on the server from a lookup of that name this person asked
    // for in the last 30 minutes. No account the browser sends is ever read.
    public sealed class BlacklistAddModel(IBlacklistData blacklist, TimeProvider time, ILogger<BlacklistAddModel> logger) : PageModel
    {
        // How long a lookup's answer is used for its name: a custom link can move to another account
        public static readonly TimeSpan LookupFor = TimeSpan.FromMinutes(30);

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

        // A custom link's lookup: the request the form carries back by its number, its box, what the page says of it, and
        // why one wasn't sent, with the request that names
        public ulong? Lookup { get; private set; }
        public RequestSection? LookupBox { get; private set; }
        public string? LookupNotice { get; private set; }
        public string? LookupProblem { get; private set; }
        public ulong? LookupProblemRequest { get; private set; }

        // A new key on every render, apart from the form sent when the database didn't confirm its lookup's save, which
        // keeps the key it was sent with, so sending it again finds the first request
        public string FormKey { get; private set; } = RequestFields.NewKey();

        public string LookupText => Lookup?.ToString(CultureInfo.InvariantCulture) ?? "";

        // A link from an entry that's no longer on the blacklist can bring its account, which is only put in the box
        public void OnGet([FromQuery] string? steamid)
        {
            var account = (steamid ?? "").Trim();

            if (account.EnumerateRunes().Count() <= BlacklistForm.SteamIdLimit)
                Form = BlacklistFormValues.Empty with { SteamId = account };
        }

        public async Task<IActionResult> OnPostAsync(string? steamid, string? name, string? reason, string? expires, string? notes, string? formkey, string? lookup)
        {
            var account = (steamid ?? "").Trim();
            BlacklistForm.TryFields(name, reason, expires, notes, null, out var fields, out var fieldProblems);
            var problems = new Dictionary<BlacklistField, string>(fieldProblems);

            // Account 0 is refused here, as every way of writing it is
            var parsed = account.EnumerateRunes().Count() > BlacklistForm.SteamIdLimit ? null : SteamIds.Parse(account);

            // What was typed goes back in the boxes. Each message is fixed text, and a custom link's name is never repeated.
            if (parsed == null)
                problems[BlacklistField.SteamId] = BlacklistWords.Problem(BlacklistField.SteamId, TextProblem.TooLong);
            else if (parsed.Kind == SteamIdKind.NeedsLookup && !RequestsOn())
                problems[BlacklistField.SteamId] = BlacklistWords.CustomLinkOff;
            else if (parsed.Kind == SteamIdKind.Invalid)
                problems[BlacklistField.SteamId] = parsed.Error ?? BlacklistWords.SteamIdHint;

            Form = new BlacklistFormValues(account, name ?? "", reason ?? "", expires ?? "", notes ?? "", problems);

            if (parsed == null || problems.Count > 0)
            {
                // A lookup the form carries stays in it, so the press after the correction uses the bot's answer rather than
                // asking again. Only its number goes back, and that press checks the lookup in full.
                if (ulong.TryParse(lookup, NumberStyles.None, CultureInfo.InvariantCulture, out var carried) && carried != 0)
                    Lookup = carried;

                Response.StatusCode = StatusCodes.Status400BadRequest;
                return Page();
            }

            if (parsed.Kind == SteamIdKind.NeedsLookup)
                return await LookUpAsync(parsed.VanityName!, fields, formkey, lookup);

            return await AddAsync(parsed.AccountNumber, fields);
        }

        // A custom link: the lookup the form names, when it's this person's of this name from the last 30 minutes, adds the
        // account its answer names, says to wait while it waits, and says Steam has none when that's the answer; otherwise
        // the bot is asked again. The browser sends back only the request's number, and one that isn't this person's lookup is
        // a form someone made up.
        async Task<IActionResult> LookUpAsync(string customName, BlacklistFields fields, string? formkey, string? lookup)
        {
            var boxes = HttpContext.RequestServices.GetRequiredService<RequestBoxes>();
            RequestBox? box = null;

            if (!string.IsNullOrEmpty(lookup))
            {
                if (!ulong.TryParse(lookup, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
                    return BadRequest();

                box = await boxes.LoadAsync(id);

                // Never another kind's request, whose number isn't an account, nor another person's lookup
                if (box != null && (box.Row.kind != RequestKinds.SteamLookup || box.Row.actor_id != RequestActors.Of(HttpContext)?.Id))
                    return BadRequest();
            }

            var now = time.GetUtcNow().UtcDateTime;

            // This name's own lookup, recent enough, as the bot has answered it so far
            if (box is { Row: var row } && row.argument == customName && row.created_at is { } asked && now - asked <= LookupFor)
            {
                switch (box.View.Stage)
                {
                    case RequestStage.Done when row.result_number is >= 1 and <= uint.MaxValue:
                        Lookup = row.id;
                        return await AddAsync((uint)row.result_number.Value, fields);

                    case RequestStage.Done:
                        Lookup = row.id;
                        LookupBox = new RequestSection(box.View, null);
                        Form = Form with { Problems = new Dictionary<BlacklistField, string>(Form.Problems) { [BlacklistField.SteamId] = BlacklistWords.LookupNoProfile } };
                        Response.StatusCode = StatusCodes.Status400BadRequest;
                        return Page();

                    case RequestStage.Queued or RequestStage.Running:
                        Lookup = row.id;
                        LookupBox = new RequestSection(box.View, null);
                        LookupNotice = BlacklistWords.LookupWaiting;
                        return Page();
                }

                // Failed, refused, expired, not sure it happened, past its time, or a time that can't be read: asked again
            }

            var outcome = await HttpContext.RequestServices.GetRequiredService<RequestSender>()
                .SendAsync(new SendOrder(RequestKinds.SteamLookup, formkey, Argument: customName), HttpContext);

            switch (outcome)
            {
                // The page again, not a redirect, because the boxes hold a player's name and a reason, which don't belong in an
                // address. Sending it again sends the same key, which finds the same request.
                case SendOutcome.Sent sent:
                    Lookup = sent.Id;
                    LookupBox = await boxes.LoadAsync(sent.Id) is { } sentBox ? new RequestSection(sentBox.View, null) : null;
                    LookupNotice = BlacklistWords.LookupSent;
                    return Page();

                case SendOutcome.Refused { Why: SendRefusal.Forbidden }:
                    return Forbid();

                case SendOutcome.Refused { Why: SendRefusal.MadeUp }:
                    return BadRequest();
            }

            // The page again with why, every box kept, and the form's key kept only after a save the database didn't confirm
            (LookupProblem, LookupProblemRequest) = outcome switch
            {
                SendOutcome.SentBefore before => (RequestWords.SentBefore(before.Id), before.Id),
                SendOutcome.Refused refused => (refused.Words, refused.Waiting),
                _ => (RequestWords.Unsure, (ulong?)null)
            };

            FormKey = FormKeys.For(outcome);
            RequestWords.Answer(Response, outcome, now);
            return Page();
        }

        async Task<IActionResult> AddAsync(uint accountNumber, BlacklistFields fields)
        {
            var outcome = await HttpContext.RequestServices.GetRequiredService<BlacklistChanges>().AddAsync(accountNumber, fields, HttpContext);
            var link = SteamAccounts.EntryLink(accountNumber);

            switch (outcome)
            {
                // The entry's own page, so reloading it shows the entry rather than sending the form again
                case BlacklistOutcome.Done:
                    return RedirectToPage("/Admin/Blacklist/Entry", new { account = (long)accountNumber, done = BlacklistAction.Add.DoneWord() });

                // Not the error page, which would say nothing was changed
                case BlacklistOutcome.Unsure:
                    Unsure = true;
                    SearchLink = "/admin/blacklist?q=" + Uri.EscapeDataString(SteamAccounts.Steam3(accountNumber));
                    Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return Page();
            }

            // Already on the blacklist: the form again as it was sent, and a link to the entry. An entry that already has
            // exactly these details was most likely this editor's own earlier try, which isn't a problem.
            var existing = await DatabaseCalls.Guard(() => blacklist.GetBannedPlayer(accountNumber), logger);

            NoticeIsProblem = existing == null || !BlacklistChanges.Has(existing, fields);
            Notice = NoticeIsProblem ? BlacklistWords.Exists : BlacklistWords.AlreadyAdded;
            EntryLink = link;
            Response.StatusCode = StatusCodes.Status409Conflict;
            return Page();
        }

        bool RequestsOn() => RequestSigning.ParseKey(HttpContext.RequestServices.GetRequiredService<IOptions<DashboardOptions>>().Value.RequestSigningKey) != null;

        // A request no handler takes, such as a PUT, would otherwise get the page as if it were a GET
        public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
        {
            if (context.HandlerMethod == null)
                context.Result = BadRequest();
        }
    }
}
