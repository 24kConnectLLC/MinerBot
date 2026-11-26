using System.Reflection;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using Microsoft.Extensions.DependencyInjection;
using MinerBot_2._0.Attributes;
using MinerBot_2._0.Handlers;
using MinerBot_2._0.Jobs;
using MinerBot_2._0.Models;
using MinerBot_2._0.Status;
using MySql.Data.MySqlClient;

namespace MinerBot.Tests
{
    // The owner's direct message about armor sent in for a review, on the real Discord.Net pipeline: at most once an hour
    // while new armor waits, new meaning above the floor of the first look and never told about, all of it kept in MinerBot's
    // database, which the memory store stands in for
    [Collection("Discord")]
    public class SubmissionAlertTests
    {
        // Made up, and never connected to: the memory store stands in for the database
        const string Connection = "Server=127.0.0.1;Database=bot_db;Uid=minerbot-bot;Pwd=password";

        const string SendStep = "send message";

        // Each run of the job is 5 minutes after the last, 2 past a notification run's mark, from 15:05:30 UTC
        static readonly DateTimeOffset Start = new(2026, 11, 24, 15, 0, 30, TimeSpan.Zero);

        // The website's time of a submission, by its own clock, as Dapper reads a DATETIME
        static PendingSubmission Submission(long id) => new() { ID = id, CreatedAt = new DateTime(2024, 8, 3, 18, 30, 0).AddDays(id) };

        static async Task<FakeDiscord> StartAsync(INotificationData? store = null, bool holdReady = false)
        {
            var discord = await FakeDiscord.StartAsync(holdReady: holdReady, store: store);
            discord.Settings["ConnectionStrings:BotConnection"] = Connection;
            discord.Clock.UtcNow = Start;
            return discord;
        }

        // One run as JobHandler makes it, 5 minutes after the last or as long after as given, its wait for the message short
        // unless the test gives one
        static Task<string> RunAsync(FakeDiscord discord, TimeSpan? after = null, TimeSpan? sendLimit = null)
        {
            discord.MoveClock(after ?? TimeSpan.FromMinutes(5));

            var job = ActivatorUtilities.CreateInstance<SubmissionAlertJob>(discord.Services);
            job.SendLimit = sendLimit ?? TimeSpan.FromSeconds(2);

            return job.AlertOwner().WaitAsync(job.SendLimit + TimeSpan.FromSeconds(13), TestContext.Current.CancellationToken);
        }

        // The first look, at 15:05:30, with the armor waiting and the highest armor ID given
        static async Task FirstLookAsync(FakeDiscord discord, long highest, params long[] pending)
        {
            discord.Armor.Pending = [.. pending.Select(Submission)];
            discord.Armor.HighestId = highest;

            Assert.StartsWith("took ", await RunAsync(discord));
        }

        static List<string> Messages(FakeDiscord discord) =>
            discord.Steps.Where(step => step.Name == SendStep && step.SentTo == FakeDiscord.DirectChannelId).Select(step => step.Content!).ToList();

        static List<(ulong, string)> Seen(FakeDiscord discord) =>
            discord.Store.Seen.Where(row => row.source == PostSource.Submissions).Select(row => (row.item_id, row.state)).ToList();

        static string Message(int fresh, int pending, string oldest) =>
            $"New armor is waiting for review: {fresh} submission{(fresh == 1 ? "" : "s")} since the last message, {pending} pending in all, the oldest sent on {oldest} by the "
            + "website's clock. Review them at https://bot.murderminershub.com/admin/armor?status=pending (MinerBot sends this at most once an hour.)";

