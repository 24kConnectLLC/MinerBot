using System.Globalization;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Status;

namespace MinerBot.Dashboard.Pages.Admin
{
    // One job with its latest runs, newest first
    public sealed record JobRow(JobStatusRow Job, IReadOnlyList<JobRunRow> Runs);

    // The events' filters as the address carries them, each checked, and empty when not given. Showing the routine
    // reconnects adds events rather than leaving any out, so only a level or a kind narrows the list.
    public sealed record EventFilters(string Level, string Kind, bool Routine)
    {
        public static readonly EventFilters None = new("", "", false);

        public bool Any => this != None;
        public bool Narrows => Level.Length > 0 || Kind.Length > 0;
    }

    // MinerBot itself: the status box, the version and starts, uptime, the jobs and the events, all read-only, from the
    // tables the bot writes, so it works while the bot is down. Every filter is checked here first: the form's selects and
    // box send nothing else, so anything else is a made-up address and a 400.
    public sealed class BotStatusModel(IBotStatusReads reads, ILogger<BotStatusModel> logger) : PageModel
    {
        public const int EventsPerPage = 50;
        public const int RunsPerJob = 50;

        // The value the routine reconnects' box sends
        public const string RoutineShown = "1";

        public StatusBox Box { get; private set; } = null!;

        // The bot's starts and its runs that ended without a clean stop, over the last 7 and 30 days. The dashboard can't
        // see Docker, so the bot's own records stand in for its restarts.
        public long StartsWeek { get; private set; }
        public long StartsMonth { get; private set; }
        public long UncleanWeek { get; private set; }
        public long UncleanMonth { get; private set; }

        public IReadOnlyList<UptimePeriod> Periods { get; private set; } = [];
        public IReadOnlyList<UptimeDay> Days { get; private set; } = [];
        public IReadOnlyList<JobRow> Jobs { get; private set; } = [];

        public EventFilters Filters { get; private set; } = EventFilters.None;
        public int PageNumber { get; private set; } = 1;
        public long Total { get; private set; }
        public IReadOnlyList<BotEventRow> Events { get; private set; } = [];

        public long PageCount => Math.Max(1, (Total + EventsPerPage - 1) / EventsPerPage);

        public static IReadOnlyList<EventWord> Kinds => EventWords.Kinds;
        public static IReadOnlyList<EventWord> Levels => EventWords.Levels;

        public string Summary
        {
            get
            {
                var count = Total.ToString("N0", CultureInfo.InvariantCulture);
                var events = Total == 1 ? "event" : "events";

                return Filters.Narrows ? $"{count} {events} that {(Total == 1 ? "matches" : "match")} the filters, newest first." : $"{count} {events}, newest first.";
            }
        }

        // From the query alone: Razor Pages keeps the page's own name in a route value called page, which would win
        public async Task<IActionResult> OnGetAsync([FromQuery] string? level, [FromQuery] string? kind, [FromQuery] string? routine, [FromQuery(Name = "page")] string? page)
        {
            if (!string.IsNullOrEmpty(level) && !EventLevel.IsKnown(level))
                return BadRequest();

            if (!string.IsNullOrEmpty(kind) && EventWords.Kind(kind) == null)
                return BadRequest();

            if (!string.IsNullOrEmpty(routine) && routine != RoutineShown)
                return BadRequest();

            Filters = new EventFilters(level ?? "", kind ?? "", routine == RoutineShown);
            PageNumber = int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 1 ? parsed : 1;

            Box = await HttpContext.RequestServices.GetRequiredService<StatusBoxes>().LoadAsync();
            var now = Box.Now;

            var counts = await DatabaseCalls.Guard(() => reads.CountEvents([EventWords.Started, EventWords.UncleanEnd], now.AddDays(-30), now.AddDays(-7)), logger);
            var starts = counts.FirstOrDefault(count => count.kind == EventWords.Started);
            var unclean = counts.FirstOrDefault(count => count.kind == EventWords.UncleanEnd);
            (StartsMonth, StartsWeek) = (starts?.total ?? 0, starts?.recent ?? 0);
            (UncleanMonth, UncleanWeek) = (unclean?.total ?? 0, unclean?.recent ?? 0);

            await LoadUptimeAsync(now);

            var jobs = await DatabaseCalls.Guard(() => reads.ListJobs(), logger);
            var runs = await DatabaseCalls.Guard(() => reads.ListJobRuns(jobs.Select(job => job.job).ToList(), RunsPerJob), logger);
            Jobs = jobs.Select(job => new JobRow(job, runs.Where(run => run.job == job.job).ToList())).ToList();

            var found = await DatabaseCalls.Guard(() => reads.ListEvents(new EventQuery
            {
                Level = NullIfEmpty(level),
                Kind = NullIfEmpty(kind),
                IncludeRoutine = Filters.Routine,
                Page = PageNumber - 1,
                PageSize = EventsPerPage
            }), logger);

            Total = found.Total;
            Events = found.Rows;
            return Page();
        }

        // The strip's 90 days from uptime_daily where the clean-up has totalled them and from the samples after that, today
        // included up to the current minute, and the last 24 hours from the samples
        async Task LoadUptimeAsync(DateTime now)
        {
            var minute = UptimeDays.Minute(now);
            var start = await DatabaseCalls.Guard(() => reads.FirstRecord(), logger);
            var totalled = await DatabaseCalls.Guard(() => reads.ListUptimeDays(UptimeDays.StripStart(now), minute.Date), logger);
            var sampleFrom = UptimeDays.SampleDaysFrom(now, totalled);
            IReadOnlyList<UptimeDayRow> sampled = sampleFrom < minute ? await DatabaseCalls.Guard(() => reads.CountSampleDays(sampleFrom, minute), logger) : [];
            var lastDay = await DatabaseCalls.Guard(() => reads.CountSamples(minute.AddDays(-1), minute), logger);
            var recordStart = UptimeDays.RecordStart(start);

            Days = UptimeDays.Days(now, recordStart, totalled, sampled);
            Periods =
            [
                UptimeDays.LastDay(now, recordStart, lastDay),
                UptimeDays.Period("Last 7 days", Days, 7),
                UptimeDays.Period("Last 30 days", Days, 30),
                UptimeDays.Period("Last 90 days", Days, UptimeDays.StripDays)
            ];
        }

        // Nothing here takes a form, and a request no handler takes would otherwise get the page as if it were a GET
        public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
        {
            if (context.HandlerMethod == null)
                context.Result = BadRequest();
        }

        // This page with every filter kept, at a page of the events, the first unless one is given
        public string Link(int? page = null)
        {
            var query = new QueryBuilder();

            if (Filters.Level.Length > 0)
                query.Add("level", Filters.Level);

            if (Filters.Kind.Length > 0)
                query.Add("kind", Filters.Kind);

            if (Filters.Routine)
                query.Add("routine", RoutineShown);

            if ((page ?? 1) > 1)
                query.Add("page", page!.Value.ToString(CultureInfo.InvariantCulture));

            return "/admin/bot" + query.ToQueryString() + "#events";
        }

        static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
