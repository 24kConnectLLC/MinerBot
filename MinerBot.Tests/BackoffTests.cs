using DataAccessLibrary.Posting;
using Discord;
using MinerBot.Shared.Delivery;
using MinerBot.Shared.Requests;
using MinerBot_2._0.Posting;
using MinerBot_2._0.Status;
using static MinerBot.Tests.Posts;

namespace MinerBot.Tests
{
    // Not hammering Discord: a server's kind whose posts keep being refused waits longer before each next try, 6 hours at
    // most, and the wait ends early when something that could fix it changes, after a refresh of the server's copy, or after
    // a test message that went; a rate limit that reaches the bot, a refused token and two posts Discord didn't answer stop
    // a run's posts, counting against no server; and a run claims at most 50 messages, and none after 200 s.
    [Collection("Discord")]
    public class BackoffTests
    {
        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

        // 5 minutes, which is the next run, then 15, an hour, 3 hours and 6 hours, never longer, each from the start of the run
        // that failed: a run before the try's time doesn't try, one at it does, and so does one up to a minute early, as the
        // next run can start earlier in its minute than the one that failed
        [Fact]
        public async Task RefusedChannelIsTriedAgainOnlyAfterItsWait()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.ChannelFailures[FakeDiscord.ChannelId] = (403, 50013);

            await discord.RunNotificationJobAsync();
            AssertWaits(discord, 1, TimeSpan.FromMinutes(5));

            // A run asked for from the dashboard before then
            await discord.RunNotificationJobAsync(TimeSpan.FromMinutes(2));
            Assert.Single(Sends(discord));

            await discord.RunNotificationJobAsync(TimeSpan.FromSeconds(150));
            Assert.Equal(2, Sends(discord).Count);

            uint failures = 2;

            foreach (var minutes in new[] { 15, 60, 180, 360, 360, 360 })
            {
                AssertWaits(discord, failures, TimeSpan.FromMinutes(minutes));

                var tries = Sends(discord).Count;
                discord.MoveClock(TimeSpan.FromMinutes(minutes - 10));
                await discord.RunNotificationJobAsync();

                Assert.Equal(tries, Sends(discord).Count);

                await discord.RunNotificationJobAsync();

                Assert.Equal(tries + 1, Sends(discord).Count);
                failures++;
            }

            Assert.All(discord.Store.Deliveries, row => Assert.Equal((DeliveryState.Failed, (string?)DeliveryReasons.DiscordPermissions), (row.state, row.reason)));
            Assert.Equal(QueueState.Pending, Assert.Single(discord.Store.Queue).state);
        }

        // The status's tries in a row, and its next try from the start of the run that failed last
        static void AssertWaits(FakeDiscord discord, uint failures, TimeSpan wait)
        {
            var status = Status(discord, FakeDiscord.GuildId, PostSource.Armor)!;
            Assert.Equal((failures, (DateTime?)(discord.Store.Deliveries[^1].run_started_at + wait)), (status.failures, status.next_try_at));
        }