        // MySql.Data's messages can name the login, as this one does
        static MySqlException Refused(int number) =>
            (MySqlException)typeof(MySqlException)
                .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string), typeof(int), typeof(Exception)])!
                .Invoke([$"Access denied for user 'minerbot-bot'@'localhost'", number, null]);

        // The job as the bot runs it, through JobHandler, with the bot's history: its run, and its job.failed event if any
        static async Task<(JobRun Run, BotEvent? Failed)> RunThroughJobHandlerAsync(FakeDiscord discord)
        {
            discord.MoveClock(TimeSpan.FromMinutes(5));
            var handler = new JobHandler(new RecordingLogger<JobHandler>(), discord.Services);
            var before = discord.Recorder.PeekJobRuns(100).Count;

            await handler.RunDueJobsAsync([AlertJob()], TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);

            var run = discord.Recorder.PeekJobRuns(100).Skip(before).Single();
            return (run, discord.Recorder.PeekEvents(100).LastOrDefault(row => row.kind == BotEvents.JobFailed));
        }

        static Job AlertJob() => new()
        {
            Name = nameof(SubmissionAlertJob.AlertOwner),
            MethodInfo = typeof(SubmissionAlertJob).GetMethod(nameof(SubmissionAlertJob.AlertOwner))!,
            Details = new JobAttribute("2-59/5 * * * *"),
            DeclaringType = typeof(SubmissionAlertJob),
            NextRun = DateTimeOffset.Now.AddMinutes(-1)
        };

        // The 7 that waited when the alerts began, on 4 November, bring no message: the Overview already shows them, with its
        // banner after 3 days
        [Fact]
        public async Task FirstCheckTakesTheBacklogAsSeenWithoutAMessage()
        {
            await using var discord = await StartAsync();
            discord.Armor.Pending = [Submission(3), Submission(5), Submission(9)];
            discord.Armor.HighestId = 12;

            Assert.Equal("took 3 pending submissions as seen, and every armor up to ID 12 as known, so no message went", await RunAsync(discord));

            var floor = Assert.Single(discord.Store.Baselines, row => row.source == PostSource.Submissions);

            Assert.Equal((BaselineReason.First, (ulong?)12, (DateTime?)null, 3u, discord.Recorder.RunId), (floor.reason, floor.high_id, floor.alerted_at, floor.item_count, floor.taken_by_run));
            Assert.Equal([(3UL, SeenState.Baseline), (5UL, SeenState.Baseline), (9UL, SeenState.Baseline)], Seen(discord));
            Assert.Equal(["GetPendingSubmissions", "GetHighestArmorId"], discord.StepNames);

            Assert.Equal("no new submissions (3 pending)", await RunAsync(discord));
            Assert.Empty(Messages(discord));
        }

        // The message holds counts, a date and the dashboard's link, never an armor's name or its author's, which are the
        // submitter's own text, and the bot's history keeps the counts alone in its place
        [Fact]
        public async Task NewSubmissionSendsOneMessage()
        {
            await using var discord = await StartAsync();
            await FirstLookAsync(discord, 3, 3);

            discord.Armor.Pending.Add(Submission(4));
            discord.Armor.Pending.Add(Submission(5));

            Assert.Equal("told the owner about 2 new submissions (3 pending)", await RunAsync(discord));

            var message = Assert.Single(Messages(discord));
            Assert.Equal(Message(2, 3, "2024-08-06"), message);
            Assert.True(discord.Single(SendStep).AllowsNoMentions);
            Assert.DoesNotContain(discord.Armor.AllArmor, armor => message.Contains(armor.ArmorName, StringComparison.Ordinal));
            Assert.Equal("new armor submissions: 2 new, 3 pending", Assert.Single(discord.Recorder.PeekEvents(100), row => row.kind == BotEvents.AlertSent).detail);

            Assert.Equal([(3UL, SeenState.Baseline), (4UL, SeenState.Alerted), (5UL, SeenState.Alerted)], Seen(discord));
            Assert.Equal(new DateTime(2026, 11, 24, 15, 10, 30, DateTimeKind.Utc), Assert.Single(discord.Store.Baselines, row => row.source == PostSource.Submissions).alerted_at);

            Assert.Equal("no new submissions (3 pending)", await RunAsync(discord));
            Assert.Single(Messages(discord));
        }

        [Fact]
        public async Task SecondSubmissionWithinTheHourWaitsForTheHour()
        {
            await using var discord = await StartAsync();
            await FirstLookAsync(discord, 10);

            discord.Armor.Pending.Add(Submission(11));
            await RunAsync(discord);
            discord.Armor.Pending.Add(Submission(12));

            Assert.Equal("1 new submission waits for the hourly message (the last try was at 15:10 UTC)", await RunAsync(discord));
            Assert.Equal("1 new submission waits for the hourly message (the last try was at 15:10 UTC)", await RunAsync(discord, TimeSpan.FromMinutes(54)));
            Assert.Single(Messages(discord));
        }

        [Fact]
        public async Task ItsMessageComesOnceTheHourIsOver()
        {
            await using var discord = await StartAsync();
            await FirstLookAsync(discord, 10);

            discord.Armor.Pending.Add(Submission(11));
            await RunAsync(discord);
            discord.Armor.Pending.Add(Submission(12));
            await RunAsync(discord);

            // 16:10:30, an hour after the try
            Assert.Equal("told the owner about 1 new submission (2 pending)", await RunAsync(discord, TimeSpan.FromMinutes(55)));
            Assert.Equal([Message(1, 1, "2024-08-14"), Message(1, 2, "2024-08-14")], Messages(discord));
            Assert.Equal([(11UL, SeenState.Alerted), (12UL, SeenState.Alerted)], Seen(discord));
        }

        // The hour is kept in MinerBot's database, so a restart, as the nightly Docker restart or a deploy, can't make two
        // messages in it
        [Fact]
        public async Task RestartDoesNotResetTheHour()
        {
            await using var discord = await StartAsync();
            await FirstLookAsync(discord, 10);

            discord.Armor.Pending.Add(Submission(11));
            await RunAsync(discord);
            discord.Armor.Pending.Add(Submission(12));

            await using var next = await discord.RestartAsync(TimeSpan.FromMinutes(2));
            next.Settings["ConnectionStrings:BotConnection"] = Connection;

            Assert.Equal("1 new submission waits for the hourly message (the last try was at 15:10 UTC)", await RunAsync(next));
            Assert.Empty(Messages(next));

            Assert.Equal("told the owner about 1 new submission (2 pending)", await RunAsync(next, TimeSpan.FromMinutes(53)));
            Assert.Single(Messages(next));
        }

        // The try is saved before its message goes: one MinerBot's database didn't take sends nothing, and the run fails
        // saying so; what it would have told about is told at the next run
        [Fact]
        public async Task TryThatCantBeSavedSendsNothing()
        {
            var store = new FailingNotificationData(new MemoryNotificationData());
            await using var discord = await StartAsync(store);
            await FirstLookAsync(discord, 10);

            discord.Armor.Pending.Add(Submission(11));
            store.FailNext(nameof(INotificationData.SaveDetectionAsync), new TimeoutException("The database didn't answer within 15 seconds"));

            var failed = await Assert.ThrowsAsync<JobFailedException>(() => RunAsync(discord));

            Assert.Equal("MinerBot's database didn't take the try (TimeoutException), so no message went", failed.Message);
            Assert.DoesNotContain("open direct message", discord.StepNames);
            Assert.Empty(Messages(discord));
            Assert.Null(Assert.Single(discord.Store.Baselines, row => row.source == PostSource.Submissions).alerted_at);

            Assert.Equal("told the owner about 1 new submission (1 pending)", await RunAsync(discord));
            Assert.Single(Messages(discord));
        }

        // A save whose answer never came may have landed, which would leave the submission told about with no message: it's
        // told once the try's hour is over, as after a message that failed
        [Fact]
        public async Task TryWhoseSaveMayHaveLandedIsToldAfterItsHour()
        {
            var store = new FailingNotificationData(new MemoryNotificationData());
            await using var discord = await StartAsync(store);
            await FirstLookAsync(discord, 10);

            discord.Armor.Pending.Add(Submission(11));
            store.FailNext(nameof(INotificationData.SaveDetectionAsync), new TimeoutException("The database didn't answer within 15 seconds"), lands: true);

            await Assert.ThrowsAsync<JobFailedException>(() => RunAsync(discord));

            Assert.Empty(Messages(discord));
            Assert.Equal([(11UL, SeenState.AlertFailed)], Seen(discord));
            Assert.Equal("1 new submission waits for the hourly message (the last try was at 15:10 UTC)", await RunAsync(discord));
            Assert.Equal("told the owner about 1 new submission (1 pending)", await RunAsync(discord, TimeSpan.FromMinutes(55)));
            Assert.Single(Messages(discord));
        }

        // PR 9's live check found the owner's direct messages refused by the owner's own setting, which Discord's code 50007
        // says. A refused message counts as the hour's try, fails the run, so the Overview's banner shows it, and its
        // submissions are told again once the hour is over.
        [Fact]
        public async Task FailedMessageRecordsDiscordsCodeFailsTheRunAndTriesAgainAfterAnHour()
        {
            await using var discord = await StartAsync();
            await FirstLookAsync(discord, 10);

            discord.Armor.Pending.Add(Submission(11));
            discord.ChannelFailures[FakeDiscord.DirectChannelId] = (403, 50007);

            var (run, failedEvent) = await RunThroughJobHandlerAsync(discord);
            const string failure = "the message to the owner failed (HttpException, HTTP 403, Discord 50007); the next try is after 16:10 UTC";

            Assert.Equal((JobResult.Failed, (string?)failure), (run.result, run.detail));
            Assert.Equal($"SubmissionAlertJob.AlertOwner failed: {failure}", failedEvent!.detail);
            Assert.Equal("HttpException, HTTP 403, Discord 50007: new armor submissions: 1 new, 1 pending",
                Assert.Single(discord.Recorder.PeekEvents(100), row => row.kind == BotEvents.AlertFailed).detail);
            Assert.DoesNotContain(discord.Recorder.PeekEvents(100), row => row.detail != null && row.detail.Contains(FakeDiscord.DiscordText));
            Assert.Equal([(11UL, SeenState.AlertFailed)], Seen(discord));

            discord.ChannelFailures.Clear();

            Assert.Equal("1 new submission waits for the hourly message (the last try was at 15:10 UTC)", await RunAsync(discord));
            Assert.Equal("told the owner about 1 new submission (1 pending)", await RunAsync(discord, TimeSpan.FromMinutes(55)));
            Assert.Equal([(11UL, SeenState.Alerted)], Seen(discord));
        }

        // A refused message whose failure MinerBot's database didn't take leaves its submissions told about: the run's failure
        // says so
        [Fact]
        public async Task FailedMessageTheDatabaseDidNotTakeSaysSo()
        {
            var store = new FailingNotificationData(new MemoryNotificationData());
            await using var discord = await StartAsync(store);
            await FirstLookAsync(discord, 10);

            discord.Armor.Pending.Add(Submission(11));
            discord.ChannelFailures[FakeDiscord.DirectChannelId] = (403, 50007);
            store.FailNext(nameof(INotificationData.SaveDetectionAsync), new TimeoutException("The database didn't answer within 15 seconds"), after: 1);

            var failed = await Assert.ThrowsAsync<JobFailedException>(() => RunAsync(discord));

            Assert.Equal("the message to the owner failed (HttpException, HTTP 403, Discord 50007), and MinerBot's database didn't take that (TimeoutException), "
                + "so these submissions aren't told about again", failed.Message);
            Assert.Equal([(11UL, SeenState.Alerted)], Seen(discord));
        }

        // A message whose answer didn't come within the run's limit may still arrive, so its submissions stay told about: at
        // most once. It's sent once, whatever comes after.
        [Fact]
        public async Task UnansweredMessageIsNotSentAgain()
        {
            await using var discord = await StartAsync();
            await FirstLookAsync(discord, 10);

            discord.Armor.Pending.Add(Submission(11));
            var hold = new TaskCompletionSource();
            discord.SendHold = hold.Task;

            Assert.Equal("the message to the owner about 1 new submission got no answer within 2 seconds, so it may still arrive, and isn't sent again (1 pending)",
                await RunAsync(discord));
            Assert.Equal([(11UL, SeenState.Alerted)], Seen(discord));

            hold.SetResult();
            discord.SendHold = null;

            Assert.Equal("no new submissions (1 pending)", await RunAsync(discord, TimeSpan.FromHours(2)));
            Assert.Single(Messages(discord));
        }

        // Discord's own trouble (5xx) may have made the message, as for the bot's posts, so it isn't sent again either
        [Fact]
        public async Task MessageDiscordDidNotAnswerIsNotSentAgain()
        {
            await using var discord = await StartAsync();
            await FirstLookAsync(discord, 10);

            discord.Armor.Pending.Add(Submission(11));
            discord.ChannelFailures[FakeDiscord.DirectChannelId] = (500, 0);

            Assert.Equal("Discord didn't answer the message to the owner about 1 new submission (HttpException, HTTP 500), so it may have arrived, and isn't sent again (1 pending)",
                await RunAsync(discord));

            discord.ChannelFailures.Clear();

            Assert.Equal("no new submissions (1 pending)", await RunAsync(discord, TimeSpan.FromHours(2)));
            Assert.Equal([(11UL, SeenState.Alerted)], Seen(discord));
        }

        // So may one whose answer never came, which HttpClient ends as a TaskCanceledException: it isn't sent again either
        [Fact]
        public async Task MessageWhoseAnswerNeverCameIsNotSentAgain()
        {
            await using var discord = await StartAsync();
            await FirstLookAsync(discord, 10);

            discord.Armor.Pending.Add(Submission(11));
            discord.Unanswered[FakeDiscord.DirectChannelId] = true;

            Assert.Equal("Discord didn't answer the message to the owner about 1 new submission (TaskCanceledException), so it may have arrived, and isn't sent again (1 pending)",
                await RunAsync(discord));

            discord.Unanswered.Clear();

            Assert.Equal("no new submissions (1 pending)", await RunAsync(discord, TimeSpan.FromHours(2)));
            Assert.Single(Messages(discord));
            Assert.Equal([(11UL, SeenState.Alerted)], Seen(discord));
        }

        // Discord.Net's default retries throw a TimeoutException only before a try, here once a rate limit answered past the
        // request's 15 s used them up: the message never went, so it's a failure, as a refusal is, and its submission is told
        // about once the hour is over. It takes the real 16 s, as Discord.Net times its requests by the system's clock.
        [Fact]
        public async Task MessageWhoseTriesWereRateLimitedPastItsTimeIsToldAgainAfterTheHour()
        {
            await using var discord = await StartAsync();
            await FirstLookAsync(discord, 10);

            discord.Armor.Pending.Add(Submission(11));
            discord.LateRateLimits[FakeDiscord.DirectChannelId] = TimeSpan.FromSeconds(16);

            var failed = await Assert.ThrowsAsync<JobFailedException>(() => RunAsync(discord, sendLimit: TimeSpan.FromSeconds(30)));

            Assert.Equal("the message to the owner failed (TimeoutException); the next try is after 16:10 UTC", failed.Message);
            Assert.Single(Messages(discord));
            Assert.Empty(discord.MessagesIn(FakeDiscord.DirectChannelId));
            Assert.Equal([(11UL, SeenState.AlertFailed)], Seen(discord));

            Assert.Equal("told the owner about 1 new submission (1 pending)", await RunAsync(discord, TimeSpan.FromHours(1)));
            Assert.Single(discord.MessagesIn(FakeDiscord.DirectChannelId));
            Assert.Equal([(11UL, SeenState.Alerted)], Seen(discord));
        }

        // The run's wait for its message can run out just as the message goes, so the send has ended by the time the run
        // reads the TimeoutException. That one is the run's own, never the send's: the message stays told, as one still under
        // way does, and isn't sent again. In the bot this lasts only a moment; here a context holds the run back from that
        // moment until the message has gone.
        [Fact]
        public async Task MessageThatGoesAsTheRunStopsWaitingIsNotSentAgain()
        {
            await using var discord = await StartAsync();
            await FirstLookAsync(discord, 10);

            discord.Armor.Pending.Add(Submission(11));
            var hold = new TaskCompletionSource();
            discord.SendHold = hold.Task;
            discord.MoveClock(TimeSpan.FromMinutes(5));

            var job = ActivatorUtilities.CreateInstance<SubmissionAlertJob>(discord.Services);
            job.SendLimit = TimeSpan.FromSeconds(2);

            // Once Discord has made the message and holds its answer, only the run's wait running out comes
            var context = new HoldingContext(() => !hold.Task.IsCompleted && discord.MessagesIn(FakeDiscord.DirectChannelId).Count > 0);
            var run = context.Start(job.AlertOwner);

            await context.HeldBack.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            // Discord answers and the send ends, recorded as sent, while the run is yet to read its TimeoutException
            hold.SetResult();
            discord.SendHold = null;
            await WaitUntilAsync(() => discord.Recorder.PeekEvents(100).Any(row => row.kind == BotEvents.AlertSent) && !context.Running, "The send never ended");
            Assert.False(run.IsCompleted);

            context.RunHeldBack();

            Assert.Equal("the message to the owner about 1 new submission got no answer within 2 seconds, so it may still arrive, and isn't sent again (1 pending)",
                await run.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
            Assert.Single(discord.MessagesIn(FakeDiscord.DirectChannelId));
            Assert.Equal([(11UL, SeenState.Alerted)], Seen(discord));
            Assert.DoesNotContain(discord.Recorder.PeekEvents(100), row => row.kind == BotEvents.AlertFailed);

            Assert.Equal("no new submissions (1 pending)", await RunAsync(discord, TimeSpan.FromHours(2)));
            Assert.Single(Messages(discord));
        }

        // Discord's trouble before the message's own request, here opening the direct channel, sent nothing: it's a failure,
        // as a refusal is, never a message that may have arrived, so the run fails and its submissions are told about once the
        // hour is over
        [Fact]
        public async Task TroubleBeforeTheMessageIsToldAgainAfterTheHour()
        {
            await using var discord = await StartAsync();
            await FirstLookAsync(discord, 10);

            discord.Armor.Pending.Add(Submission(11));
            discord.FailStep = "open direct message";
            discord.FailStepAnswer = (503, 0);

            var failed = await Assert.ThrowsAsync<JobFailedException>(() => RunAsync(discord));

            Assert.Equal("the message to the owner failed (HttpException, HTTP 503); the next try is after 16:10 UTC", failed.Message);
            Assert.Empty(Messages(discord));
            Assert.Equal("HttpException, HTTP 503: new armor submissions: 1 new, 1 pending",
                Assert.Single(discord.Recorder.PeekEvents(100), row => row.kind == BotEvents.AlertFailed).detail);
            Assert.Equal([(11UL, SeenState.AlertFailed)], Seen(discord));

            discord.FailStep = null;

            Assert.Equal("1 new submission waits for the hourly message (the last try was at 15:10 UTC)", await RunAsync(discord));
            Assert.Equal("told the owner about 1 new submission (1 pending)", await RunAsync(discord, TimeSpan.FromMinutes(55)));
            Assert.Single(Messages(discord));
            Assert.Equal([(11UL, SeenState.Alerted)], Seen(discord));
        }

        // A set of IDs, not a high-water mark: two sent in at once can be saved in either order, and the lower one, saved
        // after the higher was told about, is still new
        [Fact]
        public async Task OutOfOrderIdsAreAllTold()
        {
            await using var discord = await StartAsync();
            await FirstLookAsync(discord, 10);

            discord.Armor.Pending.Add(Submission(12));
            await RunAsync(discord);
            discord.Armor.Pending.Insert(0, Submission(11));

            Assert.Equal("1 new submission waits for the hourly message (the last try was at 15:10 UTC)", await RunAsync(discord));
            Assert.Equal("told the owner about 1 new submission (2 pending)", await RunAsync(discord, TimeSpan.FromMinutes(55)));
            Assert.Equal(2, Messages(discord).Count);
            Assert.Equal([(11UL, SeenState.Alerted), (12UL, SeenState.Alerted)], Seen(discord).Order());
        }

        // The floor: armor approved before the alerts began, which the Owner moves back to pending, was never seen as pending,
        // but isn't new either, the highest armor at the first look, which sets the floor, included
        [Theory]
        [InlineData(7)]
        [InlineData(9)]
        public async Task ArmorApprovedBeforeTheFirstLookAndMovedBackIsNotNew(long movedBack)
        {
            await using var discord = await StartAsync();
            await FirstLookAsync(discord, 9, 5);

            discord.Armor.Pending.Add(Submission(movedBack));

            Assert.Equal("no new submissions (2 pending)", await RunAsync(discord));
            Assert.Equal("no new submissions (2 pending)", await RunAsync(discord, TimeSpan.FromHours(2)));
            Assert.Empty(Messages(discord));
        }

        [Fact]
        public async Task ArmorMovedBackAfterBeingToldIsNotNew()
        {
            await using var discord = await StartAsync();
            await FirstLookAsync(discord, 10);

            discord.Armor.Pending.Add(Submission(11));
            await RunAsync(discord);
            discord.Armor.Pending.Clear();
            await RunAsync(discord);
            discord.Armor.Pending.Add(Submission(11));

            Assert.Equal("no new submissions (1 pending)", await RunAsync(discord, TimeSpan.FromHours(2)));
            Assert.Single(Messages(discord));
        }

        [Fact]
        public async Task SubmissionReviewedBeforeTheHourBringsNoMessage()
        {
            await using var discord = await StartAsync();
            await FirstLookAsync(discord, 10);

            discord.Armor.Pending.Add(Submission(11));
            await RunAsync(discord);
            discord.Armor.Pending.Add(Submission(12));
            await RunAsync(discord);
            discord.Armor.Pending.RemoveAt(1);

            Assert.Equal("no new submissions (1 pending)", await RunAsync(discord, TimeSpan.FromMinutes(55)));
            Assert.Single(Messages(discord));
        }

        // OwnerAlertService waits up to 2 minutes for Discord, which a run of this job never does: it looks at nothing until
        // Discord is ready
        [Fact]
        public async Task DiscordNotReadySendsNothing()
        {
            await using var discord = await StartAsync(holdReady: true);
            discord.Armor.Pending = [Submission(11)];
            discord.Armor.HighestId = 11;

            Assert.Equal("Discord isn't ready yet, so this run didn't look for new submissions", await RunAsync(discord));
            Assert.Empty(discord.StepNames);
            Assert.Empty(discord.Store.Baselines);

            await discord.ReleaseReadyAsync();

            Assert.StartsWith("took 1 pending submission as seen", await RunAsync(discord));
        }

        // The website's database being down is a failure of the notification run already: this run says so in its detail
        // rather than raise job.failed every 5 minutes, with MariaDB's number and nothing of its message
        [Fact]
        public async Task WebsiteOutageIsADetailNotAFailedJob()
        {
            await using var discord = await StartAsync();
            discord.Armor.Failure = Refused(1045);

            var (run, failedEvent) = await RunThroughJobHandlerAsync(discord);

            Assert.Equal((JobResult.Ok, (string?)"couldn't read the armor waiting for a review (MySqlException, MySQL error 1045)"), (run.result, run.detail));
            Assert.Null(failedEvent);
            Assert.Empty(discord.Store.Baselines);
            Assert.DoesNotContain("minerbot-bot", run.detail);
        }

        // Without MinerBot's database there's nowhere to keep the hour, so the job does nothing, as the clean-up
        [Fact]
        public async Task WithoutBotConnectionItDoesNothing()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Armor.Pending = [Submission(11)];

            Assert.Equal("skipped, because ConnectionStrings:BotConnection isn't set", await RunAsync(discord));
            Assert.Empty(discord.StepNames);
        }

        // MinerBot's database down is the run's failure, with the exception's type and MariaDB's number
        [Fact]
        public async Task MinerBotsDatabaseDownFailsTheRun()
        {
            var store = new FailingNotificationData(new MemoryNotificationData());
            await using var discord = await StartAsync(store);
            store.FailAlways(nameof(INotificationData.ListBaselinesAsync), Refused(1042));

            var failed = await Assert.ThrowsAsync<JobFailedException>(() => RunAsync(discord));

            Assert.Equal("new-submission alerts wait: MinerBot's database didn't answer (MySqlException, MySQL error 1042)", failed.Message);
            Assert.Empty(Messages(discord));
        }

        // 2 minutes after each notification run, so a slow website read or a slow message never shares its pass in JobHandler
        [Fact]
        public void RunsTwoMinutesAfterEachNotificationRun()
        {
            Assert.Equal("2-59/5 * * * *", typeof(SubmissionAlertJob).GetMethod(nameof(SubmissionAlertJob.AlertOwner))!.GetCustomAttribute<JobAttribute>()!.Cron);
            Assert.Equal("*/5 * * * *", typeof(GuildNotificationJob).GetMethod(nameof(GuildNotificationJob.NotifyAllGuilds))!.GetCustomAttribute<JobAttribute>()!.Cron);
        }

        static async Task WaitUntilAsync(Func<bool> condition, string because)
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();

            while (!condition())
            {
                if (waited.Elapsed > TimeSpan.FromSeconds(15))
                    Assert.Fail(because);

                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        // Runs what's posted to it at once, on the thread that posts it, with itself as the context, so a run started in it
        // goes on in it after each wait. What comes while holds says so waits for RunHeldBack.
        sealed class HoldingContext(Func<bool> holds) : SynchronizationContext
        {
            readonly Queue<(SendOrPostCallback Callback, object? State)> held = new();
            readonly TaskCompletionSource heldBack = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int running;

            // Done once something is held back
            public Task HeldBack => heldBack.Task;

            // Whether something posted to it runs now, on any thread
            public bool Running => Volatile.Read(ref running) > 0;

            public Task<T> Start<T>(Func<Task<T>> start)
            {
                var before = Current;
                SetSynchronizationContext(this);

                try
                {
                    return start();
                }
                finally
                {
                    SetSynchronizationContext(before);
                }
            }

            public override void Post(SendOrPostCallback callback, object? state)
            {
                if (holds())
                {
                    lock (held)
                        held.Enqueue((callback, state));

                    heldBack.TrySetResult();
                    return;
                }

                Run(callback, state);
            }

            public override void Send(SendOrPostCallback callback, object? state) => Run(callback, state);

            public void RunHeldBack()
            {
                while (true)
                {
                    (SendOrPostCallback Callback, object? State) next;

                    lock (held)
                    {
                        if (!held.TryDequeue(out next))
                            return;
                    }

                    Run(next.Callback, next.State);
                }
            }

            void Run(SendOrPostCallback callback, object? state)
            {
                var before = Current;
                Interlocked.Increment(ref running);
                SetSynchronizationContext(this);

                try
                {
                    callback(state);
                }
                finally
                {
                    SetSynchronizationContext(before);
                    Interlocked.Decrement(ref running);
                }
            }
        }
    }
}
