using Dapper;
using DataAccessLibrary.Status.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace DataAccessLibrary.Status
{
    // The bot's status and history in MinerBot's own database, which only the bot writes, with its own login. The bot keeps
    // what it records in memory until a write of it goes through, and a write that failed or timed out may still have
    // landed, so every write here is safe to repeat: the status row, the samples, the job runs and the job status are
    // upserts, and an event that's written again lands once.
    public class BotStatusData : DbBaseConnection, IBotStatusData
    {
        // The most rows one call writes, so one statement never grows large
        public const int BatchLimit = 100;

        const int VersionLimit = 40;
        const int KindLimit = 40;
        const int JobLimit = 100;
        const int ScheduleLimit = 100;
        const int ResultNameLimit = 40;
        const int DetailLimit = 300;

        // Words in small letters joined by one dot, such as discord.connected
        static readonly Regex Kind = new(@"^[a-z_]+\.[a-z_]+\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        private readonly DatabaseNames _names;

        public BotStatusData(ISQLConnectionFactory connectionFactory, DatabaseNames names) : base(connectionFactory, DBConnectionName.Bot)
        {
            _names = names;
        }

        // The row as the last run left it, null when there's none. A run reads it before its first write, to tell whether
        // the run before stopped cleanly.
        public Task<BotStatus?> GetStatus()
        {
            string sql = $@"SELECT run_id, version, started_at, heartbeat_at, stopped_at, discord_state, discord_since, discord_latency_ms,
                            discord_guilds, steam_state, steam_since, steam_last_result, steam_token_expires
                            FROM {_names.Bot}.bot_status
                            WHERE id = 1";

            return LoadFirstData<BotStatus?, object?>(sql, null);
        }

        public Task SaveStatus(BotStatus status)
        {
            ArgumentNullException.ThrowIfNull(status);
            RequireUtc(status.started_at, nameof(status.started_at));
            RequireUtc(status.heartbeat_at, nameof(status.heartbeat_at));
            RequireUtcOrNull(status.stopped_at, nameof(status.stopped_at));
            RequireUtcOrNull(status.discord_since, nameof(status.discord_since));
            RequireUtcOrNull(status.steam_since, nameof(status.steam_since));
            RequireFits(status.version, VersionLimit, "version");

            if (!DiscordState.IsKnown(status.discord_state))
                throw new ArgumentException($"Discord's state is one of: {string.Join(", ", DiscordState.All)}", nameof(status));

            if (!SteamState.IsKnown(status.steam_state))
                throw new ArgumentException($"Steam's state is one of: {string.Join(", ", SteamState.All)}", nameof(status));

            // The stop write is a run's last, so a row that has its run's stop never changes for that run again. A write the
            // bot gave up on when it stopped may land after the stop write, and taking the stop away would make the next start
            // count a clean stop as an unclean end. MariaDB applies the assignments in order, so every one that looks at the
            // row's run and stop comes before the two that set them.
            const string Stopped = "run_id = VALUES(run_id) AND stopped_at IS NOT NULL";

            string sql = $@"INSERT INTO {_names.Bot}.bot_status (id, run_id, version, started_at, heartbeat_at, stopped_at, discord_state, discord_since,
                            discord_latency_ms, discord_guilds, steam_state, steam_since, steam_last_result, steam_token_expires)
                            VALUES (1, @run_id, @version, @started_at, @heartbeat_at, @stopped_at, @discord_state, @discord_since,
                            @discord_latency_ms, @discord_guilds, @steam_state, @steam_since, @steam_last_result, @steam_token_expires)
                            ON DUPLICATE KEY UPDATE version = IF({Stopped}, version, VALUES(version)),
                            started_at = IF({Stopped}, started_at, VALUES(started_at)), heartbeat_at = IF({Stopped}, heartbeat_at, VALUES(heartbeat_at)),
                            discord_state = IF({Stopped}, discord_state, VALUES(discord_state)), discord_since = IF({Stopped}, discord_since, VALUES(discord_since)),
                            discord_latency_ms = IF({Stopped}, discord_latency_ms, VALUES(discord_latency_ms)),
                            discord_guilds = IF({Stopped}, discord_guilds, VALUES(discord_guilds)), steam_state = IF({Stopped}, steam_state, VALUES(steam_state)),
                            steam_since = IF({Stopped}, steam_since, VALUES(steam_since)), steam_last_result = IF({Stopped}, steam_last_result, VALUES(steam_last_result)),
                            steam_token_expires = IF({Stopped}, steam_token_expires, VALUES(steam_token_expires)),
                            stopped_at = IF({Stopped}, stopped_at, VALUES(stopped_at)), run_id = VALUES(run_id)";

            return SaveDataCount(sql, new
            {
                status.run_id,
                status.version,
                status.started_at,
                status.heartbeat_at,
                status.stopped_at,
                status.discord_state,
                status.discord_since,
                status.discord_latency_ms,
                status.discord_guilds,
                status.steam_state,
                status.steam_since,
                steam_last_result = Clip(status.steam_last_result, ResultNameLimit),
                // MySql.Data sends a DateOnly as the culture's short date, which MariaDB refuses, so it goes as a time
                steam_token_expires = status.steam_token_expires?.ToDateTime(TimeOnly.MinValue)
            });
        }

        // Not INSERT IGNORE, which would also let a detail that's too long through, cut, without an error. A row that's
        // already there stays as it is.
        public Task AddEvents(IReadOnlyList<BotEvent> events)
        {
            RequireBatch(events);

            foreach (var row in events)
            {
                RequireUtc(row.at, nameof(row.at));
                RequireSeq(row.seq);

                if (row.kind == null || row.kind.Length > KindLimit || !Kind.IsMatch(row.kind))
                    throw new ArgumentException($"An event's kind is words in small letters joined by one dot, such as discord.connected, in at most {KindLimit} characters", nameof(events));

                if (!EventLevel.IsKnown(row.level))
                    throw new ArgumentException($"An event's level is one of: {string.Join(", ", EventLevel.All)}", nameof(events));
            }

            if (events.Count == 0)
                return Task.CompletedTask;

            var (values, parameters) = Rows(events,
                ("run_id", row => row.run_id),
                ("seq", row => row.seq),
                ("at", row => row.at),
                ("kind", row => row.kind),
                ("level", row => row.level),
                ("detail", row => Clip(row.detail, DetailLimit)));

            string sql = $@"INSERT INTO {_names.Bot}.bot_events (run_id, seq, at, kind, level, detail)
                            VALUES {values}
                            ON DUPLICATE KEY UPDATE id = id";

            return SaveDataCount(sql, parameters);
        }

        // A run that was cut off is written when it's cut off and again when it ends. Its row then gets the end, and stays
        // cut_off. Once a row has its end nothing changes it, so an older write that lands late can't take the end away.
        // MariaDB applies the assignments in order, so the ones that look at the row's end come before the one that sets it.
        public Task SaveJobRuns(IReadOnlyList<JobRun> runs)
        {
            RequireBatch(runs);

            foreach (var row in runs)
            {
                RequireUtc(row.started_at, nameof(row.started_at));
                RequireUtcOrNull(row.ended_at, nameof(row.ended_at));
                RequireSeq(row.seq);
                RequireFits(row.job, JobLimit, "job");

                if (!JobResult.IsKnown(row.result))
                    throw new ArgumentException($"A job run's result is one of: {string.Join(", ", JobResult.All)}", nameof(runs));
            }

            if (runs.Count == 0)
                return Task.CompletedTask;

            var (values, parameters) = Rows(runs,
                ("run_id", row => row.run_id),
                ("seq", row => row.seq),
                ("job", row => row.job),
                ("started_at", row => row.started_at),
                ("ended_at", row => row.ended_at),
                ("result", row => row.result),
                ("duration_ms", row => row.duration_ms),
                ("detail", row => Clip(row.detail, DetailLimit)));

            parameters.Add("cut_off", JobResult.CutOff);

            string sql = $@"INSERT INTO {_names.Bot}.job_runs (run_id, seq, job, started_at, ended_at, result, duration_ms, detail)
                            VALUES {values}
                            ON DUPLICATE KEY UPDATE detail = IF(ended_at IS NULL, VALUES(detail), detail),
                            duration_ms = IF(ended_at IS NULL, VALUES(duration_ms), duration_ms),
                            result = IF(result = @cut_off, result, VALUES(result)),
                            ended_at = IF(ended_at IS NULL, VALUES(ended_at), ended_at)";

            return SaveDataCount(sql, parameters);
        }

        // A job's status without a last run, as each start writes it, changes only its schedule and next run, and keeps
        // the last run the row has
        public Task SaveJobStatus(IReadOnlyList<JobStatus> jobs)
        {
            RequireBatch(jobs);

            foreach (var row in jobs)
            {
                RequireFits(row.job, JobLimit, "job");
                RequireFits(row.schedule, ScheduleLimit, "schedule");
                RequireUtcOrNull(row.next_run_at, nameof(row.next_run_at));
                RequireUtcOrNull(row.last_started_at, nameof(row.last_started_at));
                RequireUtcOrNull(row.last_ended_at, nameof(row.last_ended_at));
                RequireUtc(row.updated_at, nameof(row.updated_at));

                if (string.IsNullOrEmpty(row.job) || row.schedule == null)
                    throw new ArgumentException("A job's status needs its name and its schedule", nameof(jobs));

                if (row.last_result != null && !JobResult.IsKnown(row.last_result))
                    throw new ArgumentException($"A job's last result is one of: {string.Join(", ", JobResult.All)}", nameof(jobs));
            }

            if (jobs.Count == 0)
                return Task.CompletedTask;

            var (values, parameters) = Rows(jobs,
                ("job", row => row.job),
                ("schedule", row => row.schedule),
                ("next_run_at", row => row.next_run_at),
                ("last_started_at", row => row.last_started_at),
                ("last_ended_at", row => row.last_ended_at),
                ("last_result", row => row.last_result),
                ("last_duration_ms", row => row.last_duration_ms),
                ("last_detail", row => Clip(row.last_detail, DetailLimit)),
                ("updated_at", row => row.updated_at));

            string sql = $@"INSERT INTO {_names.Bot}.job_status (job, schedule, next_run_at, last_started_at, last_ended_at, last_result,
                            last_duration_ms, last_detail, updated_at)
                            VALUES {values}
                            ON DUPLICATE KEY UPDATE schedule = VALUES(schedule), next_run_at = VALUES(next_run_at),
                            last_ended_at = IF(VALUES(last_started_at) IS NULL, last_ended_at, VALUES(last_ended_at)),
                            last_result = IF(VALUES(last_started_at) IS NULL, last_result, VALUES(last_result)),
                            last_duration_ms = IF(VALUES(last_started_at) IS NULL, last_duration_ms, VALUES(last_duration_ms)),
                            last_detail = IF(VALUES(last_started_at) IS NULL, last_detail, VALUES(last_detail)),
                            last_started_at = COALESCE(VALUES(last_started_at), last_started_at),
                            updated_at = VALUES(updated_at)";

            return SaveDataCount(sql, parameters);
        }

        // The rows of jobs the bot no longer has, such as one that was renamed. An empty list is refused, because NOT IN
        // an empty list would delete every row.
        public Task<int> DeleteOtherJobs(IReadOnlyCollection<string> jobs)
        {
            if (jobs == null || jobs.Count == 0)
                throw new ArgumentException("The list of the jobs to keep is empty, which would delete every job's status", nameof(jobs));

            string sql = $@"DELETE FROM {_names.Bot}.job_status
                            WHERE job NOT IN @jobs";

            return SaveDataCount(sql, new { jobs });
        }

        public Task SaveSamples(IReadOnlyList<UptimeSample> samples)
        {
            RequireBatch(samples);

            foreach (var row in samples)
            {
                RequireUtc(row.minute, nameof(row.minute));

                if (row.minute.Ticks % TimeSpan.TicksPerMinute != 0)
                    throw new ArgumentException("A sample's minute is the minute's start, with no seconds", nameof(samples));
            }

            if (samples.Count == 0)
                return Task.CompletedTask;

            var (values, parameters) = Rows(samples,
                ("minute", row => row.minute),
                ("discord_up", row => row.discord_up),
                ("steam_up", row => row.steam_up));

            string sql = $@"INSERT INTO {_names.Bot}.uptime_samples (minute, discord_up, steam_up)
                            VALUES {values}
                            ON DUPLICATE KEY UPDATE discord_up = VALUES(discord_up), steam_up = VALUES(steam_up)";

            return SaveDataCount(sql, parameters);
        }

        // One statement for the whole batch, with each row's parameters numbered, such as @seq0 and @seq1. The names are
        // the columns', which only this code gives.
        static (string Values, DynamicParameters Parameters) Rows<T>(IReadOnlyList<T> rows, params (string Column, Func<T, object?> Value)[] columns)
        {
            var values = new StringBuilder();
            var parameters = new DynamicParameters();

            for (var i = 0; i < rows.Count; i++)
            {
                values.Append(i == 0 ? "(" : ", (");

                for (var c = 0; c < columns.Length; c++)
                {
                    var name = $"{columns[c].Column}{i}";
                    values.Append(c == 0 ? "@" : ", @").Append(name);
                    parameters.Add(name, columns[c].Value(rows[i]));
                }

                values.Append(')');
            }

            return (values.ToString(), parameters);
        }

        static void RequireBatch<T>(IReadOnlyList<T> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);

            if (rows.Count > BatchLimit)
                throw new ArgumentException($"One call writes at most {BatchLimit} rows", nameof(rows));
        }

        static void RequireSeq(uint seq)
        {
            if (seq == 0)
                throw new ArgumentException("Sequence numbers count from 1", nameof(seq));
        }

        static void RequireUtcOrNull(DateTime? time, string name)
        {
            if (time is { } value)
                RequireUtc(value, name);
        }

        static void RequireFits(string? text, int limit, string what)
        {
            if (text != null && CodePoints(text) > limit)
                throw new ArgumentException($"A {what} holds at most {limit} characters", nameof(text));
        }
    }
}
