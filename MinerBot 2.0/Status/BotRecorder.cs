using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using SteamWebAPIAccess;
using System.Globalization;
using System.Security.Cryptography;

namespace MinerBot_2._0.Status
{
    // What the bot records about itself, held here until StatusWriter has written it to MinerBot's database. The rest of the
    // bot only calls this: every method is quick, does no I/O, never throws and is safe from any thread, so recording can
    // never hold anything up. Each queue has a limit, and when one is full its oldest item goes and is counted, so a
    // database that's down for hours costs the oldest history, never the bot's memory or its work. A call after the writer
    // has stopped, such as Discord's last disconnect while the bot shuts down, is kept and never written.
    public sealed class BotRecorder
    {
        // Samples are one a minute, so 360 is 6 hours of them, and lobby samples one a notification run, every 5 minutes, so
        // 72 is 6 hours of those
        public const int EventLimit = 1000;
        public const int JobRunLimit = 500;
        public const int SampleLimit = 360;
        public const int LobbySampleLimit = 72;

        readonly object gate = new();
        readonly TimeProvider _clock;

        readonly Queue<BotEvent> events = new();
        readonly Queue<JobRun> jobRuns = new();
        readonly Queue<UptimeSample> samples = new();
        readonly Queue<LobbySample> lobbySamples = new();
        readonly Dictionary<string, JobEntry> jobs = new(StringComparer.Ordinal);
        List<string>? jobNames;

        uint eventSeq, jobRunSeq;
        int droppedEvents, droppedJobRuns, droppedSamples, droppedLobbySamples;

        // The status row as the sampler last saw it. The version grows with each change of a state, which the writer
        // writes at its next tick rather than waiting for the next 30 seconds.
        readonly BotStatus status;
        long statusVersion;

        // A job's status, with a version that grows with each change, and the newest run it shows
        sealed class JobEntry(JobStatus status)
        {
            public readonly JobStatus Status = status;
            public long Version, Written;
            public uint LastSeq;
        }

        public BotRecorder(TimeProvider clock, IConfiguration configuration)
        {
            _clock = clock;
            RunId = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(sizeof(ulong)));
            StartedAt = Now();
            Version = BotVersion.Parse(configuration[BotVersion.Setting]);

            status = new BotStatus
            {
                run_id = RunId,
                version = Version,
                started_at = StartedAt,
                heartbeat_at = StartedAt,
                discord_state = DiscordState.Disconnected,
                discord_since = StartedAt,
                steam_state = SteamState.Offline,
                steam_since = StartedAt
            };

            Record(BotEvents.BotStarted, Version == null ? "with no version, because the image wasn't built from a commit" : $"version {Version}");
        }

        // Random for each start of the process, so the status row tells one run from the next
        public ulong RunId { get; }

        // UTC
        public DateTime StartedAt { get; }

        // The commit, or null when the image has none
        public string? Version { get; }

        // An event of one of BotEvents' kinds. Any other kind is dropped.
        public void Record(string kind, string? detail = null)
        {
            try
            {
                if (BotEvents.LevelOf(kind) is not { } level)
                    return;

                lock (gate)
                {
                    Add(events, new BotEvent { run_id = RunId, seq = ++eventSeq, at = Now(), kind = kind, level = level, detail = detail },
                        EventLimit, ref droppedEvents);
                }
            }
            catch (Exception)
            {
                // Recording never stops the caller
            }
        }

