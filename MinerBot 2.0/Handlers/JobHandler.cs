using MinerBot_2._0.Attributes;
using Cronos;
using System.Reflection;
using MinerBot_2._0.Models;

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

            // On the thread pool, so no job runs on the thread that started the handler
            _ = Task.Run(() => Main(cancellationToken));
            return Task.CompletedTask;
        }

        private async Task RunJobAsync(Job job, CancellationToken cancellationToken)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();

            // On the thread pool too, because a job that blocks before it hands back its task would hold up the limit
            var run = Task.Run(() => InvokeJobAsync(job));
            job.Running = run;

            try
            {
                await run.WaitAsync(JobTimeLimit, cancellationToken);
                _logger.LogInformation("{jobDeclareType}.{jobName} took {timerHumanized}",
                    job.DeclaringType.Name, job.Name, timer.Elapsed.ToString());
            }
            // A job that throws a TimeoutException of its own has failed, it wasn't cut off
            catch (TimeoutException) when (!run.IsCompleted)
            {
                _logger.LogWarning("{jobDeclareType}.{jobName} was cut off after {timeLimit}. It carries on in the background, and this job isn't started again until it ends.",
                    job.DeclaringType.Name, job.Name, JobTimeLimit.ToString());

                // Otherwise nothing would show when, and how, it ended
                _ = run.ContinueWith(ended =>
                {
                    if (ended.IsFaulted)
                        _logger.LogError(ended.Exception, "{jobDeclareType}.{jobName}, which was cut off, failed after {timerHumanized}",
                            job.DeclaringType.Name, job.Name, timer.Elapsed.ToString());
                    else
                        _logger.LogInformation("{jobDeclareType}.{jobName}, which was cut off, ended after {timerHumanized}",
                            job.DeclaringType.Name, job.Name, timer.Elapsed.ToString());
                }, TaskScheduler.Default);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(e, "{jobDeclareType}.{jobName} failed", job.DeclaringType.Name, job.Name);
            }
        }

        private async Task InvokeJobAsync(Job job)
        {
            var jobClass = ActivatorUtilities.CreateInstance(_service, job.DeclaringType);
            await (Task)job.DeclaringType.GetMethod(job.Name).Invoke(jobClass, null);
        }

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
