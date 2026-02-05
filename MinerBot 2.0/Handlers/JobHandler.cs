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

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting JobService");

            _jobs = AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => x.GetTypes())
                      .SelectMany(t => t.GetMethods())
                      .Where(m => m.GetCustomAttributes(typeof(JobAttribute), false).Length > 0)
                      .Select(x =>
                        new Job
                        {
                            Name = x.Name,
                            MethodInfo = x,
                            Details = x.GetCustomAttribute<JobAttribute>(),
                            Parameters = x.GetParameters(),
                            DeclaringType = x.DeclaringType,
                            NextRun = GetNextRun(x.GetCustomAttribute<JobAttribute>().Cron)
                        })
                      .ToList();

            _ = Main(cancellationToken);
            return Task.CompletedTask;
        }

        private async Task RunJobAsync(Job job)
        {
            var jobClass = ActivatorUtilities.CreateInstance(_service, job.DeclaringType);
            await (Task)job.DeclaringType.GetMethod(job.Name).Invoke(jobClass, null);
        }

        private async Task Main(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var jobs = _jobs.Where(x => x.NextRun < DateTimeOffset.Now).OrderBy(x => x.NextRun);
                    foreach (var job in jobs)
                    {
                        job.NextRun = GetNextRun(job.Details.Cron);
                        _logger.LogInformation("Running Job {jobDeclareType}.{jobName}, Current time: {currentTime}, next run at {nextRun}",
                            job.DeclaringType.Name, job.Name, $"{DateTimeOffset.Now:h:mm:ss:ff}", $"{job.NextRun:h:mm:ss:ff}");
                        var timer = System.Diagnostics.Stopwatch.StartNew();
                        await RunJobAsync(job);
                        _logger.LogInformation("{jobDeclareType}.{jobName} took {timerHumanized}",
                            job.DeclaringType.Name, job.Name, timer.Elapsed.ToString());
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error running job");
                }
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
    }
}