        // Every job JobHandler found at its start, with its schedule and next run, UTC. The first write of the job status
        // also deletes the rows of jobs that aren't here any more.
        public void JobsFound(IEnumerable<(string Job, string Schedule, DateTime NextRun)> found)
        {
            try
            {
                lock (gate)
                {
                    var now = Now();
                    jobNames = [];

                    foreach (var (job, schedule, nextRun) in found)
                    {
                        jobNames.Add(job);

                        var entry = Entry(job, schedule);
                        entry.Status.next_run_at = nextRun;
                        entry.Status.updated_at = now;
                        entry.Version++;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        // A new job run's number, from 1 in each run of the bot
        public uint NewJobRun()
        {
            lock (gate)
                return ++jobRunSeq;
        }

        // A job run as it ended, or as it was cut off, and its job's status after it. A run that was cut off and ends
        // after a newer run of its job began changes only its own row.
        public void RecordJobRun(JobRun run, string schedule, DateTime? nextRun)
        {
            try
            {
                lock (gate)
                {
                    run.run_id = RunId;
                    Add(jobRuns, run, JobRunLimit, ref droppedJobRuns);

                    var entry = Entry(run.job, schedule);

                    if (run.seq >= entry.LastSeq)
                    {
                        entry.LastSeq = run.seq;
                        entry.Status.last_started_at = run.started_at;
                        entry.Status.last_ended_at = run.ended_at;
                        entry.Status.last_result = run.result;
                        entry.Status.last_duration_ms = run.duration_ms;
                        entry.Status.last_detail = run.detail;
                    }

                    if (nextRun != null)
                        entry.Status.next_run_at = nextRun;

                    entry.Status.updated_at = Now();
                    entry.Version++;
                }
            }
            catch (Exception)
            {
            }
        }

        // A minute the bot ran, by its start, UTC
        public void Sample(DateTime minute, bool discordUp, bool steamUp)
        {
            try
            {
                lock (gate)
                    Add(samples, new UptimeSample { minute = minute, discord_up = discordUp, steam_up = steamUp }, SampleLimit, ref droppedSamples);
            }
            catch (Exception)
            {
            }
        }

        // The open lobbies at a notification run's read, counts only, for the analytics: StatusWriter writes them beside the
        // uptime samples, so a database that's down never holds up the run
        public void SampleLobbies(LobbySample sample)
        {
            try
            {
                if (sample == null)
                    return;

                lock (gate)
                    Add(lobbySamples, sample, LobbySampleLimit, ref droppedLobbySamples);
            }
            catch (Exception)
            {
            }
        }

        // What the sampler read of Discord and Steam. Discord's since is when the state began, by the status handler's own
        // time of the connection or the drop, which the reading carries: so "Back on Discord" is the moment itself, not the
        // sampler's next tick, up to 5 seconds later. A reading can show the connection a moment before the handler's event
        // runs, and connecting has no event, so a time that isn't newer than the since before it, or none, is the reading's own.
        public void UpdateStatus(DiscordReading discord, SteamStatus steam)
        {
            try
            {
                var steamState = steam.State switch
                {
                    SteamConnection.LoggedOn => SteamState.LoggedOn,
                    SteamConnection.NeedsSignIn => SteamState.NeedsSignIn,
                    _ => SteamState.Offline
                };

                lock (gate)
                {
                    if (status.discord_state != discord.State || status.steam_state != steamState)
                        statusVersion++;

                    if (status.discord_state != discord.State)
                        status.discord_since = discord.State != DiscordState.Connecting && discord.ChangedAt is { } changed && changed > status.discord_since
                            ? changed
                            : Now();

                    status.discord_state = discord.State;
                    status.discord_latency_ms = discord.LatencyMs;
                    status.discord_guilds = discord.Guilds;
                    status.steam_state = steamState;
                    status.steam_since = steam.Since.UtcDateTime;
                    status.steam_last_result = steam.State == SteamConnection.LoggedOn ? null : steam.LastLogOnResult?.ToString();
                    status.steam_token_expires = steam.TokenExpires;
                }
            }
            catch (Exception)
            {
            }
        }

        // For StatusWriter, which only takes items out once their write went through

        internal (BotStatus Row, long Version) StatusToWrite()
        {
            lock (gate)
            {
                var row = new BotStatus
                {
                    run_id = status.run_id,
                    version = status.version,
                    started_at = status.started_at,
                    discord_state = status.discord_state,
                    discord_since = status.discord_since,
                    discord_latency_ms = status.discord_latency_ms,
                    discord_guilds = status.discord_guilds,
                    steam_state = status.steam_state,
                    steam_since = status.steam_since,
                    steam_last_result = status.steam_last_result,
                    steam_token_expires = status.steam_token_expires
                };

                return (row, statusVersion);
            }
        }

        internal List<BotEvent> PeekEvents(int count) => Peek(events, count);
        internal List<JobRun> PeekJobRuns(int count) => Peek(jobRuns, count);
        internal List<UptimeSample> PeekSamples(int count) => Peek(samples, count);
        internal List<LobbySample> PeekLobbySamples(int count) => Peek(lobbySamples, count);

        internal void EventsWritten(IReadOnlyCollection<BotEvent> written) => Remove(events, written);
        internal void JobRunsWritten(IReadOnlyCollection<JobRun> written) => Remove(jobRuns, written);
        internal void SamplesWritten(IReadOnlyCollection<UptimeSample> written) => Remove(samples, written);
        internal void LobbySamplesWritten(IReadOnlyCollection<LobbySample> written) => Remove(lobbySamples, written);

        // Copies of the jobs whose status changed since it was last written, with the version each copy has
        internal List<(JobStatus Row, long Version)> PeekJobStatus(int count)
        {
            lock (gate)
            {
                return jobs.Values
                    .Where(entry => entry.Version > entry.Written)
                    .Take(count)
                    .Select(entry => (Copy(entry.Status), entry.Version))
                    .ToList();
            }
        }

        // A job that changed again since its copy was taken stays to be written
        internal void JobStatusWritten(IReadOnlyCollection<(JobStatus Row, long Version)> written)
        {
            lock (gate)
            {
                foreach (var (row, version) in written)
                {
                    if (jobs.TryGetValue(row.job, out var entry) && entry.Written < version)
                        entry.Written = version;
                }
            }
        }

        // Null until JobHandler has found its jobs
        internal IReadOnlyList<string>? JobNames
        {
            get
            {
                lock (gate)
                    return jobNames?.ToList();
            }
        }

        // How many items each limit dropped since the last time this was asked
        internal (int Events, int JobRuns, int Minutes, int LobbySamples) TakeDropped()
        {
            lock (gate)
            {
                var dropped = (droppedEvents, droppedJobRuns, droppedSamples, droppedLobbySamples);
                droppedEvents = droppedJobRuns = droppedSamples = droppedLobbySamples = 0;
                return dropped;
            }
        }

        // records.dropped, with what each limit dropped since the last time this was asked, unless nothing was. A full
        // event queue makes room for it first, so the event that goes is in its count: the stop's write records this
        // before it writes, and no write after it could count that one.
        internal void RecordDropped()
        {
            try
            {
                lock (gate)
                {
                    if (droppedEvents + droppedJobRuns + droppedSamples + droppedLobbySamples == 0)
                        return;

                    while (events.Count >= EventLimit)
                    {
                        events.Dequeue();
                        droppedEvents++;
                    }

                    var (lostEvents, lostJobRuns, lostMinutes, lostLobbySamples) = TakeDropped();

                    Record(BotEvents.RecordsDropped, string.Create(CultureInfo.InvariantCulture,
                        $"{lostEvents} events, {lostJobRuns} job runs, {lostMinutes} minutes and {lostLobbySamples} lobby samples, the oldest, because the bot couldn't keep more"));
                }
            }
            catch (Exception)
            {
            }
        }

        DateTime Now() => _clock.GetUtcNow().UtcDateTime;

        JobEntry Entry(string job, string schedule)
        {
            if (!jobs.TryGetValue(job, out var entry))
            {
                entry = new JobEntry(new JobStatus { job = job });
                jobs[job] = entry;
            }

            entry.Status.schedule = schedule;
            return entry;
        }

        static JobStatus Copy(JobStatus status) => new()
        {
            job = status.job,
            schedule = status.schedule,
            next_run_at = status.next_run_at,
            last_started_at = status.last_started_at,
            last_ended_at = status.last_ended_at,
            last_result = status.last_result,
            last_duration_ms = status.last_duration_ms,
            last_detail = status.last_detail,
            updated_at = status.updated_at
        };

        static void Add<T>(Queue<T> queue, T item, int limit, ref int dropped)
        {
            queue.Enqueue(item);

            while (queue.Count > limit)
            {
                queue.Dequeue();
                dropped++;
            }
        }

        List<T> Peek<T>(Queue<T> queue, int count)
        {
            lock (gate)
                return queue.Take(count).ToList();
        }

        // The batch that was written was the front of its queue when it was taken. The limit may have dropped some of it
        // since, but only from the front, so what's left of it is still the front.
        void Remove<T>(Queue<T> queue, IReadOnlyCollection<T> written) where T : class
        {
            lock (gate)
            {
                var items = new HashSet<T>(written, ReferenceEqualityComparer.Instance);

                while (queue.TryPeek(out var first) && items.Contains(first))
                    queue.Dequeue();
            }
        }
    }
}
