using MinerBot_2._0.Attributes;
using Cronos;
using System.Globalization;
using System.Reflection;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using MinerBot_2._0.Models;
using MinerBot_2._0.Status;

namespace MinerBot_2._0.Handlers
{
    // How a run asked for from the dashboard went, as far as JobHandler knows when the request stops waiting: not started,
    // because the jobs aren't found yet or a run of that job is under way, or ok, failed or still running, after Took. Detail
    // is the job's own detail, or its failure's.
    public enum RunNowState
    {
        NotFound,
        AlreadyRunning,
        Ok,
        Failed,
        StillRunning
    }

    public sealed record RunNowResult(RunNowState State, TimeSpan Took = default, string? Detail = null);

    public class JobHandler
    {
        private readonly ILogger<JobHandler> _logger;
        private readonly IServiceProvider _service;
        private List<Job>? _jobs;

        // Each job's runs and next run, which the schedule's loop and a request from the dashboard both use
        private readonly object gate = new();
        private volatile bool startFailed;

        // A little under the 5 minutes between notification runs, so a stuck run is cut off before the next one is due.
        // Shorter in tests.
        internal TimeSpan JobTimeLimit = TimeSpan.FromSeconds(270);

        // Whether StartAsync has found the jobs, so a request for one can run, and whether it failed, which the Worker says,
        // so a request for one is refused rather than left waiting for a start that won't come
        public bool IsStarted => Volatile.Read(ref _jobs) != null;
        public bool StartFailed { get => startFailed; set => startFailed = value; }

        public JobHandler(ILogger<JobHandler> logger, IServiceProvider service) 
        {
            _logger = logger;
            _service = service;
        }

        // Asked for when needed, so a test's services without them still run jobs, unrecorded
        BotRecorder? Recorder => _service.GetService<BotRecorder>();
        TimeProvider Clock => _service.GetService<TimeProvider>() ?? TimeProvider.System;

        // As every log line names a job, and as the bot's status does
        internal static string NameOf(Job job) => $"{job.DeclaringType.Name}.{job.Name}";

        DateTimeOffset GetNextRun(string cronString)
        {
            TimeZoneInfo easternTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            return CronExpression.Parse(cronString).GetNextOccurrence(DateTimeOffset.UtcNow, easternTimeZone).Value;
        }

        // Virtual so a test can make the start fail
        public virtual Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting JobService");

            var jobs = typeof(JobHandler).Assembly.GetTypes()
                      .SelectMany(t => t.GetMethods())
                      .Where(m => m.GetCustomAttributes(typeof(JobAttribute), false).Length > 0)
                      .Select(x =>
                        new Job
                        {
                            Name = x.Name,
                            MethodInfo = x,
                            Details = x.GetCustomAttribute<JobAttribute>(),
                            DeclaringType = x.DeclaringType,
                            NextRun = GetNextRun(x.GetCustomAttribute<JobAttribute>().Cron)
                        })
                      .ToList();

            // Every job's schedule and next run, for the bot's status
            Recorder?.JobsFound(jobs.Select(job => (NameOf(job), job.Details.Cron, job.NextRun.UtcDateTime)));

            Volatile.Write(ref _jobs, jobs);

            // On the thread pool, so no job runs on the thread that started the handler
            _ = Task.Run(() => Main(cancellationToken));
            return Task.CompletedTask;
        }

        // A run that has started, with its number in the bot's history, when it started and how long it has run
        private sealed record Started(Task<string?> Run, uint Seq, DateTime StartedAt, System.Diagnostics.Stopwatch Timer);

        // Starts a run of the job, unless one is under way, under the lock, so the schedule and a request from the dashboard
        // never run one job twice at once: two runs of the notification job would share its state, and could post the same
        // thing twice. Different jobs may run at once, since they share nothing.
        private Started? TryStart(Job job, ulong? request)
        {
            lock (gate)
            {
                if (job.Running is { IsCompleted: false })
                    return null;

                var startedAt = Clock.GetUtcNow();
                var seq = Recorder?.NewJobRun() ?? 0;
                var timer = System.Diagnostics.Stopwatch.StartNew();

                // On the thread pool too, because a job that blocks before it hands back its task would hold up the limit
                var run = Task.Run(() => InvokeJobAsync(job));

                job.Running = run;
                job.RunningSince = startedAt;
                job.Requested = request != null;

                return new Started(run, seq, startedAt.UtcDateTime, timer);
            }
        }

        // The job named as the bot names its jobs, <class>.<method>, once StartAsync has found the jobs
        private Job? Find(string jobName) => Volatile.Read(ref _jobs)?.FirstOrDefault(job => NameOf(job) == jobName);

        // Whether a run of the job is under way, so a request for it is refused
        public bool IsRunning(string jobName)
        {
            if (Find(jobName) is not { } job)
                return false;

            lock (gate)
                return job.Running is { IsCompleted: false };
        }

