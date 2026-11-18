using System.Reflection;
using Cronos;
using DataAccessLibrary;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Status;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MinerBot_2._0.Attributes;
using MinerBot_2._0.Handlers;
using MinerBot_2._0.Jobs;
using MinerBot_2._0.Models;
using MinerBot_2._0.Status;

namespace MinerBot.Tests
{
    // The daily member count, from Discord's API through FakeDiscord, against a stand-in for MinerBot's database
    [Collection("Discord")]
    public class GuildMembersJobTests
    {
        // Made up, and never connected to
        const string Connection = "Server=127.0.0.1;Database=bot_db;Uid=minerbot-bot;Pwd=password";

        // 04:21 in US Eastern time, when the job runs
        static readonly DateTimeOffset RunTime = new(2026, 11, 17, 9, 21, 0, TimeSpan.Zero);
        static readonly DateTime Today = new(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);

        readonly FakeBotGuildData data = new();
        readonly RecordingLogger<GuildMembersJob> logger = new();

        GuildMembersJob Job(FakeDiscord discord, string? connection = Connection) =>
            new(discord.Client, data, discord.Clock, BotRecorderTests.Settings(botConnection: connection), logger) { Pause = TimeSpan.Zero };

        // The first guild, and one the bot joins, each with its count on Discord's side
        static async Task<FakeDiscord> StartWithTwoServersAsync()
        {
            var discord = await FakeDiscord.StartAsync();
            await discord.JoinGuildAsync($"[{FakeDiscord.BotMemberJson(FakeDiscord.BotRoleId)}]");
            discord.Clock.UtcNow = RunTime;
            discord.MemberCounts[FakeDiscord.GuildId] = 57;
            discord.MemberCounts[FakeDiscord.JoinedGuildId] = 3;
            return discord;
        }

        static IEnumerable<string> Requests(FakeDiscord discord) => discord.Steps.Where(step => step.Name == "get guild").Select(step => step.Endpoint);

        // One request a server, and the count in the server's row and in the UTC day's row
        [Fact]
        public async Task EachServersCountIsWrittenForTheDay()
        {
            await using var discord = await StartWithTwoServersAsync();

            var detail = await Job(discord).CountMembers();

            Assert.Equal("counted the members of 2 of 2 servers", detail);
            Assert.Equal([(FakeDiscord.GuildId, 57u, Today), (FakeDiscord.JoinedGuildId, 3u, Today)], data.Counts);
            Assert.Equal([$"guilds/{FakeDiscord.GuildId}?with_counts=true", $"guilds/{FakeDiscord.JoinedGuildId}?with_counts=true"], Requests(discord));
            Assert.Empty(logger.Entries);
        }

        // A request that fails leaves its server for the next day, and the detail and the log say so, by the error's type only
        [Fact]
        public async Task FailedRequestLeavesItsServerForTheNextDay()
        {
            await using var discord = await StartWithTwoServersAsync();
            discord.FailStep = "get guild";
            discord.FailStepAfter = 1;

            var detail = await Job(discord).CountMembers();

            Assert.Equal("counted the members of 1 of 2 servers, and 1 request failed (HttpException)", detail);
            Assert.Equal([(FakeDiscord.GuildId, 57u, Today)], data.Counts);
            Assert.True(logger.Logged(LogLevel.Warning, "Some of the daily member count's requests failed (HttpException), and their servers are left for the next day"));
            Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("Missing Permissions"));
        }

        // A request Discord.Net keeps waiting on, as one behind a rate limit, is given up after its limit, and the run goes on
        // to the next server
        [Fact]
        public async Task RequestThatNeverEndsIsGivenUp()
        {
            await using var discord = await StartWithTwoServersAsync();
            discord.UnansweredGuilds.Add(FakeDiscord.GuildId);
            var job = Job(discord);
            job.RequestLimit = TimeSpan.FromMilliseconds(200);

            var detail = await job.CountMembers().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            Assert.Equal("counted the members of 1 of 2 servers, and 1 request failed (TimeoutException)", detail);
            Assert.Equal([(FakeDiscord.JoinedGuildId, 3u, Today)], data.Counts);
        }

        // After its time budget no request starts, so the job always ends before JobHandler cuts it off
        [Fact]
        public async Task TimeBudgetLeavesTheRestForTheNextRun()
        {
            await using var discord = await StartWithTwoServersAsync();
            var job = Job(discord);
            job.TimeBudget = TimeSpan.Zero;

            Assert.Equal("counted the members of 1 of 2 servers; 1 left for the next run", await job.CountMembers());
            Assert.Single(Requests(discord));
        }

