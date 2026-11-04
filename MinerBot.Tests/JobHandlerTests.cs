using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MinerBot_2._0.Attributes;
using MinerBot_2._0.Handlers;
using MinerBot_2._0.Models;

namespace MinerBot.Tests
{
    // What the test jobs did, and what the ones that wait are waiting for
    public sealed class JobRecord
    {
        readonly ConcurrentQueue<string> starts = new();

        public ManualResetEventSlim Gate { get; } = new();
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Started(string job) => starts.Enqueue(job);
        public int StartsOf(string job) => starts.Count(start => start == job);
    }

    // JobHandler makes a job's class and calls its method by name, the same as for the bot's own jobs
    public class TestJobs(JobRecord record)
    {
        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

        public Task Hangs()
        {
            record.Started(nameof(Hangs));
            return new TaskCompletionSource().Task;
        }

        // Holds its thread before it hands back a task, like a .Result on a call that never ends
        public Task Blocks()
        {
            record.Started(nameof(Blocks));
            record.Gate.Wait(WaitLimit);
            return Task.CompletedTask;
        }

        public Task Throws()
        {
            record.Started(nameof(Throws));
            throw new InvalidOperationException("The job failed");
        }

        // The way a database call that timed out fails
        public Task ThrowsTimeout()
        {
            record.Started(nameof(ThrowsTimeout));
            return Task.FromException(new TimeoutException("The database didn't answer"));
        }

        public Task WaitsForRelease()
        {
            record.Started(nameof(WaitsForRelease));
            return record.Release.Task;
        }

        public Task Works()
        {
            record.Started(nameof(Works));
            return Task.CompletedTask;
        }
    }

    // One pass of JobHandler at a time, over test jobs that are due. Only the tests where the limit has to fire make it short.
    public class JobHandlerTests : IDisposable
    {
        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);
        static readonly TimeSpan ShortLimit = TimeSpan.FromMilliseconds(100);

        readonly JobRecord record = new();
        readonly RecordingLogger<JobHandler> logger = new();
        readonly ServiceProvider services;
        readonly JobHandler handler;

        public JobHandlerTests()
        {
            services = new ServiceCollection().AddSingleton(record).BuildServiceProvider();
            handler = new JobHandler(logger, services);
        }

        public void Dispose()
        {
            record.Gate.Set();
            services.Dispose();
        }

        // Due some minutes ago, in the order given
        static List<Job> Due(params string[] names) => names.Select((name, index) => new Job
        {
            Name = name,
            MethodInfo = typeof(TestJobs).GetMethod(name)!,
            Details = new JobAttribute("* * * * *"),
            DeclaringType = typeof(TestJobs),
            NextRun = DateTimeOffset.Now.AddMinutes(index - 10)
        }).ToList();

        static void MakeDue(Job job) => job.NextRun = DateTimeOffset.Now.AddMinutes(-1);

        // Off the test's thread, so a pass that blocks fails the test instead of holding it
        Task RunPassAsync(List<Job> jobs)
        {
            var stop = TestContext.Current.CancellationToken;

            return Task.Run(() => handler.RunDueJobsAsync(jobs, stop), stop).WaitAsync(WaitLimit, stop);
        }

        // A run that was cut off may not have started yet when its pass ends
        static Task RunOf(Job job) => job.Running!.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

