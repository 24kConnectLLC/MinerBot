using System.Globalization;
using System.Text.RegularExpressions;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Blacklist;
using MinerBot.Dashboard.Security;

namespace MinerBot.Dashboard.Pages.Admin
{
    // The filters as the address carries them, each checked, and empty when it's not given
    public sealed record AuditFilters(string Person, string Action, string Server, string From, string To, string Type, string Id)
    {
        public static readonly AuditFilters None = new("", "", "", "", "", "", "");

        public bool Any => this != None;
    }

    // Every change made on the dashboard, every sign-in and sign-out, and the changes and sign-ins it refused, newest first,
    // 25 to a page, with filters by person, action, server, days and what a row is about. Each filter is checked here
    // first: the boxes' patterns, the select and the date boxes send nothing else, so anything else is a made-up address
    // and a 400. The ID boxes' pattern does let through 20 digits too big for any ID, which match no row. The visitor's
    // address of each row is for the Owner alone, and for anyone else it isn't even read.
    public sealed partial class AuditLogModel(IAuditLogData audit, ILogger<AuditLogModel> logger) : PageModel
    {
        public const int PageSize = 25;

        public AuditFilters Filters { get; private set; } = AuditFilters.None;
        public int PageNumber { get; private set; } = 1;
        public long Total { get; private set; }
        public AuditTable Table { get; private set; } = new([], false);

        // Whether this page shows each row's visitor address: only for the Owner
        public bool ShowsAddresses { get; private set; }

        // The From day is after the To day, so no row can match
        public bool StartAfterEnd { get; private set; }

        // What the type and ID filters are about, in words, with a link to its page when it has one
        public AuditTarget? About { get; private set; }

        public long PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);

        public string Summary
        {
            get
            {
                var count = Total.ToString("N0", CultureInfo.InvariantCulture);
                var rows = Total == 1 ? "row" : "rows";

                return Filters.Any ? $"{count} {rows} that {(Total == 1 ? "matches" : "match")} the filters, newest first." : $"{count} {rows}, newest first.";
            }
        }

        public static IReadOnlyList<AuditWord> Actions => AuditWords.Actions;

        // From the query alone: Razor Pages keeps the page's own name in a route value called page, which would win
        public async Task<IActionResult> OnGetAsync([FromQuery] string? person, [FromQuery] string? action, [FromQuery] string? server,
            [FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? type, [FromQuery] string? id, [FromQuery(Name = "page")] string? page)
        {
            if (!TryDiscordId(person, out var actorId, out var personFits) || !TryDiscordId(server, out var guildId, out var serverFits)
                || !TryDay(from, out var fromDay) || !TryDay(to, out var toDay))
                return BadRequest();

            if (!string.IsNullOrEmpty(action) && AuditWords.Action(action) == null)
                return BadRequest();

            if (!string.IsNullOrEmpty(type) && AuditWords.Target(type) == null)
                return BadRequest();

            if (!string.IsNullOrEmpty(id) && !TargetId().IsMatch(id))
                return BadRequest();

            Filters = new AuditFilters(person ?? "", action ?? "", server ?? "", from ?? "", to ?? "", type ?? "", id ?? "");
            PageNumber = int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 1 ? parsed : 1;
            ShowsAddresses = RoleClaims.RoleOf(User) == DashboardRole.Owner;
            About = Filters.Type.Length > 0 || Filters.Id.Length > 0 ? AuditRows.Target(NullIfEmpty(Filters.Type), NullIfEmpty(Filters.Id)) : null;

            if (fromDay is { } start && toDay is { } end && start > end)
            {
                StartAfterEnd = true;
                return Page();
            }

            // An ID too big for any Discord ID, such as one with a digit too many, which the box lets through: the box keeps
            // it, and the page says no row matches, which is true, without asking the database
            if (!personFits || !serverFits)
                return Page();

            // UTC days: From from its start, and To up to the start of the next day, which ListAudit leaves out
            var found = await DatabaseCalls.Guard(() => audit.ListAudit(new AuditQuery
            {
                ActorId = actorId,
                Action = NullIfEmpty(action),
                GuildId = guildId,
                TargetType = NullIfEmpty(type),
                TargetId = NullIfEmpty(id),
                From = fromDay?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                To = toDay?.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                Page = PageNumber - 1,
                PageSize = PageSize,
                IncludeIp = ShowsAddresses
            }), logger);

            Total = found.Total;
            Table = AuditRows.Table(found.Rows, ShowsAddresses);
            return Page();
        }

        // Nothing here takes a form, and a request no handler takes would otherwise get the page as if it were a GET
        public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
        {
            if (context.HandlerMethod == null)
                context.Result = BadRequest();
        }

        // This page with every filter kept, at a page, the first unless one is given
        public string Link(int? page = null)
        {
            var query = new QueryBuilder();

            foreach (var (name, value) in new[] { ("person", Filters.Person), ("action", Filters.Action), ("server", Filters.Server), ("from", Filters.From),
                ("to", Filters.To), ("type", Filters.Type), ("id", Filters.Id) })
            {
                if (value.Length > 0)
                    query.Add(name, value);
            }

            if ((page ?? 1) > 1)
                query.Add("page", page!.Value.ToString(CultureInfo.InvariantCulture));

            return "/admin/audit" + query.ToQueryString();
        }

        // A Discord ID as the boxes' pattern allows it: up to 20 digits, or nothing. Fits is false for 20 digits past the
        // biggest number a ulong holds, which the pattern can't stop and no Discord ID can be, so it's no one's.
        static bool TryDiscordId(string? value, out ulong? id, out bool fits)
        {
            id = null;
            fits = true;

            if (string.IsNullOrEmpty(value))
                return true;

            if (!DiscordId().IsMatch(value))
                return false;

            if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                id = parsed;
            else
                fits = false;

            return true;
        }

        // A day as the date boxes send it, in the years they allow, so the day after it is still a day DATETIME holds
        static bool TryDay(string? value, out DateOnly? day)
        {
            day = null;

            if (string.IsNullOrEmpty(value))
                return true;

            if (!DateOnly.TryParseExact(value, BlacklistForm.DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                || parsed.Year is < BlacklistForm.FirstYear or > BlacklistForm.LastYear)
                return false;

            day = parsed;
            return true;
        }

        static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

        // [0-9] rather than \d, which also takes other scripts' digits, and \z rather than $, which also passes a line end
        [GeneratedRegex(@"^[0-9]{1,20}\z", RegexOptions.CultureInvariant)]
        private static partial Regex DiscordId();

        // As target_id holds them: an armor's ID, an account number, or a number no account can be, at most 40 characters
        [GeneratedRegex(@"^-?[0-9]{1,39}\z", RegexOptions.CultureInvariant)]
        private static partial Regex TargetId();
    }
}