        // Whether a run of the job is under way and not yet past the time limit, for a restart, which would cut it in half: a
        // stop doesn't wait for a running job. A run that was cut off doesn't count, since a restart is the cure for one.
        public bool Runs(string jobName)
        {
            if (Find(jobName) is not { } job)
                return false;

            lock (gate)
                return job.Running is { IsCompleted: false } && job.RunningSince is { } since && Clock.GetUtcNow() - since < JobTimeLimit;
        }

        // For a request from the dashboard: starts the job now, beside its schedule, unless a run of it is under way, and
        // waits up to the time given for the run to end. The run is recorded as any run is, its detail naming the request,
        // and its end is recorded after the wait too. A scheduled run that comes due meanwhile is skipped.
        public Task<RunNowResult> RunNowAsync(string jobName, ulong request, TimeSpan wait, CancellationToken stop) =>
            Find(jobName) is { } job ? RunNowAsync(job, request, wait, stop) : Task.FromResult(new RunNowResult(RunNowState.NotFound));

        internal async Task<RunNowResult> RunNowAsync(Job job, ulong request, TimeSpan wait, CancellationToken stop)
        {
            if (TryStart(job, request) is not { } started)
                return new RunNowResult(RunNowState.AlreadyRunning);

            _logger.LogInformation("Running Job {jobDeclareType}.{jobName} now, as dashboard request {request} asked", job.DeclaringType.Name, job.Name, request);

            var recording = RunJobAsync(job, started, request, stop);

            try
            {
                return await recording.WaitAsync(wait, stop);
            }
            catch (TimeoutException) when (!recording.IsCompleted)
            {
                return new RunNowResult(RunNowState.StillRunning, started.Timer.Elapsed);
            }
        }

        // Each run is recorded for the bot's history: ok, failed, or cut off and then its end. A run that the bot's stop
        // interrupts isn't, because bot.stopping already says why it ended. A run asked for from the dashboard says so first.
        private async Task<RunNowResult> RunJobAsync(Job job, Started started, ulong? request, CancellationToken cancellationToken)
        {
            var recorder = Recorder;
            var clock = Clock;
            var (run, seq, startedAt, timer) = started;

            try
            {
                var detail = await run.WaitAsync(JobTimeLimit, cancellationToken);
                _logger.LogInformation("{jobDeclareType}.{jobName} took {timerHumanized}",
                    job.DeclaringType.Name, job.Name, timer.Elapsed.ToString());

                Record(JobResult.Ok, detail, ended: true);
                return new RunNowResult(RunNowState.Ok, timer.Elapsed, detail);
            }
            // A job that throws a TimeoutException of its own has failed, it wasn't cut off
            catch (TimeoutException) when (!run.IsCompleted)
            {
                _logger.LogWarning("{jobDeclareType}.{jobName} was cut off after {timeLimit}. It carries on in the background, and this job isn't started again until it ends.",
                    job.DeclaringType.Name, job.Name, JobTimeLimit.ToString());

                Record(JobResult.CutOff, Detail($"still running when the time limit of {JobTimeLimit.TotalSeconds:0} s cut it off"), ended: false);
                recorder?.Record(BotEvents.JobCutOff, Detail($"{NameOf(job)} was cut off after {JobTimeLimit.TotalSeconds:0} s"));

                // Otherwise nothing would show when, and how, it ended
                _ = run.ContinueWith(ended =>
                {
                    if (ended.IsFaulted)
                        _logger.LogError(ended.Exception, "{jobDeclareType}.{jobName}, which was cut off, failed after {timerHumanized}",
                            job.DeclaringType.Name, job.Name, timer.Elapsed.ToString());
                    else
                        _logger.LogInformation("{jobDeclareType}.{jobName}, which was cut off, ended after {timerHumanized}",
                            job.DeclaringType.Name, job.Name, timer.Elapsed.ToString());

                    // Its row gets its end, and stays cut_off
                    Record(JobResult.CutOff, ended.IsFaulted
                        ? Detail($"failed after {timer.Elapsed.TotalSeconds:0} s: {FailureOf(ended.Exception)}")
                        : Detail($"ended after {timer.Elapsed.TotalSeconds:0} s"), ended: true);
                }, TaskScheduler.Default);

                return new RunNowResult(RunNowState.StillRunning, timer.Elapsed);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(e, "{jobDeclareType}.{jobName} failed", job.DeclaringType.Name, job.Name);

                Record(JobResult.Failed, FailureOf(e), ended: true);
                recorder?.Record(BotEvents.JobFailed, Detail($"{NameOf(job)} failed: {FailureOf(e)}"));

                return new RunNowResult(RunNowState.Failed, timer.Elapsed, FailureOf(e));
            }

            void Record(string result, string? detail, bool ended) => recorder?.RecordJobRun(new JobRun
            {
                seq = seq,
                job = NameOf(job),
                started_at = startedAt,
                ended_at = ended ? clock.GetUtcNow().UtcDateTime : null,
                result = result,
                duration_ms = ended ? (uint)Math.Min(timer.Elapsed.TotalMilliseconds, uint.MaxValue) : null,
                detail = request is { } asked ? Detail($"asked for on request {asked}{(detail == null ? "" : ": " + detail)}") : detail
            }, job.Details.Cron, NextRunOf(job));
        }

