using Dapper;
using DataAccessLibrary.Status.Models;

namespace DataAccessLibrary.Status
{
    // The dashboard's reads of the bot's status and history, which only the bot writes. Every list has a limit, every
    // input is checked before any SQL, and every window is a UTC time the caller works out from its own clock, never the
    // server's, which is US Eastern on the live server.
    public class BotStatusReads : DbBaseConnection, IBotStatusReads
    {
        // The most jobs read, the most days a read of days covers, and the most kinds one count takes. The bot has 5 jobs and
        // 18 kinds.
        public const int JobLimit = 100;
        public const int DayLimit = 100;
        public const int KindCountLimit = 20;

        private readonly DatabaseNames _names;

        public BotStatusReads(ISQLConnectionFactory connectionFactory, DatabaseNames names) : base(connectionFactory, DBConnectionName.Bot)
        {
            _names = names;
        }

        // The row as the last run left it, null when there's none
        public Task<BotStatusRow?> GetStatus()
        {
            string sql = $@"SELECT run_id, version, started_at, heartbeat_at, stopped_at, discord_state, discord_since, discord_latency_ms,
                            discord_guilds, steam_state, steam_since, steam_last_result, steam_token_expires
                            FROM {_names.Bot}.bot_status
                            WHERE id = 1";

            return LoadFirstData<BotStatusRow?, object?>(sql, null);
        }

        // Newest first, with the order they were written breaking ties, as the audit log is listed. Routine reconnects are
        // left out unless the query asks for them.
        public Task<Page<BotEventRow>> ListEvents(EventQuery query)
        {
            ArgumentNullException.ThrowIfNull(query);
            var (take, skip) = RequirePage(query.Page, query.PageSize);

            if (query.Level != null && !EventLevel.IsKnown(query.Level))
                throw new ArgumentException($"An event's level is one of: {string.Join(", ", EventLevel.All)}", nameof(query));

            if (query.Kind != null)
                RequireKind(query.Kind);

            string where = @"WHERE (@level IS NULL OR level = @level)
                            AND (@kind IS NULL OR kind = @kind)
                            AND (@routine OR NOT (BINARY kind = @routineKind AND detail IS NOT NULL AND BINARY detail = @routineDetail))";

            string sql = $@"SELECT id, at, kind, level, detail
                            FROM {_names.Bot}.bot_events
                            {where}
                            ORDER BY at DESC, id DESC
                            LIMIT @take OFFSET @skip;
                            SELECT COUNT(*)
                            FROM {_names.Bot}.bot_events
                            {where};";

            return LoadPage<BotEventRow, object>(sql, new
            {
                level = query.Level,
                kind = query.Kind,
                routine = query.IncludeRoutine,
                routineKind = RoutineReconnect.Kind,
                routineDetail = RoutineReconnect.Detail,
                take,
                skip
            });
        }

        // The newest event of a kind, null when there's none
        public Task<BotEventRow?> LatestEvent(string kind)
        {
            RequireKind(kind);

            string sql = $@"SELECT id, at, kind, level, detail
                            FROM {_names.Bot}.bot_events
                            WHERE kind = @kind
                            ORDER BY at DESC, id DESC
                            LIMIT 1";

            return LoadFirstData<BotEventRow?, object>(sql, new { kind });
        }

        // The oldest event of a kind that comes after the one given, in the list's order, or the oldest of all when none
        // is given, such as the first drop from Discord since the last connection. The one given is a row read here, whose
        // time is UTC.
        public Task<BotEventRow?> FirstEventAfter(string kind, BotEventRow? after)
        {
            RequireKind(kind);
            DateTime? at = after?.at;

            string sql = $@"SELECT id, at, kind, level, detail
                            FROM {_names.Bot}.bot_events
                            WHERE kind = @kind AND (@at IS NULL OR at > @at OR (at = @at AND id > @id))
                            ORDER BY at, id
                            LIMIT 1";

            return LoadFirstData<BotEventRow?, object>(sql, new { kind, at, id = after?.id ?? 0 });
        }