        // Three refusals from the bot's copy in a row: the next try is an hour away, unless what could fix it changes, which
        // the next run notices: a permission there, the channel the server set, or the server's switches saved again
        [Theory]
        [InlineData("Send Messages given back in the channel")]
        [InlineData("another channel set for armor")]
        [InlineData("the server's switches saved again")]
        public async Task WaitEndsWhenTheChannelOrAPermissionChanges(string change)
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Guilds.AllGuilds = [FakeGuildData.CreateGuild(FakeDiscord.NoSendChannelId)];
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));

            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();
            discord.MoveClock(TimeSpan.FromMinutes(10));
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal(3u, Status(discord, FakeDiscord.GuildId, PostSource.Armor)!.failures);
            Assert.Equal(3, discord.Store.Deliveries.Count);

            switch (change)
            {
                case "Send Messages given back in the channel":
                    await discord.DeliverAsync("CHANNEL_UPDATE", FakeDiscord.ChannelEventJson(FakeDiscord.GuildId, FakeDiscord.NoSendChannelId, 1));
                    break;
                case "another channel set for armor":
                    discord.Guilds.AllGuilds[0].armor_channel_id = FakeDiscord.ChannelId;
                    break;
                default:
                    discord.Store.SetSwitches(Switches(FakeDiscord.GuildId));
                    break;
            }

            await discord.RunNotificationJobAsync();

            if (change == "the server's switches saved again")
            {
                // Tried at once, and still refused by the copy
                Assert.Equal(4, discord.Store.Deliveries.Count);
                Assert.Empty(Sends(discord));
            }
            else
            {
                Assert.Equal(ArmorTitle, Assert.Single(Sends(discord)).EmbedTitles.Single());
                Assert.Equal(QueueState.Sent, Assert.Single(discord.Store.Queue).state);
            }
        }

        // Discord refused the posts, though the bot's copy shows it can post there, so the copy can't show the fix: a test
        // message that went to the channel from the dashboard ends the wait, and so does a refresh of the server's copy
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task WaitEndsAfterARefreshOrATestMessageThatWent(bool testMessage)
        {
            await using var bot = await RequestBot.StartAsync();
            var discord = bot.Discord;
            bot.Pause.Load(false, null, bot.Clock);
            await discord.RunNotificationJobAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.ChannelFailures[FakeDiscord.ChannelId] = (403, 50013);

            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();
            discord.MoveClock(TimeSpan.FromMinutes(10));
            await discord.RunNotificationJobAsync();
            discord.ChannelFailures.Clear();
            await discord.RunNotificationJobAsync();

            Assert.Equal(3, ArmorPosts(discord));
            Assert.Equal(3u, Status(discord, FakeDiscord.GuildId, PostSource.Armor)!.failures);

            if (testMessage)
            {
                var id = bot.Add(RequestKinds.GuildTestMessage, guildId: FakeDiscord.GuildId, channelId: FakeDiscord.ChannelId);
                await bot.TickAsync();

                Assert.Equal(RequestStates.Done, bot.Row(id).state);
            }
            else
            {
                var copy = new FakeBotGuildData();
                var writer = new GuildWriter(discord.Client, discord.Watcher, copy, bot.Recorder, BotRecorderTests.Settings(botConnection: RequestBot.Connection), discord.Clock,
                    new RecordingLogger<GuildWriter>())
                {
                    Interval = TimeSpan.FromMilliseconds(20)
                };

                await writer.TickAsync(TestContext.Current.CancellationToken);
                var id = bot.Add(RequestKinds.GuildRefresh, guildId: FakeDiscord.GuildId);
                await writer.StartAsync(TestContext.Current.CancellationToken);

                try
                {
                    await bot.TickAsync();
                }
                finally
                {
                    await writer.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
                }

                Assert.Equal(RequestStates.Done, bot.Row(id).state);
            }

            var status = Status(discord, FakeDiscord.GuildId, PostSource.Armor)!;
            Assert.Equal(((DateTime?)null, (ulong?)null, 3u), (status.next_try_at, status.waiting_on, status.failures));

            await discord.RunNotificationJobAsync();

            Assert.Equal(4, ArmorPosts(discord));
            Assert.Equal(QueueState.Sent, Assert.Single(discord.Store.Queue).state);
        }

        static int ArmorPosts(FakeDiscord discord) => Sends(discord).Count(step => step.EmbedTitles.Contains(ArmorTitle));

        // A post Discord didn't answer, found sent in the channel, ends the server's kind's failures in a row, so a refusal in
        // the same run waits 5 minutes, as the first of a new row does
        [Fact]
        public async Task PostFoundSentStartsTheFailuresInARowAgain()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.ChannelFailures[FakeDiscord.ChannelId] = (403, 50013);
            await discord.RunNotificationJobAsync();

            discord.ChannelFailures.Clear();
            discord.Unanswered[FakeDiscord.ChannelId] = true;
            await discord.RunNotificationJobAsync();

            Assert.Equal(1u, Status(discord, FakeDiscord.GuildId, PostSource.Armor)!.failures);

            discord.Unanswered.Clear();
            discord.ChannelFailures[FakeDiscord.ChannelId] = (403, 50013);
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId + 1));
            await discord.RunNotificationJobAsync();

            Assert.Equal(DeliveryState.Sent, discord.Store.Deliveries[1].state);
            AssertWaits(discord, 1, TimeSpan.FromMinutes(5));
        }

        // So does a message of a post that went: after two refusals in a row, a post of two messages whose second is refused
        // waits 5 minutes, as the first of a new row does, not the hour a third refusal in a row would
        [Fact]
        public async Task LaterMessageOfAPostRefusedAfterTheFirstWentWaitsAsTheFirstOfARow()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.ChannelFailures[FakeDiscord.ChannelId] = (403, 50013);
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            AssertWaits(discord, 2, TimeSpan.FromMinutes(15));

            discord.ChannelFailures.Clear();
            discord.Armor.AllArmor.AddRange(Enumerable.Range(NewId + 1, 200).Select(id => ArmorEmbedTests.Armor(id, $"Long armor style name {id}")));
            discord.FailStep = SendStep;
            discord.FailStepAfter = 1;
            discord.MoveClock(TimeSpan.FromMinutes(10));
            await discord.RunNotificationJobAsync();

            Assert.Equal([(DeliveryState.Sent, (byte)1), (DeliveryState.Failed, (byte)2)], discord.Store.Deliveries.Skip(2).Select(row => (row.state, row.part)));
            AssertWaits(discord, 1, TimeSpan.FromMinutes(5));
        }

        // A 429 that Discord.Net waits out is a slower post. One that reaches the bot is about every post, not the server's:
        // the run stops posting, and its item waits for the next run with no failure counted and no wait of its own.
        [Fact]
        public async Task RateLimitStopsTheRunAndCountsNoFailure()
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.RateLimits[FakeDiscord.ChannelId] = 1;
            var welcome = Sends(discord).Count;

            Assert.Equal("1 armor: posted in 2 of 2 servers", await discord.RunNotificationJobAsync());

            Assert.Equal(welcome + 3, Sends(discord).Count);
            Assert.Single(discord.MessagesIn(FakeDiscord.ChannelId));
            Assert.All(discord.Store.Deliveries, row => Assert.Equal(DeliveryState.Sent, row.state));

            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId + 1));
            discord.ChannelFailures[FakeDiscord.ChannelId] = (429, 0);

            Assert.Equal("1 armor: posted in 0 of 1 server, 1 failed; 1 item waits for 2 servers; Discord rate-limited the bot, so this run stopped posting",
                await discord.RunNotificationJobAsync());

            Assert.Equal(welcome + 4, Sends(discord).Count);

            var limited = discord.Store.Deliveries[^1];
            Assert.Equal((DeliveryState.Failed, (string?)DeliveryReasons.RateLimited, (ushort?)429), (limited.state, limited.reason, limited.http_status));

            var status = Status(discord, FakeDiscord.GuildId, PostSource.Armor)!;
            Assert.Equal((0u, (DateTime?)null, (DateTime?)null), (status.failures, status.failing_since, status.next_try_at));
            Assert.Contains("The notification run stopped posting: Discord rate-limited the bot, so this run stopped posting", discord.ConsoleText);

            discord.ChannelFailures.Clear();

            Assert.Equal("1 armor: posted in 2 of 2 servers", await discord.RunNotificationJobAsync());
        }

        // A refused token is about every post too
        [Fact]
        public async Task RefusedTokenStopsTheRunAndCountsNoFailure()
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.ChannelFailures[FakeDiscord.ChannelId] = (401, 0);

            Assert.Equal("1 armor: posted in 0 of 1 server, 1 failed; 1 item waits for 2 servers; Discord refused the bot's token, so this run stopped posting",
                await discord.RunNotificationJobAsync());

            var refused = Assert.Single(discord.Store.Deliveries);
            Assert.Equal((DeliveryState.Failed, (string?)DeliveryReasons.Unauthorized, (ushort?)401, (uint?)null),
                (refused.state, refused.reason, refused.http_status, refused.discord_code));
            Assert.Null(Status(discord, FakeDiscord.GuildId, PostSource.Armor));

            discord.ChannelFailures.Clear();

            Assert.Equal("1 armor: posted in 2 of 2 servers", await discord.RunNotificationJobAsync());
        }

        // A 429 that came back as the request's own limit ran out: Discord.Net sends again only after a 429, and finds the
        // request's time up before that try, so it throws a TimeoutException, with nothing made. It's the rate limit that
        // reached the bot: no failure counted, the item waits, and the run stops posting; the next run sends it once.
        [Fact]
        public async Task RateLimitAnsweredAsTheRequestsLimitRunsOutStopsTheRunAndCountsNoFailure()
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Gate.RequestLimit = TimeSpan.FromMilliseconds(300);
            discord.LateRateLimits[FakeDiscord.ChannelId] = TimeSpan.FromSeconds(1);

            Assert.Equal("1 armor: posted in 0 of 1 server, 1 failed; 1 item waits for 2 servers; Discord rate-limited the bot, so this run stopped posting",
                await discord.RunNotificationJobAsync());

            var limited = Assert.Single(discord.Store.Deliveries);
            Assert.Equal((DeliveryState.Failed, (string?)DeliveryReasons.RateLimited, (ushort?)null), (limited.state, limited.reason, limited.http_status));
            Assert.Null(Status(discord, FakeDiscord.GuildId, PostSource.Armor));
            Assert.Empty(discord.MessagesIn(FakeDiscord.ChannelId));

            discord.Gate.RequestLimit = PostingLimits.RequestLimit;

            Assert.Equal("1 armor: posted in 2 of 2 servers", await discord.RunNotificationJobAsync());
            Assert.Single(discord.MessagesIn(FakeDiscord.ChannelId));
        }

        // A 429 whose retry_after outlasts the request's own limit: Discord.Net waits for it until that limit ends the wait, as
        // it ends a request Discord never answered, and the exception can't say which, so it reads as a post Discord didn't
        // answer, though nothing was made. No failure counts against the server, the run goes on to the other servers rather
        // than wait, and the next run's look in the channel finds it wasn't made and sends it once; where the bot can't read the
        // channel, it ends as one that may not have arrived.
        [Fact]
        public async Task RateLimitThatOutlastsTheRequestsLimitReadsAsAPostDiscordDidntAnswer()
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Gate.RequestLimit = TimeSpan.FromMilliseconds(300);
            discord.LongRateLimits[FakeDiscord.ChannelId] = TimeSpan.FromSeconds(1);

            Assert.Equal("1 armor: posted in 1 of 2 servers, 1 not answered", await discord.RunNotificationJobAsync());

            Assert.Empty(discord.MessagesIn(FakeDiscord.ChannelId));
            Assert.Equal([(FakeDiscord.GuildId, DeliveryState.InDoubt, (string?)DeliveryReasons.NoAnswer, (ushort?)null), (FakeDiscord.JoinedGuildId, DeliveryState.Sent, null, null)],
                discord.Store.Deliveries.Select(row => (row.guild_id, row.state, row.reason, row.http_status)));
            Assert.Null(Status(discord, FakeDiscord.GuildId, PostSource.Armor));

            discord.Gate.RequestLimit = PostingLimits.RequestLimit;

            Assert.Equal("1 armor: posted in 1 of 1 server", await discord.RunNotificationJobAsync());
            Assert.Single(discord.Steps, step => step.Name == "get messages");
            Assert.Single(discord.MessagesIn(FakeDiscord.ChannelId));
            Assert.Equal([(DeliveryState.Failed, (string?)DeliveryReasons.NotSent), (DeliveryState.Sent, null)],
                discord.Store.Deliveries.Where(row => row.channel_id == FakeDiscord.ChannelId).Select(row => (row.state, row.reason)));
        }

        // A send whose connection to Discord never opened never left the bot, so nothing was made: it isn't held in doubt,
        // which in a channel the bot can't read would count it as sent; it's about every post, so the run stops posting, and
        // its item waits for the next run with no failure counted
        [Fact]
        public async Task ConnectionThatWontOpenStopsTheRunAndCountsNoFailure()
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);

            // A channel where nothing gives the bot Read Message History
            discord.Guilds.AllGuilds[0].armor_channel_id = FakeDiscord.AnnouncementChannelId;
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.NoConnection[FakeDiscord.AnnouncementChannelId] = true;

            Assert.Equal("1 armor: posted in 0 of 1 server, 1 failed; 1 item waits for 2 servers; the bot couldn't connect to Discord, so this run stopped posting",
                await discord.RunNotificationJobAsync());

            var notSent = Assert.Single(discord.Store.Deliveries);
            Assert.Equal((DeliveryState.Failed, (string?)DeliveryReasons.NotSent), (notSent.state, notSent.reason));
            Assert.Null(Status(discord, FakeDiscord.GuildId, PostSource.Armor));
            Assert.Empty(discord.MessagesIn(FakeDiscord.AnnouncementChannelId));

            discord.NoConnection.Clear();

            Assert.Equal("1 armor: posted in 2 of 2 servers", await discord.RunNotificationJobAsync());
            Assert.Single(discord.MessagesIn(FakeDiscord.AnnouncementChannelId));
        }

        // Two posts Discord didn't answer: Discord isn't answering, so the run stops posting, and the rest wait. The next run
        // finds neither in the channel, and sends them and the rest.
        [Fact]
        public async Task TwoUnansweredSendsStopTheRun()
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);
            var welcome = discord.MessagesIn(FakeDiscord.JoinedChannelId).Count;
            AddNewItems(discord);
            discord.ChannelFailures[FakeDiscord.ChannelId] = (503, 0);

            var detail = await discord.RunNotificationJobAsync();

            Assert.EndsWith("; Discord didn't answer 2 posts, so this run stopped posting", detail);
            Assert.Equal(2, discord.Store.Deliveries.Count(row => row.state == DeliveryState.InDoubt));
            Assert.DoesNotContain(discord.Store.Deliveries, row => row.state == DeliveryState.InDoubt && row.guild_id != FakeDiscord.GuildId);
            Assert.Single(discord.Store.Queue, row => row.guild_id == FakeDiscord.GuildId && row.state == QueueState.Pending);

            discord.ChannelFailures.Clear();
            await discord.RunNotificationJobAsync();

            Assert.Equal(3, discord.MessagesIn(FakeDiscord.ChannelId).Count);
            Assert.Equal(welcome + 3, discord.MessagesIn(FakeDiscord.JoinedChannelId).Count);
            Assert.All(discord.Store.Queue, row => Assert.Equal(QueueState.Sent, row.state));
        }

        // At most 50 messages a run: the rest wait, and having the oldest rows they go first at the next run, before what
        // was found with them
        [Fact]
        public async Task RunSendsAtMostFiftyMessagesAndTheOldestGoFirstNext()
        {
            await using var discord = await StartWithBaselineAsync();

            // Names as long as an embed's title, so a message holds 15 of them: more than 60 messages' worth
            discord.Armor.AllArmor.AddRange(Enumerable.Range(NewId, 1000).Select(id => ArmorEmbedTests.Armor(id, new string('a', 252) + id)));
            discord.Steam.Lobbies.Add(Lobby(NewId));

            var detail = await discord.RunNotificationJobAsync();

            Assert.Equal(PostingLimits.MessagesPerRun, Sends(discord).Count);
            Assert.EndsWith("; this run sent its limit of 50 messages", detail);
            Assert.All(Sends(discord), step => Assert.Equal([ArmorTitle], step.EmbedTitles));
            Assert.Equal(QueueState.Pending, Queue(discord, FakeDiscord.GuildId, PostSource.Lobbies).Single().state);

            await discord.RunNotificationJobAsync();

            var next = Sends(discord).Skip(PostingLimits.MessagesPerRun).ToList();
            Assert.True(next.Count > 2);
            Assert.All(next.SkipLast(1), step => Assert.Equal([ArmorTitle], step.EmbedTitles));
            Assert.Equal([LobbyTitle], next[^1].EmbedTitles);
            Assert.All(discord.Store.Queue, row => Assert.Equal(QueueState.Sent, row.state));
            Assert.Equal(1000, Sends(discord).Where(step => step.EmbedTitles.Contains(ArmorTitle)).SelectMany(step => ArmorEmbedTests.NamesIn(step.EmbedDescriptions.Single()))
                .Distinct().Count());
        }

        // No message is claimed once the run has gone 200 s, so it ends well inside JobHandler's 270 s
        [Fact]
        public async Task RunClaimsNoMessageAfter200Seconds()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            var hold = new TaskCompletionSource();
            discord.SendHold = hold.Task;

            var run = discord.RunNotificationJobAsync();
            await WaitUntilAsync(() => discord.StepNames.Contains(SendStep), "The run never reached its send");

            discord.MoveClock(PostingLimits.ClaimTime);
            hold.SetResult();

            Assert.Equal("1 armor: posted in 1 of 1 server; 2 items wait for 1 server; this run reached its limit of 200 seconds",
                await run.WaitAsync(WaitLimit, TestContext.Current.CancellationToken));
            Assert.Single(Sends(discord));

            Assert.Equal("1 map: posted in 1 of 1 server; 1 lobby: posted in 1 of 1 server", await discord.RunNotificationJobAsync());
        }

        // The wait is in MinerBot's database, so a restart doesn't end it
        [Fact]
        public async Task RestSurvivesARestart()
        {
            await using var before = await StartWithBaselineAsync();
            before.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            before.ChannelFailures[FakeDiscord.ChannelId] = (403, 50013);
            await before.RunNotificationJobAsync();
            await before.RunNotificationJobAsync();

            // The second refusal in a row: 15 minutes
            await using var after = await before.RestartAsync(TimeSpan.FromMinutes(1));
            await after.RunNotificationJobAsync();

            Assert.Empty(Sends(after));

            after.MoveClock(TimeSpan.FromMinutes(4));
            await after.RunNotificationJobAsync();

            Assert.Single(Sends(after));
            Assert.Equal(QueueState.Sent, Assert.Single(after.Store.Queue).state);
        }

        // waiting_on is kept in MinerBot's database, so every process works it out the same: a SHA-256 of the channel, its type,
        // the server's switches and the bot's View Channel, Send Messages, Embed Links and Read Message History there, never a
        // hash seeded per process, which a restart would end every wait by. Read Message History taken away changes it too.
        [Fact]
        public async Task WaitingOnIsTheSameInEveryProcessAndHoldsReadMessageHistory()
        {
            await using var discord = await StartWithBaselineAsync();
            var guild = discord.Client.GetGuild(FakeDiscord.GuildId);

            Assert.Equal(14046316884606712176UL, Backoff.WaitingOn(guild, FakeDiscord.ChannelId, 3));

            // The general channel without the overwrite that gives the bot Read Message History there, as before otherwise
            await discord.DeliverAsync("CHANNEL_UPDATE", FakeDiscord.ChannelEventJson(FakeDiscord.GuildId, FakeDiscord.ChannelId, 0,
                FakeDiscord.DenyJson(FakeDiscord.DeniedPinnerRoleId, ChannelPermission.PinMessages), name: "general"));

            Assert.Equal(13203706441572023613UL, Backoff.WaitingOn(guild, FakeDiscord.ChannelId, 3));
        }

        static async Task WaitUntilAsync(Func<bool> condition, string because)
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();

            while (!condition())
            {
                if (waited.Elapsed > WaitLimit)
                    Assert.Fail(because);

                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }
    }
}
