using System.Reflection;
using DataAccessLibrary;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Status;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MinerBot_2._0.Attributes;
using MinerBot_2._0.Handlers;
using MinerBot_2._0.Jobs;
using MinerBot_2._0.Models;
using MinerBot_2._0.Status;
using MySql.Data.MySqlClient;

namespace MinerBot.Tests
{
    // The post records clean-up's cutoffs, chunks and counts, against a stand-in for MinerBot's database: the daily totals
    // first, from the first day the record holds whole, then the record, the settled queue, the seen lobbies and the lobby
    // samples
    public class PostingRetentionJobTests
    {
        // 04:33 in US Eastern time, when the job runs
        static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 33, 0, TimeSpan.Zero);

        static readonly DateTime Today = new(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);

        // The first day the record and the samples keep: 90 days before today, whole days only
        static readonly DateTime KeepFrom = new(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc);

        // Made up, and never connected to: FakePostingRetentionData stands in for the database
        const string Connection = "Server=127.0.0.1;Database=bot_db;Uid=minerbot-bot;Pwd=password";

        static readonly string[] Rules = ["TotalDeliveryDays", "DeleteDeliveries", "DeleteSettledQueueRows", "DeleteSeenLobbies", "DeleteLobbySamples"];

        readonly FakePostingRetentionData retention = new();
        readonly FakeClock clock = new() { UtcNow = Now };
        readonly RecordingLogger<PostingRetentionJob> jobLogger = new();

        PostingRetentionJob Job() => new(retention, clock, BotRecorderTests.Settings(botConnection: Connection), jobLogger);