        // For each kind that has any, how many events it has from the first time on, and how many of those from the second
        public async Task<IReadOnlyList<EventCountRow>> CountEvents(IReadOnlyCollection<string> kinds, DateTime from, DateTime recentFrom)
        {
            ArgumentNullException.ThrowIfNull(kinds);
            RequireUtc(from, nameof(from));
            RequireUtc(recentFrom, nameof(recentFrom));

            if (kinds.Count is < 1 or > KindCountLimit)
                throw new ArgumentException($"A count takes from 1 to {KindCountLimit} kinds", nameof(kinds));

            foreach (var kind in kinds)
                RequireKind(kind);

            string sql = $@"SELECT kind, COUNT(*) AS total, COUNT(CASE WHEN at >= @recentFrom THEN 1 END) AS recent
                            FROM {_names.Bot}.bot_events
                            WHERE kind IN @kinds AND at >= @from
                            GROUP BY kind";

            return (await LoadDataEnum<EventCountRow, object>(sql, new { kinds, from, recentFrom }).ConfigureAwait(false)).ToList();
        }

        // Every job the bot has, by name
        public async Task<IReadOnlyList<JobStatusRow>> ListJobs()
        {
            string sql = $@"SELECT job, schedule, next_run_at, last_started_at, last_ended_at, last_result, last_duration_ms, last_detail, updated_at
                            FROM {_names.Bot}.job_status
                            ORDER BY job
                            LIMIT @take";

            return (await LoadDataEnum<JobStatusRow, object>(sql, new { take = JobLimit }).ConfigureAwait(false)).ToList();
        }

        // The latest runs of each of these jobs, newest first, in one call. One branch a job, so each reads only its newest
        // runs through job_runs_job and stops at its limit: numbering the runs of all of them in one pass would sort every
        // run of the 90 days kept, some 28,000 rows, in temporary tables that outgrow memory. UNION, not UNION ALL, so a job
        // named twice, in any letter case the column's collation matches, still has its runs once, as IN gave them.
        public async Task<IReadOnlyList<JobRunRow>> ListJobRuns(IReadOnlyCollection<string> jobs, int perJob)
        {
            ArgumentNullException.ThrowIfNull(jobs);

            if (jobs.Count > JobLimit || jobs.Any(job => job == null))
                throw new ArgumentException($"The runs of at most {JobLimit} jobs are read at once", nameof(jobs));

            if (perJob is < 1 or > MaxPageSize)
                throw new ArgumentOutOfRangeException(nameof(perJob), perJob, $"From 1 to {MaxPageSize} runs of each job");

            // Named before the empty list's answer, so a missing BotConnection fails this call as it fails the others
            string table = $"{_names.Bot}.job_runs";

            // No job makes no branch, and there's nothing to ask
            if (jobs.Count == 0)
                return [];

            var branches = new List<string>(jobs.Count);
            var parameters = new DynamicParameters(new { perJob, take = jobs.Count * perJob });

            foreach (var job in jobs)
            {
                var name = $"job{branches.Count}";
                parameters.Add(name, job);
                branches.Add($@"(SELECT id, job, started_at, ended_at, result, duration_ms, detail
                                FROM {table}
                                WHERE job = @{name}
                                ORDER BY started_at DESC, id DESC
                                LIMIT @perJob)");
            }

            string sql = $@"{string.Join(" UNION ", branches)}
                            ORDER BY job, started_at DESC, id DESC
                            LIMIT @take";

            return (await LoadDataEnum<JobRunRow, DynamicParameters>(sql, parameters).ConfigureAwait(false)).ToList();
        }

