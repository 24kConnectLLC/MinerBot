using System.Reflection;
using DataAccessLibrary;
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
    // The daily clean-up's cutoffs, chunks and counts, against a stand-in for MinerBot's database
    public class HousekeepingJobTests
    {
        // 04:30 in US Eastern time, when the job runs
        static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 30, 0, TimeSpan.Zero);

        // Made up, and never connected to: FakeRetentionData stands in for the database
        const string Connection = "Server=127.0.0.1;Database=bot_db;Uid=minerbot-bot;Pwd=password";

        readonly FakeRetentionData retention = new();
        readonly FakeClock clock = new() { UtcNow = Now };
        readonly RecordingLogger<HousekeepingJob> jobLogger = new();

        HousekeepingJob Job() => new(retention, clock, BotRecorderTests.Settings(botConnection: Connection), jobLogger);

        [Fact]
        public async Task EachRuleGetsItsCutoffFromTheClock()
        {
            retention.Addresses = 3;
            retention.Events = 12;
            retention.GuildEvents = 4;
            retention.Days = 2;
            retention.Sessions = 1;

            var detail = await Job().ApplyRetention();

            Assert.Equal("deleted 0 audit rows, 12 events, 4 server events, 0 job runs, 0 minutes (2 days totalled) and 1 session, and cleared 3 addresses", detail);

            var now = Now.UtcDateTime;
            var keepFrom = new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc);

            Assert.Equal(
            [
                ("DeleteAuditRows", new DateTime(2024, 11, 14, 9, 30, 0, DateTimeKind.Utc), 5000),
                ("ClearAuditAddresses", now.AddDays(-90), 5000),
                ("DeleteEvents", now.AddDays(-90), 5000),
                ("DeleteGuildEvents", now.AddDays(-90), 5000),
                ("DeleteJobRuns", now.AddDays(-90), 5000),
                ("TotalUptimeDays", keepFrom, 0),
                ("DeleteSamples", keepFrom, 5000),
                ("DeleteSessions", now.AddDays(-7), 5000)
            ], retention.Calls);
            Assert.Equal((keepFrom, new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc)), retention.Totalled);
            Assert.All(retention.Calls, call => Assert.Equal(DateTimeKind.Utc, call.Cutoff.Kind));
        }

        [Fact]
        public async Task ChunksGoUntilOneComesBackShort()
        {
            retention.Events = 12000;
            retention.AuditRows = 5000;
            retention.Sessions = 1;

            var detail = await Job().ApplyRetention();

            Assert.Equal([5000, 5000, 5000], retention.Calls.Where(call => call.Rule == "DeleteEvents").Select(call => call.Take));
            Assert.Equal(2, retention.Calls.Count(call => call.Rule == "DeleteAuditRows"));
            Assert.Equal("deleted 5000 audit rows, 12000 events, 0 server events, 0 job runs, 0 minutes (0 days totalled) and 1 session, and cleared 0 addresses", detail);
        }

        [Fact]
        public async Task EachRuleRunsAHundredChunksAtMost()
        {
            retention.Samples = 600000;

            var detail = await Job().ApplyRetention();

            Assert.Equal(HousekeepingJob.MaxChunks, retention.Calls.Count(call => call.Rule == "DeleteSamples"));
            Assert.Contains("500000 minutes", detail);
            Assert.EndsWith("; more is left for the next run", detail);
        }

        // Each rule still runs its first chunk, so a slow night costs only what's left, which the next night takes
        [Fact]
        public async Task TimeLimitStopsTheChunksButNotTheRules()
        {
            retention.AuditRows = 20000;
            retention.Sessions = 20000;
            var job = Job();
            job.TimeBudget = TimeSpan.Zero;

            var detail = await job.ApplyRetention();

            Assert.Equal(["DeleteAuditRows", "ClearAuditAddresses", "DeleteEvents", "DeleteGuildEvents", "DeleteJobRuns", "TotalUptimeDays", "DeleteSamples", "DeleteSessions"],
                retention.Calls.Select(call => call.Rule));
            Assert.StartsWith("deleted 5000 audit rows,", detail);
            Assert.EndsWith(" and 5000 sessions, and cleared 0 addresses; more is left for the next run", detail);
        }

        // After the budget, the chunk under way ends and each later rule still runs its first chunk: at most nine
        // statements when the budget runs out in the first rule, the totals being two, each within one call's limit, and
        // the totals' count is a read, which gets one more try after a passing problem. So the job ends before JobHandler
        // cuts it off.
        [Fact]
        public void TimeBudgetLeavesRoomForTheRulesAfterIt()
        {
            var callLimit = new RetentionData(new FakeDatabase(), new DatabaseNames(new Dictionary<DBConnectionName, string>())).CallLimit;
            var jobLimit = new JobHandler(NullLogger<JobHandler>.Instance, new ServiceCollection().BuildServiceProvider()).JobTimeLimit;
            var longest = Job().TimeBudget + 10 * callLimit;

            Assert.True(longest < jobLimit, $"The clean-up may run {longest}, which isn't under JobHandler's limit of {jobLimit}");
        }

        // MySql.Data doesn't stop a statement the job gave up on, so every statement of the clean-up, the totals' count too,
        // carries MariaDB's own limits, and the server ends it after 14 seconds
        [Fact]
        public async Task EveryStatementCarriesTheServersLimits()
        {
            var database = new FakeDatabase().Rows().Rows().Rows().Rows().Rows().Rows().Rows().Rows().Rows();
            var data = new RetentionData(database, new DatabaseNames(new Dictionary<DBConnectionName, string> { [DBConnectionName.Bot] = Connection }));
            var today = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);

            await data.DeleteAuditRows(today, 1);
            await data.ClearAuditAddresses(today, 1);
            await data.DeleteEvents(today, 1);
            await data.DeleteGuildEvents(today, 1);
            await data.DeleteJobRuns(today, 1);
            // The fake's rows aren't a count, so the totals fail once both of their statements have been sent
            await Assert.ThrowsAnyAsync<Exception>(() => data.TotalUptimeDays(today.AddDays(-90), today));
            await data.DeleteSamples(today, 1);
            await data.DeleteSessions(today, 1);

            Assert.Equal(9, database.Commands.Count);
            Assert.All(database.Commands, command =>
                Assert.StartsWith("SET STATEMENT max_statement_time = 14, lock_wait_timeout = 14, innodb_lock_wait_timeout = 14 FOR ", command));
        }

        // The other rules still run, and the failure names each rule that failed by its exception's type and MariaDB's
        // number, never its message, as does the log line for each
        [Fact]
        public async Task EveryRuleRunsWhenOneFailsAndTheJobThenFails()
        {
            retention.Events = 12000;
            retention.Sessions = 1;
            retention.Failures["DeleteJobRuns"] = new TimeoutException("The database didn't answer within 15 seconds");
            retention.Failures["TotalUptimeDays"] = Refused(1205);

            var failed = await Assert.ThrowsAsync<JobFailedException>(() => Job().ApplyRetention());

            Assert.Equal("deleted 0 audit rows, 12000 events, 0 server events, 0 job runs, 0 minutes (0 days totalled) and 1 session, and cleared 0 addresses; "
                + "failed: job runs (TimeoutException); uptime totals (MySqlException, MySQL error 1205)", failed.Message);
            Assert.Equal(8, retention.Calls.Select(call => call.Rule).Distinct().Count());
            Assert.Equal(
            [
                "The daily clean-up's rule for job runs failed (TimeoutException)",
                "The daily clean-up's rule for uptime totals failed (MySqlException, MySQL error 1205)"
            ], jobLogger.Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message));
            Assert.DoesNotContain(jobLogger.Entries, entry => entry.Message.Contains("minerbot-bot") || entry.Exception != null);
        }

        // A night when MinerBot's database is down: every rule fails the same way, and the details say so with MariaDB's
        // number, inside the 300 characters that job_runs, job_status and bot_events keep, the event's with the job's name
        // before it. The log names each rule.
        [Fact]
        public async Task NightWithTheDatabaseDownNamesEveryRuleAndItsError()
        {
            foreach (var rule in new[] { "DeleteAuditRows", "ClearAuditAddresses", "DeleteEvents", "DeleteGuildEvents", "DeleteJobRuns", "TotalUptimeDays", "DeleteSamples", "DeleteSessions" })
                retention.Failures[rule] = Refused(1042);

            await using var services = new ServiceCollection()
                .AddSingleton<IRetentionData>(retention)
                .AddSingleton<TimeProvider>(clock)
                .AddSingleton<IConfiguration>(BotRecorderTests.Settings(botConnection: Connection))
                .AddSingleton<ILogger<HousekeepingJob>>(jobLogger)
                .AddSingleton<BotRecorder>()
                .BuildServiceProvider();

            var handler = new JobHandler(new RecordingLogger<JobHandler>(), services);

            await handler.RunDueJobsAsync([Housekeeping()], TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            var recorder = services.GetRequiredService<BotRecorder>();
            var run = Assert.Single(recorder.PeekJobRuns(10));
            var failedEvent = Assert.Single(recorder.PeekEvents(10), row => row.kind == BotEvents.JobFailed);
            const string failures = "failed: every rule (MySqlException, MySQL error 1042)";

            Assert.Equal($"deleted 0 audit rows, 0 events, 0 server events, 0 job runs, 0 minutes (0 days totalled) and 0 sessions, and cleared 0 addresses; {failures}", run.detail);
            Assert.EndsWith(failures, failedEvent.detail);
            Assert.True(failedEvent.detail!.Length <= 300, $"The job.failed event's detail is {failedEvent.detail.Length} characters, and its column keeps 300");
            Assert.Equal(
            [
                "The daily clean-up's rule for audit rows failed (MySqlException, MySQL error 1042)",
                "The daily clean-up's rule for audit addresses failed (MySqlException, MySQL error 1042)",
                "The daily clean-up's rule for events failed (MySqlException, MySQL error 1042)",
                "The daily clean-up's rule for server events failed (MySqlException, MySQL error 1042)",
                "The daily clean-up's rule for job runs failed (MySqlException, MySQL error 1042)",
                "The daily clean-up's rule for uptime totals failed (MySqlException, MySQL error 1042)",
                "The daily clean-up's rule for uptime minutes failed (MySqlException, MySQL error 1042)",
                "The daily clean-up's rule for sessions failed (MySqlException, MySQL error 1042)"
            ], jobLogger.Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message));
            Assert.DoesNotContain(jobLogger.Entries, entry => entry.Message.Contains("minerbot-bot"));
        }

        // When every rule but one failed the same way, as when the database went down or came back during the run, they're
        // named by the one that didn't, so the job.failed event still keeps MariaDB's number inside its 300 characters, the
        // job's name before it. The rule that went through counts its rows.
        [Theory]
        [InlineData("DeleteAuditRows", "audit rows")]
        [InlineData("ClearAuditAddresses", "audit addresses")]
        [InlineData("DeleteEvents", "events")]
        [InlineData("DeleteGuildEvents", "server events")]
        [InlineData("DeleteJobRuns", "job runs")]
        [InlineData("TotalUptimeDays", "uptime totals")]
        [InlineData("DeleteSamples", "uptime minutes")]
        [InlineData("DeleteSessions", "sessions")]
        public async Task RulesThatMostlyFailedAlikeAreNamedByTheOneThatDidnt(string spared, string rule)
        {
            (retention.AuditRows, retention.Addresses, retention.Events, retention.GuildEvents) = (4999, 4999, 4999, 4999);
            (retention.JobRuns, retention.Days, retention.Samples, retention.Sessions) = (4999, 90, 4999, 4999);

            foreach (var other in Rules.Where(other => other != spared))
                retention.Failures[other] = Refused(1042);

            var (run, failedEvent) = await RunThroughJobHandler();
            var failures = $"; failed: every rule but {rule} (MySqlException, MySQL error 1042)";

            Assert.True(failedEvent.Length <= 300, $"The job.failed event's detail is {failedEvent.Length} characters, and its column keeps 300: {failedEvent}");
            Assert.EndsWith(failures, run);
            Assert.EndsWith(failures, failedEvent);
        }

        // A rule that failed another way, as one that was cut off as the database went down, is named beside them
        [Fact]
        public async Task RuleThatFailedAnotherWayIsNamedBesideTheRest()
        {
            retention.Failures["DeleteAuditRows"] = new TimeoutException("The database didn't answer within 15 seconds");

            foreach (var rule in Rules.Skip(1))
                retention.Failures[rule] = Refused(1042);

            var (run, failedEvent) = await RunThroughJobHandler();
            const string failures = "; failed: audit rows (TimeoutException); every rule but audit rows (MySqlException, MySQL error 1042)";

            Assert.True(failedEvent.Length <= 300, $"The job.failed event's detail is {failedEvent.Length} characters, and its column keeps 300: {failedEvent}");
            Assert.EndsWith(failures, run);
            Assert.EndsWith(failures, failedEvent);
        }

        // The database down for the first rules and back for the rest, on a night with more left for the next run: that comes
        // after the failures, so the job.failed event keeps MariaDB's number inside its 300 characters, the job's name before
        // it, and the job run's detail keeps all of it
        [Fact]
        public async Task NightThatLeftMoreForTheNextRunKeepsMariaDBsNumberInTheEvent()
        {
            (retention.JobRuns, retention.Days, retention.Samples, retention.Sessions) = (600, 4, 600000, 12);

            foreach (var rule in Rules.Take(4))
                retention.Failures[rule] = Refused(1042);

            var (run, failedEvent) = await RunThroughJobHandler();
            const string failures = "; failed: audit rows, audit addresses, events and server events (MySqlException, MySQL error 1042)";

            Assert.EndsWith($"{failures}; more is left for the next run", run);
            Assert.True(run.Length <= 300, $"The job run's detail is {run.Length} characters, and its column keeps 300: {run}");
            Assert.Contains(failures, DbBaseConnection.Clip(failedEvent, 300));
        }

        // Half of the rules failing alike are named themselves, which is shorter than naming the others
        [Fact]
        public async Task HalfOfTheRulesFailingAlikeAreNamed()
        {
            foreach (var rule in new[] { "DeleteEvents", "DeleteGuildEvents", "DeleteJobRuns", "TotalUptimeDays" })
                retention.Failures[rule] = Refused(1042);

            var failed = await Assert.ThrowsAsync<JobFailedException>(() => Job().ApplyRetention());

            Assert.EndsWith("; failed: events, server events, job runs and uptime totals (MySqlException, MySQL error 1042)", failed.Message);
        }

        static readonly string[] Rules = ["DeleteAuditRows", "ClearAuditAddresses", "DeleteEvents", "DeleteGuildEvents", "DeleteJobRuns", "TotalUptimeDays", "DeleteSamples", "DeleteSessions"];

        // The job as the bot runs it, failing: its run's detail and its job.failed event's
        async Task<(string Run, string Event)> RunThroughJobHandler()
        {
            await using var services = new ServiceCollection()
                .AddSingleton<IRetentionData>(retention)
                .AddSingleton<TimeProvider>(clock)
                .AddSingleton<IConfiguration>(BotRecorderTests.Settings(botConnection: Connection))
                .AddSingleton<ILogger<HousekeepingJob>>(jobLogger)
                .AddSingleton<BotRecorder>()
                .BuildServiceProvider();

            var handler = new JobHandler(new RecordingLogger<JobHandler>(), services);

            await handler.RunDueJobsAsync([Housekeeping()], TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            var recorder = services.GetRequiredService<BotRecorder>();
            var run = Assert.Single(recorder.PeekJobRuns(10));
            var failedEvent = Assert.Single(recorder.PeekEvents(10), row => row.kind == BotEvents.JobFailed);

            Assert.Equal(JobResult.Failed, run.result);

            return (run.detail!, failedEvent.detail!);
        }

        // MySql.Data's messages can name the login, as this one does
        static MySqlException Refused(int number) =>
            (MySqlException)typeof(MySqlException)
                .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string), typeof(int), typeof(Exception)])!
                .Invoke([$"Access denied for user 'minerbot-bot'@'localhost'", number, null]);

        static Job Housekeeping() => new()
        {
            Name = nameof(HousekeepingJob.ApplyRetention),
            MethodInfo = typeof(HousekeepingJob).GetMethod(nameof(HousekeepingJob.ApplyRetention))!,
            Details = new JobAttribute("30 4 * * *"),
            DeclaringType = typeof(HousekeepingJob),
            NextRun = DateTimeOffset.Now.AddMinutes(-1)
        };

        // README lets the bot run without BotConnection: it records nothing and says so once as it starts. Its clean-up then
        // has nothing it can clean up, and does nothing, rather than fail with an error every night.
        [Theory]
        [InlineData(null, "isn't set")]
        [InlineData("Server=127.0.0.1;Uid=minerbot-bot;Pwd=password", "names no database")]
        [InlineData("Server=127.0.0.1;Database=bot_db;Uid=minerbot-bot;Pwd=secret;password", "can't be read")]
        [InlineData("Server=127.0.0.1;Database=bot_db;Uid=minerbot-bot;Use Performance Monitor=true", "can't be read")]
        public async Task WithoutBotConnectionItDoesNothing(string? connection, string problem)
        {
            var settings = BotRecorderTests.Settings(botConnection: connection);
            var logger = new RecordingLogger<JobHandler>();

            await using var services = new ServiceCollection()
                .AddSingleton<ILogger<HousekeepingJob>>(jobLogger)
                .AddDataAccessServices(settings)
                .AddRetentionAccess()
                .AddSingleton<IConfiguration>(settings)
                .AddSingleton<TimeProvider>(clock)
                .AddSingleton<BotRecorder>()
                .BuildServiceProvider();

            var handler = new JobHandler(logger, services);
            var job = new Job
            {
                Name = nameof(HousekeepingJob.ApplyRetention),
                MethodInfo = typeof(HousekeepingJob).GetMethod(nameof(HousekeepingJob.ApplyRetention))!,
                Details = new JobAttribute("30 4 * * *"),
                DeclaringType = typeof(HousekeepingJob),
                NextRun = DateTimeOffset.Now.AddMinutes(-1)
            };

            await handler.RunDueJobsAsync([job], TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            var run = Assert.Single(services.GetRequiredService<BotRecorder>().PeekJobRuns(10));

            Assert.Equal((JobResult.Ok, (string?)$"skipped, because ConnectionStrings:BotConnection {problem}"), (run.result, run.detail));
            Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
            Assert.Empty(jobLogger.Entries);
        }

        [Fact]
        public void RunsDailyAfterTheNightlyBackup()
        {
            var job = typeof(HousekeepingJob).GetMethod(nameof(HousekeepingJob.ApplyRetention))!.GetCustomAttribute<JobAttribute>();

            Assert.Equal("30 4 * * *", job!.Cron);
        }

        // As the bot runs it: JobHandler makes it with its services, and its counts become the run's detail, or its failure
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task JobHandlerRecordsItsCountsOrItsFailure(bool fails)
        {
            retention.Events = 2;

            if (fails)
                retention.Failures["DeleteSessions"] = new TimeoutException("The database didn't answer within 15 seconds");

            await using var services = new ServiceCollection()
                .AddSingleton<IRetentionData>(retention)
                .AddSingleton<TimeProvider>(clock)
                .AddSingleton<IConfiguration>(BotRecorderTests.Settings(botConnection: Connection))
                .AddSingleton<ILogger<HousekeepingJob>>(jobLogger)
                .AddSingleton<BotRecorder>()
                .BuildServiceProvider();

            var handler = new JobHandler(new RecordingLogger<JobHandler>(), services);
            var job = new Job
            {
                Name = nameof(HousekeepingJob.ApplyRetention),
                MethodInfo = typeof(HousekeepingJob).GetMethod(nameof(HousekeepingJob.ApplyRetention))!,
                Details = new JobAttribute("30 4 * * *"),
                DeclaringType = typeof(HousekeepingJob),
                NextRun = DateTimeOffset.Now.AddMinutes(-1)
            };

            await handler.RunDueJobsAsync([job], TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            var recorder = services.GetRequiredService<BotRecorder>();
            var run = Assert.Single(recorder.PeekJobRuns(10));
            var counts = "deleted 0 audit rows, 2 events, 0 server events, 0 job runs, 0 minutes (0 days totalled) and 0 sessions, and cleared 0 addresses";

            Assert.Equal("HousekeepingJob.ApplyRetention", run.job);
            Assert.Equal(fails ? JobResult.Failed : JobResult.Ok, run.result);
            Assert.Equal(fails ? $"{counts}; failed: sessions (TimeoutException)" : counts, run.detail);
            Assert.Equal(fails, recorder.PeekEvents(10).Any(row => row.kind == BotEvents.JobFailed));
        }
    }
}
