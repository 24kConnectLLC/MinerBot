using System.Globalization;
using DataAccessLibrary.Blacklist;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Blacklist;
using MinerBot.Dashboard.Hosting;
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
    // for in the last 30 minutes. No account the browser sends is ever read. Whatever the form didn't add comes back as a
    // form's notice: a redirect here with ?notice=, whose GET draws the page as the form left it, never this page drawn on
    // the POST, which a reload would send again, and which would add the account once the refusal's cause had gone.
    public sealed class BlacklistAddModel(IBlacklistData blacklist, TimeProvider time, ILogger<BlacklistAddModel> logger) : PageModel
    {
        // How long a lookup's answer is used for its name: a custom link can move to another account
        public static readonly TimeSpan LookupFor = TimeSpan.FromMinutes(30);

        // The page's one form, as its notice names it, and what the page drew for it, as the notice keeps it: a field to
        // correct; a lookup that found no profile, still waits or runs, or was just sent; a lookup the sender didn't send; an
        // account on the blacklist already, with other details or with these; and an add the database didn't confirm
        const string AddForm = "add";
        const string Fields = "fields", LookupNone = "lookup-none", LookupWaits = "lookup-waiting", LookupAsked = "lookup-sent", LookupRefused = "lookup-refused",
            Exists = "exists", Already = "already", Unconfirmed = "unsure";

        // The boxes, by the names the form sends them under, as the notice keeps them
        const string SteamIdBox = "steamid", NameBox = "name", ReasonBox = "reason", ExpiresBox = "expires", NotesBox = "notes";
        const int ExpiresLimit = 64;

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

        // A new key on every render, apart from the form a notice draws after a save the database didn't confirm for its
        // lookup, which keeps the key it was sent with, so sending it again finds the first request
        public string FormKey { get; private set; } = RequestFields.NewKey();

        public string LookupText => Lookup?.ToString(CultureInfo.InvariantCulture) ?? "";

        public async Task OnGetAsync([FromQuery] string? steamid, [FromQuery] string? notice)
        {
            // A form's notice, which draws the page as the form left it, and wins over ?steamid=
            if (HttpContext.RequestServices.GetRequiredService<FormNotices>().Find(this, notice, [AddForm]) is { } found)
            {
                await DrawAsync(found);
                return;
            }

            // A link from an entry that's no longer on the blacklist can bring its account, which is only put in the box
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
                var carried = ulong.TryParse(lookup, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number != 0 ? number : (ulong?)null;

                return KeepForm(new FormNotice { State = Fields, Status = StatusCodes.Status400BadRequest, Lookup = carried });
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
                        Form = Form with { Problems = new Dictionary<BlacklistField, string>(Form.Problems) { [BlacklistField.SteamId] = BlacklistWords.LookupNoProfile } };
                        return KeepForm(new FormNotice { State = LookupNone, Status = StatusCodes.Status400BadRequest, Lookup = row.id });

                    case RequestStage.Queued or RequestStage.Running:
                        return KeepForm(new FormNotice { State = LookupWaits, Status = StatusCodes.Status200OK, Lookup = row.id });
                }

                // Failed, refused, expired, not sure it happened, past its time, or a time that can't be read: asked again
            }

            var outcome = await HttpContext.RequestServices.GetRequiredService<RequestSender>()
                .SendAsync(new SendOrder(RequestKinds.SteamLookup, formkey, Argument: customName), HttpContext);

            switch (outcome)
            {
                // A notice too, whose address names only the notice, since the boxes hold a player's name and a reason, which
                // don't belong in an address: they travel in the notice's encrypted cookie. Its GET draws the lookup's box.
                case SendOutcome.Sent sent:
                    return KeepForm(new FormNotice { State = LookupAsked, Status = StatusCodes.Status200OK, Lookup = sent.Id });

                case SendOutcome.Refused { Why: SendRefusal.Forbidden }:
                    return Forbid();

                case SendOutcome.Refused { Why: SendRefusal.MadeUp }:
                    return BadRequest();
            }

            // Why it wasn't sent, every box kept, and the form's key kept only after a save the database didn't confirm
            return KeepForm(FormNotice.Of(AddForm, outcome) with { State = LookupRefused });
        }

        async Task<IActionResult> AddAsync(uint accountNumber, BlacklistFields fields)
        {
            var outcome = await HttpContext.RequestServices.GetRequiredService<BlacklistChanges>().AddAsync(accountNumber, fields, HttpContext);

            switch (outcome)
            {
                // The entry's own page, so reloading it shows the entry rather than sending the form again
                case BlacklistOutcome.Done:
                    return RedirectToPage("/Admin/Blacklist/Entry", new { account = (long)accountNumber, done = BlacklistAction.Add.DoneWord() });

                // Not the error page, which would say nothing was changed. The boxes aren't kept: only why is drawn.
                case BlacklistOutcome.Unsure:
                    return HttpContext.RequestServices.GetRequiredService<FormNotices>().Keep(this, new FormNotice
                    {
                        Form = AddForm,
                        State = Unconfirmed,
                        Status = StatusCodes.Status503ServiceUnavailable,
                        Unsure = true,
                        Account = accountNumber
                    });
            }

            // Already on the blacklist: the form again as it was sent, and a link to the entry. An entry that already has
            // exactly these details was most likely this editor's own earlier try, which isn't a problem. Decided here, from
            // the entry as the add found it, so the notice's GET says the same however the entry changes after.
            var existing = await DatabaseCalls.Guard(() => blacklist.GetBannedPlayer(accountNumber), logger);
            var same = existing != null && BlacklistChanges.Has(existing, fields);

            return KeepForm(new FormNotice { State = same ? Already : Exists, Status = StatusCodes.Status409Conflict, Account = accountNumber, Lookup = Lookup });
        }

        // A notice of the form as it was sent, which this page's GET draws with every box back in it
        IActionResult KeepForm(FormNotice notice)
        {
            var sent = Form;
            return HttpContext.RequestServices.GetRequiredService<FormNotices>().Keep(this, notice with { Form = AddForm, Kept = Kept(sent) });
        }

        // Each box as it was sent, within its limit, all a browser sends, and each field's message: an end date's box sends
        // 10 characters, and 64 leaves room for a browser without date boxes. A box past its limit comes only from a form
        // someone made up, so it isn't kept, and its message still shows.
        static Dictionary<string, string> Kept(BlacklistFormValues form)
        {
            var kept = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (box, value, limit) in new[]
            {
                (SteamIdBox, form.SteamId, BlacklistForm.SteamIdLimit),
                (NameBox, form.Name, BlacklistData.UsernameLimit),
                (ReasonBox, form.Reason, BlacklistData.ReasonLimit),
                (ExpiresBox, form.Expires, ExpiresLimit),
                (NotesBox, form.Notes, BlacklistData.NotesLimit)
            })
            {
                if (FormNotices.Text(value, limit) is { } text)
                    kept[box] = text;
            }

            foreach (var (field, problem) in form.Problems)
                kept[FormNotices.ProblemPrefix + field] = problem;

            return kept;
        }

        // The boxes and each field's message as a notice kept them, a box it didn't keep empty
        static BlacklistFormValues FormOf(Dictionary<string, string>? kept)
        {
            kept ??= new Dictionary<string, string>(StringComparer.Ordinal);
            var problems = new Dictionary<BlacklistField, string>();

            foreach (var (name, problem) in kept)
            {
                if (name.StartsWith(FormNotices.ProblemPrefix, StringComparison.Ordinal)
                    && Enum.TryParse<BlacklistField>(name[FormNotices.ProblemPrefix.Length..], out var field) && Enum.IsDefined(field))
                    problems[field] = problem;
            }

            return new BlacklistFormValues(kept.GetValueOrDefault(SteamIdBox, ""), kept.GetValueOrDefault(NameBox, ""), kept.GetValueOrDefault(ReasonBox, ""),
                kept.GetValueOrDefault(ExpiresBox, ""), kept.GetValueOrDefault(NotesBox, ""), problems);
        }

        // The page as a form's notice left it, with what's read now: the lookup's box as it stands, which says whether the
        // bot has answered since
        async Task DrawAsync(FormNotice found)
        {
            Form = FormOf(found.Kept);
            Lookup = found.Lookup;
            FormKey = found.Key ?? FormKey;

            switch (found.State)
            {
                case LookupNone or LookupWaits or LookupAsked when Lookup is { } id:
                    // Never another kind's request, nor another person's lookup, as at the POST
                    if (await HttpContext.RequestServices.GetRequiredService<RequestBoxes>().LoadAsync(id) is { } box
                        && box.Row.kind == RequestKinds.SteamLookup && box.Row.actor_id == RequestActors.Of(HttpContext)?.Id)
                        LookupBox = new RequestSection(box.View, null);

                    LookupNotice = found.State switch
                    {
                        LookupWaits => BlacklistWords.LookupWaiting,
                        LookupAsked => BlacklistWords.LookupSent,
                        _ => null
                    };
                    break;

                case LookupRefused:
                    LookupProblem = found.Words;
                    LookupProblemRequest = found.Request;
                    break;

                case Exists or Already when found.Account is { } listed:
                    NoticeIsProblem = found.State == Exists;
                    Notice = NoticeIsProblem ? BlacklistWords.Exists : BlacklistWords.AlreadyAdded;
                    EntryLink = SteamAccounts.EntryLink(listed);
                    break;

                case Unconfirmed when found.Account is { } added:
                    Unsure = true;
                    SearchLink = "/admin/blacklist?q=" + Uri.EscapeDataString(SteamAccounts.Steam3(added));
                    break;
            }

            RequestWords.Answer(Response, found, time.GetUtcNow().UtcDateTime);
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
