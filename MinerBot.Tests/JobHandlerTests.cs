using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Cronos;
using DataAccessLibrary.Status;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MinerBot_2._0.Attributes;
using MinerBot_2._0.Handlers;
using MinerBot_2._0.Models;
using MinerBot_2._0.Status;

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

        // Its text is its run's detail
        public Task<string> CountsRows()
        {
            record.Started(nameof(CountsRows));
            return Task.FromResult("deleted 3 rows");
        }

        // A failure in the bot's own words, as the clean-up's
        public Task FailsInItsOwnWords()
        {
            record.Started(nameof(FailsInItsOwnWords));
            return Task.FromException(new JobFailedException("deleted 1 event; failed: sessions (TimeoutException)"));
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

        // With the bot's recorder, as the bot runs its jobs
        readonly FakeClock clock = new();
        readonly ServiceProvider recordedServices;
        readonly JobHandler recorded;
        readonly BotRecorder recorder;

        public JobHandlerTests()
        {
            services = new ServiceCollection().AddSingleton(record).BuildServiceProvider();
            handler = new JobHandler(logger, services);

            recordedServices = new ServiceCollection()
                .AddSingleton(record)
                .AddSingleton<TimeProvider>(clock)
                .AddSingleton<IConfiguration>(BotRecorderTests.Settings())
                .AddSingleton<BotRecorder>()
                .BuildServiceProvider();
            recorded = new JobHandler(logger, recordedServices);
            recorder = recordedServices.GetRequiredService<BotRecorder>();
        }

        public void Dispose()
        {
            record.Gate.Set();
            services.Dispose();
            recordedServices.Dispose();
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
        Task RunPassAsync(List<Job> jobs, JobHandler? with = null)
        {
            var stop = TestContext.Current.CancellationToken;

            return Task.Run(() => (with ?? handler).RunDueJobsAsync(jobs, stop), stop).WaitAsync(WaitLimit, stop);
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

        // Each run, with its end, its result and a detail made of its exception's type, never its message, which can hold
        // anything. A job's text is its detail. The job's status after it has its next run.
        [Fact]
        public async Task EachRunIsRecordedWithItsResult()
        {
            var jobs = Due(nameof(TestJobs.Works), nameof(TestJobs.Throws), nameof(TestJobs.ThrowsTimeout), nameof(TestJobs.CountsRows), nameof(TestJobs.FailsInItsOwnWords));

            await RunPassAsync(jobs, recorded);

            var runs = recorder.PeekJobRuns(10);

            Assert.Equal(
            [
                ("TestJobs.Works", JobResult.Ok, null),
                ("TestJobs.Throws", JobResult.Failed, "InvalidOperationException"),
                ("TestJobs.ThrowsTimeout", JobResult.Failed, "TimeoutException"),
                ("TestJobs.CountsRows", JobResult.Ok, "deleted 3 rows"),
                ("TestJobs.FailsInItsOwnWords", JobResult.Failed, (string?)"deleted 1 event; failed: sessions (TimeoutException)")
            ], runs.Select(run => (run.job, run.result, run.detail)));
            Assert.Equal([1u, 2u, 3u, 4u, 5u], runs.Select(run => run.seq));
            Assert.All(runs, run =>
            {
                Assert.Equal((clock.UtcNow.UtcDateTime, (DateTime?)clock.UtcNow.UtcDateTime), (run.started_at, run.ended_at));
                Assert.NotNull(run.duration_ms);
            });

            Assert.Equal(
            [
                "TestJobs.Throws failed: InvalidOperationException",
                "TestJobs.ThrowsTimeout failed: TimeoutException",
                "TestJobs.FailsInItsOwnWords failed: deleted 1 event; failed: sessions (TimeoutException)"
            ], recorder.PeekEvents(10).Where(row => row.kind == BotEvents.JobFailed).Select(row => row.detail));
            Assert.DoesNotContain(recorder.PeekEvents(10).Select(row => row.detail).Concat(runs.Select(run => run.detail)),
                detail => detail != null && (detail.Contains("The job failed") || detail.Contains("didn't answer")));

            var status = recorder.PeekJobStatus(10).Select(job => job.Row).ToList();
            Assert.Equal(jobs.Select(job => $"TestJobs.{job.Name}"), status.Select(job => job.job));
            Assert.All(status, job => Assert.Equal("* * * * *", job.schedule));
            Assert.Equal(jobs.Select(job => (DateTime?)job.NextRun.UtcDateTime), status.Select(job => job.next_run_at));
            Assert.Equal(runs.Select(run => run.result), status.Select(job => job.last_result));
        }

        // A run that was cut off is recorded then, and gets its end when it ends. A run due while it still runs is skipped.
        [Fact]
        public async Task CutOffRunIsRecordedThenGetsItsEnd()
        {
            recorded.JobTimeLimit = ShortLimit;
            var jobs = Due(nameof(TestJobs.WaitsForRelease));

            await RunPassAsync(jobs, recorded);

            var cutOff = Assert.Single(recorder.PeekJobRuns(10));
            Assert.Equal((JobResult.CutOff, (DateTime?)null, (uint?)null), (cutOff.result, cutOff.ended_at, cutOff.duration_ms));
            Assert.StartsWith("still running when the time limit of ", cutOff.detail);
            Assert.Equal("TestJobs.WaitsForRelease was cut off after 0 s", Assert.Single(recorder.PeekEvents(10), row => row.kind == BotEvents.JobCutOff).detail);

            MakeDue(jobs[0]);
            await RunPassAsync(jobs, recorded);

            record.Release.SetResult();
            await WaitUntilAsync(() => recorder.PeekJobRuns(10).Count == 3, "The end of the run that was cut off was never recorded");

            var runs = recorder.PeekJobRuns(10);
            Assert.Equal([(1u, JobResult.CutOff), (2u, JobResult.Skipped), (1u, JobResult.CutOff)], runs.Select(run => (run.seq, run.result)));
            Assert.Equal("the run that was cut off still runs", runs[1].detail);
            Assert.NotNull(runs[2].ended_at);
            Assert.NotNull(runs[2].duration_ms);
            Assert.Matches(@"^ended after \d+ s$", runs[2].detail);

            // The job's status shows the newest run, the one that was skipped
            Assert.Equal(JobResult.Skipped, Assert.Single(recorder.PeekJobStatus(10)).Row.last_result);
        }

        [Fact]
        public async Task CutOffRunThatFailsLaterIsRecordedByItsType()
        {
            recorded.JobTimeLimit = ShortLimit;
            var jobs = Due(nameof(TestJobs.WaitsForRelease));

            await RunPassAsync(jobs, recorded);
            record.Release.SetException(new InvalidOperationException("The job failed late"));

            await WaitUntilAsync(() => recorder.PeekJobRuns(10).Count == 2, "The end of the run that was cut off was never recorded");

            Assert.Matches(@"^failed after \d+ s: InvalidOperationException$", recorder.PeekJobRuns(10)[1].detail);
        }

        // Every job of the bot, with its schedule and next run, before any of them runs
        [Fact]
        public async Task EveryJobsScheduleAndNextRunAreRecordedAtTheStart()
        {
            await recorded.StartAsync(new CancellationToken(canceled: true));

            var bots = typeof(JobHandler).Assembly.GetTypes()
                .SelectMany(type => type.GetMethods())
                .Where(method => method.GetCustomAttribute<JobAttribute>() != null)
                .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
                .Order()
                .ToList();
            var status = recorder.PeekJobStatus(100).Select(job => job.Row).ToList();

            Assert.Contains("HousekeepingJob.ApplyRetention", bots);
            Assert.Equal(bots, recorder.JobNames!.Order());
            Assert.Equal(bots, status.Select(job => job.job).Order());
            Assert.Equal("30 4 * * *", status.Single(job => job.job == "HousekeepingJob.ApplyRetention").schedule);
            Assert.All(status, job =>
            {
                Assert.True(job.next_run_at > DateTime.UtcNow.AddSeconds(-5), $"{job.job} has no next run");
                Assert.Equal(DateTimeKind.Utc, job.next_run_at!.Value.Kind);
                Assert.Null(job.last_started_at);
            });
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