        // Under the lock, since the schedule's loop changes it
        private DateTime NextRunOf(Job job)
        {
            lock (gate)
                return job.NextRun.UtcDateTime;
        }

        // A job may return Task<string>, whose text becomes its run's detail, such as the clean-up's counts
        private async Task<string?> InvokeJobAsync(Job job)
        {
            var jobClass = ActivatorUtilities.CreateInstance(_service, job.DeclaringType);

            switch (job.MethodInfo.Invoke(jobClass, null))
            {
                case Task<string> text:
                    return await text;
                case Task task:
                    await task;
                    break;
            }

            return null;
        }

        // What a failed run's detail says: the exception's type, never its message, which can hold anything, unless it's a
        // job's own failure in the bot's wording. A job that throws before it hands back its task arrives wrapped.
        internal static string FailureOf(Exception? e)
        {
            while (e is AggregateException { InnerException: not null } or TargetInvocationException { InnerException: not null })
                e = e.InnerException;

            return e switch
            {
                JobFailedException failed => failed.Message,
                null => "no exception given",
                _ => e.GetType().Name
            };
        }

        static string Detail(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

        private async Task Main(CancellationToken cancellationToken)
        {
            var jobs = Volatile.Read(ref _jobs)!;

            while (!cancellationToken.IsCancellationRequested)
            {
                await RunDueJobsAsync(jobs, cancellationToken);

                Job? nextJob;
                DateTimeOffset nextRun;

                lock (gate)
                {
                    nextJob = jobs.OrderBy(x => x.NextRun).FirstOrDefault();
                    nextRun = nextJob?.NextRun ?? default;
                }

                if (nextJob != null)
                {
                    var delay = (nextRun - DateTimeOffset.Now) + TimeSpan.FromMilliseconds(10);
                    if (delay < TimeSpan.Zero)
                    {
                        delay = TimeSpan.FromSeconds(1);
                    }
                    _logger.LogTrace("Next job {jobDeclareType}.{jobName} in {delaySeconds} seconds, delaying for {delaySeconds} seconds",
                        nextJob.DeclaringType.Name, nextJob.Name, delay.TotalSeconds, delay.TotalSeconds);
                    await Task.Delay((int)delay.TotalMilliseconds, cancellationToken);
                }
                else
                {
                    _logger.LogTrace($"No jobs found, delaying for 1 second");
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }

        // One pass over the jobs that are due. A job that fails or is cut off doesn't stop the others.
        internal async Task RunDueJobsAsync(List<Job> jobs, CancellationToken cancellationToken)
        {
            try
            {
                List<Job> dueJobs;

                lock (gate)
                    dueJobs = jobs.Where(x => x.NextRun < DateTimeOffset.Now).OrderBy(x => x.NextRun).ToList();

                foreach (var job in dueJobs)
                {
                    Started? started;
                    bool requested;
                    DateTimeOffset nextRun;

                    lock (gate)
                    {
                        job.NextRun = nextRun = GetNextRun(job.Details.Cron);

                        // Two runs of a job at once would share its state, and could post the same thing twice
                        started = TryStart(job, request: null);
                        requested = job.Requested;
                    }

                    if (started == null)
                    {
                        _logger.LogWarning(requested
                            ? "{jobDeclareType}.{jobName} is still running from a run asked for from the dashboard, so this run is skipped"
                            : "{jobDeclareType}.{jobName} is still running from a run that was cut off, so this run is skipped",
                            job.DeclaringType.Name, job.Name);

                        if (Recorder is { } recorder)
                        {
                            var now = Clock.GetUtcNow().UtcDateTime;

                            recorder.RecordJobRun(new JobRun
                            {
                                seq = recorder.NewJobRun(),
                                job = NameOf(job),
                                started_at = now,
                                ended_at = now,
                                result = JobResult.Skipped,
                                detail = requested ? "the run asked for from the dashboard still runs" : "the run that was cut off still runs"
                            }, job.Details.Cron, nextRun.UtcDateTime);
                        }

                        continue;
                    }

                    _logger.LogInformation("Running Job {jobDeclareType}.{jobName}, Current time: {currentTime}, next run at {nextRun}",
                        job.DeclaringType.Name, job.Name, $"{DateTimeOffset.UtcNow:HH:mm:ss}", $"{nextRun.UtcDateTime:HH:mm:ss}");
                    await RunJobAsync(job, started, request: null, cancellationToken);
                }
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(e, "Error running job");
            }
        }
    }
}
