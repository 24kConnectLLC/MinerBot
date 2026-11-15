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
    public class JobHandler
    {
        private readonly ILogger<JobHandler> _logger;
        private readonly IServiceProvider _service;
        private List<Job> _jobs;

        // A little under the 5 minutes between notification runs, so a stuck run is cut off before the next one is due.
        // Shorter in tests.
        internal TimeSpan JobTimeLimit = TimeSpan.FromSeconds(270);

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

            _jobs = typeof(JobHandler).Assembly.GetTypes()
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
            Recorder?.JobsFound(_jobs.Select(job => (NameOf(job), job.Details.Cron, job.NextRun.UtcDateTime)));

            // On the thread pool, so no job runs on the thread that started the handler
            _ = Task.Run(() => Main(cancellationToken));
            return Task.CompletedTask;
        }

        // Each run is recorded for the bot's history: ok, failed, or cut off and then its end. A run that the bot's stop
        // interrupts isn't, because bot.stopping already says why it ended.
        private async Task RunJobAsync(Job job, CancellationToken cancellationToken)
        {
            var recorder = Recorder;
            var clock = Clock;
            var seq = recorder?.NewJobRun() ?? 0;
            var startedAt = clock.GetUtcNow().UtcDateTime;
            var timer = System.Diagnostics.Stopwatch.StartNew();

            // On the thread pool too, because a job that blocks before it hands back its task would hold up the limit
            var run = Task.Run(() => InvokeJobAsync(job));
            job.Running = run;

            try
            {
                var detail = await run.WaitAsync(JobTimeLimit, cancellationToken);
                _logger.LogInformation("{jobDeclareType}.{jobName} took {timerHumanized}",
                    job.DeclaringType.Name, job.Name, timer.Elapsed.ToString());

                Record(JobResult.Ok, detail, ended: true);
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
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(e, "{jobDeclareType}.{jobName} failed", job.DeclaringType.Name, job.Name);

                Record(JobResult.Failed, FailureOf(e), ended: true);
                recorder?.Record(BotEvents.JobFailed, Detail($"{NameOf(job)} failed: {FailureOf(e)}"));
            }

            void Record(string result, string? detail, bool ended) => recorder?.RecordJobRun(new JobRun
            {
                seq = seq,
                job = NameOf(job),
                started_at = startedAt,
                ended_at = ended ? clock.GetUtcNow().UtcDateTime : null,
                result = result,
                duration_ms = ended ? (uint)Math.Min(timer.Elapsed.TotalMilliseconds, uint.MaxValue) : null,
                detail = detail
            }, job.Details.Cron, job.NextRun.UtcDateTime);
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
            while (!cancellationToken.IsCancellationRequested)
            {
                await RunDueJobsAsync(_jobs, cancellationToken);

                var nextJob = _jobs.OrderBy(x => x.NextRun).FirstOrDefault();
                if (nextJob != null)
                {
                    var delay = (nextJob.NextRun - DateTimeOffset.Now) + TimeSpan.FromMilliseconds(10);
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
                var dueJobs = jobs.Where(x => x.NextRun < DateTimeOffset.Now).OrderBy(x => x.NextRun);
                foreach (var job in dueJobs)
                {
                    job.NextRun = GetNextRun(job.Details.Cron);

                    // Two runs of a job at once would share its state, and could post the same thing twice
                    if (job.Running is { IsCompleted: false })
                    {
                        _logger.LogWarning("{jobDeclareType}.{jobName} is still running from a run that was cut off, so this run is skipped",
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
                                detail = "the run that was cut off still runs"
                            }, job.Details.Cron, job.NextRun.UtcDateTime);
                        }

                        continue;
                    }

                    _logger.LogInformation("Running Job {jobDeclareType}.{jobName}, Current time: {currentTime}, next run at {nextRun}",
                        job.DeclaringType.Name, job.Name, $"{DateTimeOffset.UtcNow:HH:mm:ss}", $"{job.NextRun.UtcDateTime:HH:mm:ss}");
                    await RunJobAsync(job, cancellationToken);
                }
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(e, "Error running job");
            }
        }
    }
}