        // A write that fails ends the run, which fails, as the writes after it would too
        [Fact]
        public async Task DatabaseThatFailsEndsTheRun()
        {
            await using var discord = await StartWithTwoServersAsync();
            data.Failure = new TimeoutException("The database didn't answer within 15 seconds");

            var failed = await Assert.ThrowsAsync<JobFailedException>(() => Job(discord).CountMembers());

            Assert.Equal("counted the members of 0 of 2 servers; failed: saving them (TimeoutException)", failed.Message);
            Assert.Single(Requests(discord));
        }

        // The bot runs without BotConnection, and said so as it started: the job asks Discord nothing
        [Theory]
        [InlineData(null, "isn't set")]
        [InlineData("Server=127.0.0.1;Uid=minerbot-bot;Pwd=password", "names no database")]
        public async Task WithoutBotConnectionNothingIsAsked(string? connection, string problem)
        {
            await using var discord = await StartWithTwoServersAsync();

            Assert.Equal($"skipped, because ConnectionStrings:BotConnection {problem}", await Job(discord, connection).CountMembers());
            Assert.Empty(Requests(discord));
            Assert.Equal(0, data.Calls);
            Assert.Empty(logger.Entries);
        }

        // At a minute no other job runs at, with none due in the 4 minutes after it either, so a run that starts on time holds
        // none up: JobHandler runs the jobs that are due one after another. It starts late when the 04:20 notification run
        // takes over a minute.
        [Fact]
        public void RunsDailyAtAMinuteNoOtherJobUses()
        {
            var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var jobs = typeof(JobHandler).Assembly.GetTypes()
                .SelectMany(type => type.GetMethods())
                .Select(method => (Method: method, Job: method.GetCustomAttribute<JobAttribute>()))
                .Where(found => found.Job != null)
                .ToList();

            bool IsCount(MethodInfo method) => method.DeclaringType == typeof(GuildMembersJob) && method.Name == nameof(GuildMembersJob.CountMembers);

            var runs = CronExpression.Parse(jobs.Single(found => IsCount(found.Method)).Job!.Cron).GetOccurrences(from, from.AddDays(8)).ToList();
            var others = jobs.Where(found => !IsCount(found.Method)).SelectMany(found => CronExpression.Parse(found.Job!.Cron).GetOccurrences(from, from.AddDays(8))).ToList();

            Assert.Equal(8, runs.Count);
            Assert.DoesNotContain(runs, run => others.Any(other => other >= run && other < run.AddMinutes(4)));
        }

        // The last request and its write, each at its limit, end within the 4 minutes, and well inside JobHandler's limit
        [Fact]
        public void TimeBudgetLeavesRoomForTheLastServer()
        {
            using var client = new DiscordSocketClient();
            var job = new GuildMembersJob(client, data, TimeProvider.System, BotRecorderTests.Settings(), logger);
            var callLimit = new BotGuildData(new FakeDatabase(), new DatabaseNames(new Dictionary<DBConnectionName, string>())).CallLimit;
            var jobLimit = new JobHandler(NullLogger<JobHandler>.Instance, new ServiceCollection().BuildServiceProvider()).JobTimeLimit;
            var longest = job.TimeBudget + job.Pause + job.RequestLimit + callLimit;

            Assert.True(longest < TimeSpan.FromMinutes(4) && longest < jobLimit, $"The member count may run {longest}");
        }

        // As the bot runs it: JobHandler makes it with the services Program.cs registers, and its counts become the run's detail
        [Fact]
        public async Task JobHandlerRecordsItsCounts()
        {
            await using var discord = await StartWithTwoServersAsync();
            await using var services = new ServiceCollection()
                .AddSingleton(discord.Client)
                .AddSingleton<IBotGuildData>(data)
                .AddSingleton<TimeProvider>(discord.Clock)
                .AddSingleton<IConfiguration>(BotRecorderTests.Settings(botConnection: Connection))
                .AddSingleton<ILogger<GuildMembersJob>>(logger)
                .AddSingleton<BotRecorder>()
                .BuildServiceProvider();

            var handler = new JobHandler(new RecordingLogger<JobHandler>(), services);
            var job = new Job
            {
                Name = nameof(GuildMembersJob.CountMembers),
                MethodInfo = typeof(GuildMembersJob).GetMethod(nameof(GuildMembersJob.CountMembers))!,
                Details = new JobAttribute("21 4 * * *"),
                DeclaringType = typeof(GuildMembersJob),
                NextRun = DateTimeOffset.Now.AddMinutes(-1)
            };

            await handler.RunDueJobsAsync([job], TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            var run = Assert.Single(services.GetRequiredService<BotRecorder>().PeekJobRuns(10));
            Assert.Equal(("GuildMembersJob.CountMembers", JobResult.Ok, (string?)"counted the members of 2 of 2 servers"), (run.job, run.result, run.detail));
            Assert.Equal(2, data.Counts.Count);
        }
    }
}