        static async Task WaitUntilAsync(Func<bool> condition, string failure)
        {
            var clock = Stopwatch.StartNew();

            while (!condition())
            {
                Assert.True(clock.Elapsed < WaitLimit, failure);
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        [Fact]
        public async Task StuckJobIsCutOffAndTheNextJobStillRuns()
        {
            handler.JobTimeLimit = ShortLimit;
            var jobs = Due(nameof(TestJobs.Hangs), nameof(TestJobs.Works));

            await RunPassAsync(jobs);
            await RunOf(jobs[1]);

            Assert.Equal(1, record.StartsOf(nameof(TestJobs.Works)));
            Assert.True(logger.Logged(LogLevel.Warning, "TestJobs.Hangs was cut off"));
        }

        [Fact]
        public async Task JobThatBlocksItsThreadIsCutOffToo()
        {
            handler.JobTimeLimit = ShortLimit;
            var jobs = Due(nameof(TestJobs.Blocks), nameof(TestJobs.Works));

            try
            {
                await RunPassAsync(jobs);
                await RunOf(jobs[1]);
            }
            finally
            {
                record.Gate.Set();
            }

            Assert.Equal(1, record.StartsOf(nameof(TestJobs.Works)));
            Assert.True(logger.Logged(LogLevel.Warning, "TestJobs.Blocks was cut off"));
        }

        [Fact]
        public async Task FailedJobIsLoggedAndTheRestOfThePassStillRuns()
        {
            await RunPassAsync(Due(nameof(TestJobs.Throws), nameof(TestJobs.Works)));

            Assert.Equal(1, record.StartsOf(nameof(TestJobs.Works)));
            Assert.True(logger.Logged(LogLevel.Error, "TestJobs.Throws failed"));
        }

        [Fact]
        public async Task JobsOwnTimeoutIsAFailureNotACutOff()
        {
            await RunPassAsync(Due(nameof(TestJobs.ThrowsTimeout)));

            Assert.True(logger.Logged(LogLevel.Error, "TestJobs.ThrowsTimeout failed"));
            Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("cut off"));
        }

        // Two runs of the notification job at once would share its state and could post the same items twice
        [Fact]
        public async Task JobThatIsStillRunningIsNotStartedAgain()
        {
            handler.JobTimeLimit = ShortLimit;
            var jobs = Due(nameof(TestJobs.Hangs));

            await RunPassAsync(jobs);
            await WaitUntilAsync(() => record.StartsOf(nameof(TestJobs.Hangs)) == 1, "The first run never started");

            MakeDue(jobs[0]);
            await RunPassAsync(jobs);

            Assert.Equal(1, record.StartsOf(nameof(TestJobs.Hangs)));
            Assert.True(logger.Logged(LogLevel.Warning, "TestJobs.Hangs is still running from a run that was cut off"));
        }

        [Fact]
        public async Task CutOffJobIsStartedAgainOnceItHasEnded()
        {
            handler.JobTimeLimit = ShortLimit;
            var jobs = Due(nameof(TestJobs.WaitsForRelease));

            await RunPassAsync(jobs);
            record.Release.SetResult();
            await RunOf(jobs[0]);

            MakeDue(jobs[0]);
            await RunPassAsync(jobs);
            await RunOf(jobs[0]);

            Assert.Equal(2, record.StartsOf(nameof(TestJobs.WaitsForRelease)));
        }

        // Nothing else would show that it ended, or how
        [Fact]
        public async Task CutOffJobThatFailsLaterIsLogged()
        {
            handler.JobTimeLimit = ShortLimit;
            var jobs = Due(nameof(TestJobs.WaitsForRelease));

            await RunPassAsync(jobs);
            record.Release.SetException(new InvalidOperationException("The job failed late"));

            await WaitUntilAsync(() => logger.Logged(LogLevel.Error, "TestJobs.WaitsForRelease, which was cut off, failed"), "The end of the run was never logged");
        }

        // Checks every job of the bot, so one that runs more often than the limit allows can't be added unnoticed
        [Fact]
        public void TimeLimitIsUnderTheShortestSchedule()
        {
            var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var shortestGap = typeof(JobHandler).Assembly.GetTypes()
                .SelectMany(type => type.GetMethods())
                .Select(method => method.GetCustomAttribute<JobAttribute>())
                .OfType<JobAttribute>()
                .Select(job => CronExpression.Parse(job.Cron).GetOccurrences(from, from.AddDays(8)).ToList())
                .SelectMany(runs => runs.Zip(runs.Skip(1), (run, next) => next - run))
                .Min();

            Assert.True(handler.JobTimeLimit < shortestGap, $"A limit of {handler.JobTimeLimit} isn't under the shortest gap between runs, {shortestGap}");
        }
    }
}