        // The days the clean-up has totalled, from the first day given up to the second, oldest first
        public async Task<IReadOnlyList<UptimeDayRow>> ListUptimeDays(DateTime from, DateTime to)
        {
            RequireDay(from, nameof(from));
            RequireDay(to, nameof(to));
            RequireDays(from, to);

            // A DateTime, because MySql.Data sends a DateOnly as the culture's short date, which MariaDB refuses
            string sql = $@"SELECT day, sampled_minutes, discord_minutes, steam_minutes
                            FROM {_names.Bot}.uptime_daily
                            WHERE day >= @from AND day < @to
                            ORDER BY day
                            LIMIT @take";

            return (await LoadDataEnum<UptimeDayRow, object>(sql, new { from, to, take = DayLimit }).ConfigureAwait(false)).ToList();
        }

        // Each UTC day's minutes as the samples have them, from a day's start up to any minute, such as today's so far.
        // A day with no samples has no row.
        public async Task<IReadOnlyList<UptimeDayRow>> CountSampleDays(DateTime from, DateTime to)
        {
            RequireDay(from, nameof(from));
            RequireUtc(to, nameof(to));
            RequireDays(from, to);

            string sql = $@"SELECT DATE(minute) AS day, COUNT(*) AS sampled_minutes, COUNT(CASE WHEN discord_up = 1 THEN 1 END) AS discord_minutes,
                            COUNT(CASE WHEN steam_up = 1 THEN 1 END) AS steam_minutes
                            FROM {_names.Bot}.uptime_samples
                            WHERE minute >= @from AND minute < @to
                            GROUP BY DATE(minute)
                            ORDER BY day
                            LIMIT @take";

            return (await LoadDataEnum<UptimeDayRow, object>(sql, new { from, to, take = DayLimit }).ConfigureAwait(false)).ToList();
        }

        // The minutes the samples have from one minute up to another, such as the last 24 hours
        public async Task<UptimeTotalRow> CountSamples(DateTime from, DateTime to)
        {
            RequireUtc(from, nameof(from));
            RequireUtc(to, nameof(to));
            RequireDays(from, to);

            string sql = $@"SELECT COUNT(*) AS sampled_minutes, COUNT(CASE WHEN discord_up = 1 THEN 1 END) AS discord_minutes,
                            COUNT(CASE WHEN steam_up = 1 THEN 1 END) AS steam_minutes
                            FROM {_names.Bot}.uptime_samples
                            WHERE minute >= @from AND minute < @to";

            return await LoadFirstData<UptimeTotalRow, object>(sql, new { from, to }).ConfigureAwait(false) ?? new UptimeTotalRow();
        }

        // The first minute sampled that's still kept and the first day totalled, which say where the record begins
        public async Task<UptimeStartRow> FirstRecord()
        {
            string sql = $@"SELECT (SELECT MIN(minute) FROM {_names.Bot}.uptime_samples) AS first_minute,
                            (SELECT MIN(day) FROM {_names.Bot}.uptime_daily) AS first_day";

            return await LoadFirstData<UptimeStartRow, object?>(sql, null).ConfigureAwait(false) ?? new UptimeStartRow();
        }

        // The form the bot writes, so a made-up kind never reaches the SQL
        static void RequireKind(string kind)
        {
            if (kind == null || kind.Length > BotStatusData.KindLimit || !BotStatusData.Kind.IsMatch(kind))
                throw new ArgumentException($"An event's kind is words in small letters joined by one dot, such as discord.connected, in at most {BotStatusData.KindLimit} characters", nameof(kind));
        }

        static void RequireDay(DateTime day, string name)
        {
            RequireUtc(day, name);

            if (day.TimeOfDay != TimeSpan.Zero)
                throw new ArgumentException("This is a day's start, 00:00 UTC", name);
        }

        static void RequireDays(DateTime from, DateTime to)
        {
            if (to < from)
                throw new ArgumentException("The time to read up to is before the time to read from", nameof(to));

            if (to - from > TimeSpan.FromDays(DayLimit))
                throw new ArgumentException($"One read covers at most {DayLimit} days", nameof(to));
        }
    }
}