        // MySql.Data's messages can name the login, as this one does
        static MySqlException Refused(int number) =>
            (MySqlException)typeof(MySqlException)
                .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string), typeof(int), typeof(Exception)])!
                .Invoke([$"Access denied for user 'minerbot-bot'@'localhost'", number, null]);

        static Job PostRecords() => new()
        {
            Name = nameof(PostingRetentionJob.ApplyRetention),
            MethodInfo = typeof(PostingRetentionJob).GetMethod(nameof(PostingRetentionJob.ApplyRetention))!,
            Details = new JobAttribute("33 4 * * *"),
            DeclaringType = typeof(PostingRetentionJob),
            NextRun = DateTimeOffset.Now.AddMinutes(-1)
        };

        // The days are totalled before any of their rows can go, from the first day the record holds whole, and the record and
        // the samples go in whole days, so no day's totals are ever worked out from part of it. The lobbies go a week after
        // they were last seen.
        [Fact]
        public async Task EachRuleGetsItsCutoffFromTheClock()
        {
            (retention.Deliveries, retention.QueueRows, retention.SeenLobbies, retention.LobbySamples, retention.Days) = (12, 4, 30, 288, 90);

            var detail = await Job().ApplyRetention();

            Assert.Equal("deleted 12 post records, 4 queued items, 30 seen lobbies and 288 lobby samples; totalled 90 days", detail);
            Assert.Equal(
            [
                ("TotalDeliveryDays", KeepFrom, 0),
                ("DeleteDeliveries", KeepFrom, 5000),
                ("DeleteSettledQueueRows", KeepFrom, 5000),
                ("DeleteSeenLobbies", Now.UtcDateTime.AddDays(-7), 5000),
                ("DeleteLobbySamples", KeepFrom, 5000)
            ], retention.Calls);
            Assert.Equal((KeepFrom, Today), retention.Totalled);
            Assert.All(retention.Calls, call => Assert.Equal(DateTimeKind.Utc, call.Cutoff.Kind));
        }

        [Fact]
        public async Task ChunksGoUntilOneComesBackShort()
        {
            (retention.Deliveries, retention.QueueRows, retention.LobbySamples) = (12000, 5000, 1);

            var detail = await Job().ApplyRetention();

            Assert.Equal([5000, 5000, 5000], retention.Calls.Where(call => call.Rule == "DeleteDeliveries").Select(call => call.Take));
            Assert.Equal(2, retention.Calls.Count(call => call.Rule == "DeleteSettledQueueRows"));
            Assert.Equal("deleted 12000 post records, 5000 queued items, 0 seen lobbies and 1 lobby sample; totalled 0 days", detail);
        }

        [Fact]
        public async Task EachRuleRunsAHundredChunksAtMost()
        {
            retention.LobbySamples = 600000;

            var detail = await Job().ApplyRetention();

            Assert.Equal(PostingRetentionJob.MaxChunks, retention.Calls.Count(call => call.Rule == "DeleteLobbySamples"));
            Assert.Contains("500000 lobby samples", detail);
            Assert.EndsWith("; more is left for the next run", detail);
        }

        // Each rule still runs its first chunk, so a slow night costs only what's left, which the next night takes
        [Fact]
        public async Task TimeLimitStopsTheChunksButNotTheRules()
        {
            (retention.Deliveries, retention.LobbySamples) = (20000, 20000);
            var job = Job();
            job.TimeBudget = TimeSpan.Zero;

            var detail = await job.ApplyRetention();

            Assert.Equal(Rules, retention.Calls.Select(call => call.Rule));
            Assert.Equal("deleted 5000 post records, 0 queued items, 0 seen lobbies and 5000 lobby samples; totalled 0 days; more is left for the next run", detail);
        }

        // After the budget, the chunk under way ends and each later rule still runs its first chunk: at most five calls after
        // it, when it runs out in the totals, each within one call's limit. So the job ends before JobHandler cuts it off, and
        // early enough that the notification run at 04:35, which waits for it in the same pass, waits little.
        [Fact]
        public void TimeBudgetLeavesRoomForTheRulesAfterIt()
        {
            var callLimit = new PostingRetentionData(new FakeDatabase(), new DatabaseNames(new Dictionary<DBConnectionName, string>())).CallLimit;
            var jobLimit = new JobHandler(NullLogger<JobHandler>.Instance, new ServiceCollection().BuildServiceProvider()).JobTimeLimit;
            var longest = Job().TimeBudget + 5 * callLimit;

            Assert.True(longest < jobLimit, $"The post records clean-up may run {longest}, which isn't under JobHandler's limit of {jobLimit}");
            Assert.True(longest <= TimeSpan.FromMinutes(2.5), $"The post records clean-up may run {longest}, which holds up the 04:35 notification run");
        }

        // The other rules still run, and the failure names each rule that failed by its exception's type and MariaDB's
        // number, never its message, as does the log line for each
        [Fact]
        public async Task EveryRuleRunsWhenOneFailsAndTheJobThenFails()
        {
            (retention.Deliveries, retention.LobbySamples) = (12, 3);
            retention.Failures["TotalDeliveryDays"] = Refused(1205);
            retention.Failures["DeleteSettledQueueRows"] = new TimeoutException("The database didn't answer within 15 seconds");

            var failed = await Assert.ThrowsAsync<JobFailedException>(() => Job().ApplyRetention());

            Assert.Equal("deleted 12 post records, 0 queued items, 0 seen lobbies and 3 lobby samples; totalled 0 days; "
                + "failed: daily totals (MySqlException, MySQL error 1205); queued items (TimeoutException)", failed.Message);
            Assert.Equal(Rules, retention.Calls.Select(call => call.Rule).Distinct());
            Assert.Equal(
            [
                "The post records clean-up's rule for daily totals failed (MySqlException, MySQL error 1205)",
                "The post records clean-up's rule for queued items failed (TimeoutException)"
            ], jobLogger.Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message));
            Assert.DoesNotContain(jobLogger.Entries, entry => entry.Message.Contains("minerbot-bot") || entry.Exception != null);
        }

        // A night when MinerBot's database is down, with more left than one night takes: every rule fails the same way, and the
        // details name every rule with MariaDB's number inside the 300 characters that job_runs, job_status and bot_events
        // keep, the event's with the job's name before it
        [Fact]
        public async Task NightWithTheDatabaseDownNamesEveryRuleAndItsError()
        {
            foreach (var rule in Rules)
                retention.Failures[rule] = Refused(1042);

            await using var services = new ServiceCollection()
                .AddSingleton<IPostingRetentionData>(retention)
                .AddSingleton<TimeProvider>(clock)
                .AddSingleton<IConfiguration>(BotRecorderTests.Settings(botConnection: Connection))
                .AddSingleton<ILogger<PostingRetentionJob>>(jobLogger)
                .AddSingleton<BotRecorder>()
                .BuildServiceProvider();

            var handler = new JobHandler(new RecordingLogger<JobHandler>(), services);

            await handler.RunDueJobsAsync([PostRecords()], TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            var recorder = services.GetRequiredService<BotRecorder>();
            var run = Assert.Single(recorder.PeekJobRuns(10));
            var failedEvent = Assert.Single(recorder.PeekEvents(10), row => row.kind == BotEvents.JobFailed);
            const string failures = "failed: every rule (MySqlException, MySQL error 1042)";

            Assert.Equal((JobResult.Failed, (string?)$"deleted 0 post records, 0 queued items, 0 seen lobbies and 0 lobby samples; totalled 0 days; {failures}"), (run.result, run.detail));
            Assert.EndsWith(failures, failedEvent.detail);
            Assert.True(failedEvent.detail!.Length <= 300, $"The job.failed event's detail is {failedEvent.detail.Length} characters, and its column keeps 300");
        }

        // The longest failure where the rules that failed failed alike: four of the five, after a hundred chunks of each rule,
        // with more left for the next run. MariaDB's number stays inside the event's 300 characters, the job's name before it.
        [Fact]
        public void FailureDetailKeepsMariaDBsNumberInTheEvent()
        {
            var most = PostingRetentionJob.MaxChunks * PostingRetentionJob.ChunkSize;
            var problem = "MySqlException, MySQL error 1042";
            var four = PostingRetentionTexts.Failed([("post records", problem), ("queued items", problem), ("seen lobbies", problem), ("lobby samples", problem)], 5);
            var detail = $"PostingRetentionJob.ApplyRetention failed: {PostingRetentionTexts.Done(most, most, most, most, 90)}{four}{PostingRetentionTexts.MoreLeft}";

            Assert.Contains("post records, queued items, seen lobbies and lobby samples (MySqlException, MySQL error 1042)", detail);
            Assert.True(detail.Length <= 300, $"The longest job.failed detail is {detail.Length} characters, and its column keeps 300: {detail}");
            Assert.Equal("; failed: every rule (TimeoutException)",
                PostingRetentionTexts.Failed([.. new[] { "daily totals", "post records", "queued items", "seen lobbies", "lobby samples" }.Select(rule => (rule, "TimeoutException"))], 5));
        }

        // README lets the bot run without BotConnection: it records nothing, and this clean-up does nothing, rather than fail
        // with an error every night
        [Theory]
        [InlineData(null, "isn't set")]
        [InlineData("Server=127.0.0.1;Uid=minerbot-bot;Pwd=password", "names no database")]
        public async Task WithoutBotConnectionItDoesNothing(string? connection, string problem)
        {
            var settings = BotRecorderTests.Settings(botConnection: connection);
            var logger = new RecordingLogger<JobHandler>();

            await using var services = new ServiceCollection()
                .AddSingleton<ILogger<PostingRetentionJob>>(jobLogger)
                .AddDataAccessServices(settings)
                .AddPostingRetentionAccess()
                .AddSingleton<IConfiguration>(settings)
                .AddSingleton<TimeProvider>(clock)
                .AddSingleton<BotRecorder>()
                .BuildServiceProvider();

            var handler = new JobHandler(logger, services);

            await handler.RunDueJobsAsync([PostRecords()], TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            var run = Assert.Single(services.GetRequiredService<BotRecorder>().PeekJobRuns(10));

            Assert.Equal((JobResult.Ok, (string?)$"skipped, because ConnectionStrings:BotConnection {problem}"), (run.result, run.detail));
            Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
            Assert.Empty(jobLogger.Entries);
        }

        // 04:33 Eastern: three minutes after the clean-up, which shares its pass with the 04:30 notification run, and off the
        // notification run's 5-minute marks
        [Fact]
        public void RunsDailyAfterTheCleanUpAndOffTheNotificationRunsMarks()
        {
            var job = typeof(PostingRetentionJob).GetMethod(nameof(PostingRetentionJob.ApplyRetention))!.GetCustomAttribute<JobAttribute>();

            Assert.Equal("33 4 * * *", job!.Cron);
        }

        // As the bot runs it: JobHandler makes it with its services, and its counts become the run's detail, or its failure
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task JobHandlerRecordsItsCountsOrItsFailure(bool fails)
        {
            retention.Deliveries = 2;

            if (fails)
                retention.Failures["DeleteSeenLobbies"] = new TimeoutException("The database didn't answer within 15 seconds");

            await using var services = new ServiceCollection()
                .AddSingleton<IPostingRetentionData>(retention)
                .AddSingleton<TimeProvider>(clock)
                .AddSingleton<IConfiguration>(BotRecorderTests.Settings(botConnection: Connection))
                .AddSingleton<ILogger<PostingRetentionJob>>(jobLogger)
                .AddSingleton<BotRecorder>()
                .BuildServiceProvider();

            var handler = new JobHandler(new RecordingLogger<JobHandler>(), services);

            await handler.RunDueJobsAsync([PostRecords()], TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            var recorder = services.GetRequiredService<BotRecorder>();
            var run = Assert.Single(recorder.PeekJobRuns(10));
            const string counts = "deleted 2 post records, 0 queued items, 0 seen lobbies and 0 lobby samples; totalled 0 days";

            Assert.Equal("PostingRetentionJob.ApplyRetention", run.job);
            Assert.Equal(fails ? JobResult.Failed : JobResult.Ok, run.result);
            Assert.Equal(fails ? $"{counts}; failed: seen lobbies (TimeoutException)" : counts, run.detail);
            Assert.Equal(fails, recorder.PeekEvents(10).Any(row => row.kind == BotEvents.JobFailed));
        }
    }
}
