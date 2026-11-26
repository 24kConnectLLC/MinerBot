using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using DataAccessLibrary.Status;
using Discord;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MinerBot.Shared.Delivery;
using MinerBot_2._0.Models;
using MinerBot_2._0.Posting;
using MinerBot_2._0.Services;
using MinerBot_2._0.Status;
using static MinerBot.Tests.Posts;

namespace MinerBot.Tests
{
    // Sends whose fate the bot can't know at once: a claim whose answer didn't come, a send Discord didn't answer, a settle
    // MinerBot's database didn't take, a process that stopped between the send and the record, two processes on one
    // MinerBot's database, and the host's stop. A message is sent only once its claim has committed, a claim takes only rows
    // still pending, and a message whose fate isn't known is looked for in its channel's messages, so none is posted twice:
    // where the bot can't look, it's counted as sent, and may be missed, never doubled.
    [Collection("Discord")]
    public class PostingInDoubtTests
    {
        const string ReadStep = "get messages";

        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

        // The end of a run's detail when MinerBot's database didn't take a claim, a settle, a refusal or a drop
        const string DatabaseStop = "MinerBot's database didn't take a post's record (TimeoutException), so this run stopped posting";

        // MinerBot's database didn't confirm the claim, which may have landed: nothing is sent, and the run sends nothing more
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ClaimWithAnUnknownOutcomeSendsNothing(bool landed)
        {
            var store = new FailingNotificationData(new());
            await using var discord = await FakeDiscord.StartAsync(store: store);
            await discord.RunNotificationJobAsync();
            AddNewItems(discord);
            store.FailNext(nameof(FailingNotificationData.ClaimAsync), new TimeoutException("The database didn't answer within 15 seconds"), lands: landed);

            // Nothing of the armor's post went, so the run's detail doesn't count it as posted, or as tried there, but as waiting,
            // and says why the run stopped
            Assert.Equal($"3 items wait for 1 server; {DatabaseStop}", await discord.RunNotificationJobAsync());

            Assert.DoesNotContain(SendStep, discord.StepNames);
            Assert.Single(store.Calls, call => call == nameof(FailingNotificationData.ClaimAsync));
            Assert.Contains($"MinerBot's database didn't take the armor notification for guild {FakeDiscord.GuildId} before its send (TimeoutException), so this run sends nothing more",
                discord.ConsoleText);
            Assert.Equal(new[] { landed ? QueueState.Sending : QueueState.Pending, QueueState.Pending, QueueState.Pending }, discord.Store.Queue.Select(row => row.state));

            // Nothing was sent for it, so the next run gives back what it may have taken, and posts everything once
            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, NewMapTitle, LobbyTitle], SentTitles(discord));
            Assert.All(discord.Store.Queue, row => Assert.Equal(QueueState.Sent, row.state));
            var notSent = discord.Store.Deliveries.Where(row => row.state != DeliveryState.Sent).Select(row => (row.state, row.reason)).ToList();

            if (landed)
                Assert.Equal((DeliveryState.Failed, (string?)DeliveryReasons.NotSent), Assert.Single(notSent));
            else
                Assert.Empty(notSent);
            Assert.DoesNotContain(ReadStep, discord.StepNames);
            Assert.Equal(0, discord.Pending.Count);
        }

        // A drop or a refusal from the bot's copy that MinerBot's database didn't take stops the run's posting as a claim does,
        // and the run's detail says why, while the armor still waits for the next run
        [Theory]
        [InlineData(nameof(FailingNotificationData.DropAsync))]
        [InlineData(nameof(FailingNotificationData.RecordRefusalAsync))]
        public async Task WriteTheDatabaseDidNotTakeStopsTheRunSayingWhy(string method)
        {
            var store = new FailingNotificationData(new());
            await using var discord = await FakeDiscord.StartAsync(store: store);
            await discord.RunNotificationJobAsync();

            // An armor that waits for the server, which is then switched off for armor, or set to a channel where the bot can't
            // send, so the next run drops it there, or refuses it from the copy
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            await discord.MakeGuildUnavailableAsync();
            await discord.RunNotificationJobAsync();
            await discord.MakeGuildAvailableAsync();
            var drop = method == nameof(FailingNotificationData.DropAsync);

            if (drop)
                discord.Store.SetSwitches(Switches(FakeDiscord.GuildId, armor: false));
            else
                discord.Guilds.AllGuilds = [FakeGuildData.CreateGuild(FakeDiscord.NoSendChannelId)];

            store.FailNext(method, new TimeoutException("The database didn't answer within 15 seconds"));

            Assert.Equal(drop ? $"nothing new; 1 item still waits for 1 server; {DatabaseStop}" : $"1 armor: posted in 0 of 1 server, 1 failed; 1 item waits for 1 server; {DatabaseStop}",
                await discord.RunNotificationJobAsync());

            Assert.Equal(QueueState.Pending, Assert.Single(discord.Store.Queue).state);
            Assert.Empty(discord.Store.Deliveries);
        }

        // Two processes on one database at once, as the bot and a second one by mistake: while the first holds its claimed
        // message in its send, the second finds nothing of it pending, and every item goes once. The second finds the first's
        // message in the channel, which Discord made before its slow answer, and records it as sent.
        [Fact]
        public async Task TwoProcessesNeverClaimTheSameRow()
        {
            await using var first = await StartWithBaselineAsync();
            first.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));

            var hold = new TaskCompletionSource();
            first.SendHold = hold.Task;
            var firstRun = first.RunNotificationJobAsync();

            await WaitUntilAsync(() => first.StepNames.Contains(SendStep), "The first process never reached its send");

            await using var second = await first.RestartAsync(TimeSpan.Zero);
            await second.RunNotificationJobAsync();

            hold.SetResult();
            await firstRun.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.Single(Sends(first));
            Assert.Empty(Sends(second));
            Assert.Equal(QueueState.Sent, Assert.Single(first.Store.Queue).state);
            Assert.Equal(DeliveryState.Sent, Assert.Single(first.Store.Deliveries).state);
        }

        // The bot stops between Discord making its post and MinerBot's database taking it, and what it kept in memory goes
        // with it: the next process finds the post in the channel's messages, by the bot, held by no other row and of the
        // kind (armor's title, the lobbies' title, the link of a map it carries), not the bot's test message just before it,
        // and records it as sent with that ID. It's sent once in all.
        [Theory]
        [InlineData(PostSource.Armor, "armor")]
        [InlineData(PostSource.Maps, "map")]
        [InlineData(PostSource.Lobbies, "lobby")]
        public async Task DeathBetweenSendAndRecordIsSettledFromTheHistory(string source, string kind)
        {
            var store = new FailingNotificationData(new());
            await using var before = await FakeDiscord.StartAsync(store: store);
            await before.RunNotificationJobAsync();

            if (source == PostSource.Armor)
                before.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            else if (source == PostSource.Maps)
                before.Steam.LatestItems.Insert(0, FakeGetData.CreateItem(NewId));
            else
                before.Steam.Lobbies.Add(Lobby(NewId));

            var test = before.AddMessage(FakeDiscord.ChannelId, before.Clock.UtcNow.AddMinutes(5).AddSeconds(-2), FakeDiscord.BotId,
                embeds: $"[{FakeDiscord.EmbedJson("MinerBot test message")}]");
            store.FailAlways(nameof(FailingNotificationData.SettleAsync), new TimeoutException("The database didn't answer within 15 seconds"));

            await before.RunNotificationJobAsync();

            var made = Assert.Single(before.MessagesIn(FakeDiscord.ChannelId), id => id != test);
            Assert.Equal(DeliveryState.Sending, Assert.Single(store.Inner.Deliveries).state);
            Assert.Equal(1, before.Pending.Count);

            store.Heal();
            await using var after = await before.RestartAsync(TimeSpan.FromMinutes(1));

            Assert.Equal(PostingTexts.NothingNew, await after.RunNotificationJobAsync());

            Assert.Single(after.Steps, step => step.Name == ReadStep);
            Assert.Empty(Sends(after));
            Assert.Single(Sends(before));

            var row = Assert.Single(store.Inner.Deliveries);
            Assert.Equal((DeliveryState.Sent, (ulong?)made, (string?)null), (row.state, row.message_id, row.reason));
            Assert.Equal(QueueState.Sent, Assert.Single(store.Inner.Queue).state);
            Assert.Equal(made, Status(after, FakeDiscord.GuildId, source)!.last_message_id);
            Assert.Contains($"Found the {kind} notification to channel {FakeDiscord.ChannelId} of guild {FakeDiscord.GuildId} in the channel's messages, so it went",
                after.ConsoleText);
        }

        // Another process's message still sending is looked for once it's a minute old, so one Discord made late has arrived,
        // and a second process's message still in flight isn't taken for one that wasn't made
        [Fact]
        public async Task AnotherProcesssPostIsLookedForOnceItsAMinuteOld()
        {
            var store = new FailingNotificationData(new());
            await using var before = await FakeDiscord.StartAsync(store: store);
            await before.RunNotificationJobAsync();
            before.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            store.FailAlways(nameof(FailingNotificationData.SettleAsync), new TimeoutException("The database didn't answer within 15 seconds"));
            await before.RunNotificationJobAsync();

            store.Heal();
            await using var after = await before.RestartAsync(TimeSpan.Zero);
            await after.RunNotificationJobAsync(TimeSpan.FromSeconds(30));

            Assert.DoesNotContain(ReadStep, after.StepNames);
            Assert.Equal(DeliveryState.Sending, Assert.Single(store.Inner.Deliveries).state);

            await after.RunNotificationJobAsync(TimeSpan.FromSeconds(30));

            Assert.Single(after.Steps, step => step.Name == ReadStep);
            Assert.Equal(DeliveryState.Sent, Assert.Single(store.Inner.Deliveries).state);
            Assert.Empty(Sends(after));
        }

        // A post of two messages whose second Discord made and MinerBot's database didn't take: the look takes the message no
        // other row holds, so the first message, which its own row holds, is never taken for it
        [Fact]
        public async Task MessageAnotherRowHoldsIsNotTakenForTheOneInDoubt()
        {
            var store = new FailingNotificationData(new());
            await using var before = await FakeDiscord.StartAsync(store: store);
            await before.RunNotificationJobAsync();
            before.Armor.AllArmor.AddRange(Enumerable.Range(NewId, 200).Select(id => ArmorEmbedTests.Armor(id, $"Long armor style name {id}")));
            store.FailNext(nameof(FailingNotificationData.SettleAsync), new TimeoutException("The database didn't answer within 15 seconds"), after: 1);

            await before.RunNotificationJobAsync();

            var made = before.MessagesIn(FakeDiscord.ChannelId);
            Assert.Equal(2, made.Count);
            Assert.Equal([DeliveryState.Sent, DeliveryState.Sending], store.Inner.Deliveries.Select(row => row.state));

            await using var after = await before.RestartAsync(TimeSpan.FromMinutes(1));
            await after.RunNotificationJobAsync();

            Assert.Equal(made, store.Inner.Deliveries.Select(row => row.message_id!.Value));
            Assert.Empty(Sends(after));
        }

        // A post of two messages to one channel whose first went seconds after it was tried, as a send that waits out a rate
        // limit or a slow Discord does, so Discord made it a second before the second message was tried, and whose second
        // Discord neither made nor answered: the first message, which its own row holds, though that row was tried before the
        // look's time, isn't taken for the second, whose armor is sent again
        [Theory]
        [InlineData(4)]
        [InlineData(9)]
        [InlineData(14)]
        public async Task EarlierPartMadeLateIsNotTakenForTheOneInDoubt(int firstTriedSecondsBefore)
        {
            await using var discord = await StartWithBaselineAsync();
            var store = discord.Store;
            var second = discord.Clock.UtcNow.UtcDateTime;
            var first = second.AddSeconds(-firstTriedSecondsBefore);
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId + 1));

            await store.SaveDetectionAsync(new Detection
            {
                At = first,
                Seen = [.. new[] { NewId, NewId + 1 }.Select(id => new SeenRow { source = PostSource.Armor, item_id = (ulong)id,
                    state = SeenState.Queued, first_seen_at = first, last_seen_at = first })],
                Queue = [.. new[] { NewId, NewId + 1 }.Select(id => new QueueItem { guild_id = FakeDiscord.GuildId, source = PostSource.Armor,
                    item_id = (ulong)id, is_new = true, queued_at = first })]
            });

            var open = await store.ListOpenAsync();

            DeliveryClaim Claim(ulong key, byte part, DateTime at, int itemId) => new()
            {
                AttemptKey = key, RunId = discord.Recorder.RunId, RunStartedAt = first, GuildId = FakeDiscord.GuildId, ChannelId = FakeDiscord.ChannelId,
                Source = PostSource.Armor, Part = part, AttemptedAt = at, QueueIds = [open.Single(row => row.item_id == (ulong)itemId).id]
            };

            // The first, tried then, made a second before the second was tried, and recorded as sent with its ID
            await store.ClaimAsync(Claim(1, 1, first, NewId));
            var made = discord.AddMessage(FakeDiscord.ChannelId, new DateTimeOffset(second.AddSeconds(-1)), FakeDiscord.BotId, embeds: $"[{FakeDiscord.EmbedJson(ArmorTitle)}]");
            await store.SettleAsync(new Settlement { AttemptKey = 1, Outcome = DeliveryState.Sent, At = second.AddSeconds(-1), MessageId = made });

            // The second, which Discord didn't make or answer within the send's 15 seconds
            await store.ClaimAsync(Claim(2, 2, second, NewId + 1));
            await store.SettleAsync(new Settlement { AttemptKey = 2, Outcome = DeliveryState.InDoubt, At = second.AddSeconds(15),
                Reason = DeliveryReasons.NoAnswer });

            await discord.RunNotificationJobAsync();

            var row = store.Deliveries.Single(delivery => delivery.attempt_key == 2);
            Assert.Equal((DeliveryState.Failed, (string?)DeliveryReasons.NotSent, (ulong?)null), (row.state, row.reason, row.message_id));
            Assert.Equal([ArmorTitle], SentTitles(discord));
            Assert.All(store.Queue.Where(queued => queued.item_id is NewId or NewId + 1), queued => Assert.Equal(QueueState.Sent, queued.state));
            Assert.Equal(1, store.Deliveries.Count(delivery => delivery.message_id == made));
        }

        // A post of two messages whose first was made 7 s after its attempt, its claim slow, and whose second Discord
        // didn't make, answering 503: the first message lies in the second's window, though its own row was tried
        // before that window, and that row still holds it, so the look finds the second wasn't made, and it goes again
        [Fact]
        public async Task MessageOfAnEarlierSlowPartIsNotTakenForTheOneInDoubt()
        {
            var store = new FailingNotificationData(new());
            await using var discord = await FakeDiscord.StartAsync(store: store);
            await discord.RunNotificationJobAsync();
            discord.Armor.AllArmor.AddRange(Enumerable.Range(NewId, 200).Select(id => ArmorEmbedTests.Armor(id, $"Long armor style name {id}")));

            store.AfterClaim = claim =>
            {
                if (claim.Part == 1)
                    discord.MoveClock(TimeSpan.FromSeconds(7));
                else
                    discord.ChannelFailures[FakeDiscord.ChannelId] = (503, 0);

                return Task.CompletedTask;
            };

            Assert.Equal("200 armor: posted in 0 of 1 server, 1 not answered", await discord.RunNotificationJobAsync());

            store.AfterClaim = null;
            discord.ChannelFailures.Clear();
            var first = Assert.Single(discord.MessagesIn(FakeDiscord.ChannelId));
            var tried = store.Inner.Deliveries.Select(row => row.attempted_at).ToList();
            Assert.Equal(TimeSpan.FromSeconds(7), tried[1] - tried[0]);

            await discord.RunNotificationJobAsync();

            var made = discord.MessagesIn(FakeDiscord.ChannelId);
            Assert.Equal(2, made.Count);
            Assert.Equal([(DeliveryState.Sent, (string?)null, (ulong?)first), (DeliveryState.Failed, DeliveryReasons.NotSent, null), (DeliveryState.Sent, null, made[1])],
                store.Inner.Deliveries.Select(row => (row.state, row.reason, row.message_id)));
            Assert.Equal(3, Sends(discord).Count);
            Assert.Contains($"Long armor style name {NewId + 199}", Sends(discord)[2].EmbedDescriptions.Single());
            Assert.All(store.Inner.Queue, row => Assert.Equal(QueueState.Sent, row.state));
        }

        // The bot's answer to /maps latest, just after a map post Discord didn't answer and didn't make, shows the same map: a
        // slash command's answer is another type of message than a post, so the look doesn't take it, and the post goes again
        [Fact]
        public async Task SlashCommandsAnswerIsNotTakenForThePost()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Steam.LatestItems.Insert(0, FakeGetData.CreateItem(NewId));
            discord.ChannelFailures[FakeDiscord.ChannelId] = (503, 0);
            await discord.RunNotificationJobAsync();

            discord.ChannelFailures.Clear();
            var link = discord.Services.GetRequiredService<SteamAPIService>().WorkshopItemDetailsURI + NewId;
            discord.AddMessage(FakeDiscord.ChannelId, new DateTimeOffset(discord.Store.Deliveries[0].attempted_at).AddSeconds(10), FakeDiscord.BotId, type: 20,
                embeds: $"[{FakeDiscord.EmbedJson(NewMapTitle, link)}]");

            await discord.RunNotificationJobAsync();

            Assert.Equal(2, Sends(discord).Count);
            Assert.Equal([(DeliveryState.Failed, (string?)DeliveryReasons.NotSent), (DeliveryState.Sent, null)], discord.Store.Deliveries.Select(row => (row.state, row.reason)));
        }

        // Nor does it take another user's message with the post's embed, as when someone shares the map's link just as the
        // bot posts it, or the bot's own post of the kind made more than 2 minutes after the attempt, which can't be this one:
        // the post goes again
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MessageNotThePostsIsNotTakenForIt(bool botsOwnLater)
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Steam.LatestItems.Insert(0, FakeGetData.CreateItem(NewId));
            discord.ChannelFailures[FakeDiscord.ChannelId] = (503, 0);
            await discord.RunNotificationJobAsync();

            discord.ChannelFailures.Clear();
            var attempted = new DateTimeOffset(discord.Store.Deliveries[0].attempted_at);
            var link = discord.Services.GetRequiredService<SteamAPIService>().WorkshopItemDetailsURI + NewId;
            discord.AddMessage(FakeDiscord.ChannelId, botsOwnLater ? attempted + PostingLimits.HistoryAfter + TimeSpan.FromSeconds(1) : attempted.AddSeconds(10),
                botsOwnLater ? FakeDiscord.BotId : discord.OtherUserId, embeds: $"[{FakeDiscord.EmbedJson(NewMapTitle, link)}]");

            await discord.RunNotificationJobAsync();

            Assert.Equal(2, Sends(discord).Count);
            Assert.Equal([(DeliveryState.Failed, (string?)DeliveryReasons.NotSent), (DeliveryState.Sent, null)], discord.Store.Deliveries.Select(row => (row.state, row.reason)));
        }

        // Discord refusing the read of the channel's messages is a look that couldn't tell, with Discord's numbers in the log
        // and never its words
        [Fact]
        public async Task ReadDiscordRefusesIsALookThatCouldntTell()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Unanswered[FakeDiscord.ChannelId] = true;
            await discord.RunNotificationJobAsync();

            discord.Unanswered.Clear();
            discord.HistoryFailures[FakeDiscord.ChannelId] = (403, 50001);
            await discord.RunNotificationJobAsync();

            Assert.Single(discord.Steps, step => step.Name == ReadStep);
            Assert.Single(Sends(discord));

            var doubted = Assert.Single(discord.Store.Deliveries);
            Assert.Equal((DeliveryState.InDoubt, (byte)1), (doubted.state, doubted.checks));
            Assert.Contains($"Couldn't check whether the armor notification to channel {FakeDiscord.ChannelId} of guild {FakeDiscord.GuildId} arrived, because Discord refused "
                + "the read of the channel's messages (HTTP 403, Discord 50001) (look 1 of 3)", discord.ConsoleText);
            Assert.DoesNotContain(FakeDiscord.DiscordText, discord.ConsoleText);
        }

        // A read Discord doesn't answer is Discord's trouble, not the bot unable to look: no look is counted, and the other
        // messages wait for the next run, so a Discord outage doesn't use up their looks. An hour after the attempt the post
        // counts as sent all the same, once Discord is seen to answer, so it's this channel alone: here Discord answered none
        // of the posts' requests since, so the hour's run reads the other server's message too, which Discord answers, and the
        // post counts as sent at the next run.
        [Fact]
        public async Task ReadDiscordDoesntAnswerIsNoLookUntilAnHourAfterTheAttempt()
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Unanswered[FakeDiscord.ChannelId] = true;
            discord.Unanswered[FakeDiscord.JoinedChannelId] = true;
            await discord.RunNotificationJobAsync();

            discord.Unanswered.Clear();
            discord.HistoryFailures[FakeDiscord.ChannelId] = (0, 0);

            for (var run = 0; run < 3; run++)
                await discord.RunNotificationJobAsync();

            Assert.Equal(3, discord.Steps.Count(step => step.Name == ReadStep));
            Assert.All(discord.Store.Deliveries, row => Assert.Equal((DeliveryState.InDoubt, (byte)0), (row.state, row.checks)));
            Assert.Contains($"Discord didn't answer the read of channel {FakeDiscord.ChannelId}'s messages in guild {FakeDiscord.GuildId} (TaskCanceledException), so the "
                + "other checks wait for the next run", discord.ConsoleText);

            // 55 minutes after the attempt it still waits
            discord.MoveClock(TimeSpan.FromMinutes(35));
            await discord.RunNotificationJobAsync();

            Assert.Equal(4, discord.Steps.Count(step => step.Name == ReadStep));
            Assert.All(discord.Store.Deliveries, row => Assert.Equal((DeliveryState.InDoubt, (byte)0), (row.state, row.checks)));

            discord.MoveClock(TimeSpan.FromMinutes(5));
            await discord.RunNotificationJobAsync();

            Assert.Equal([(FakeDiscord.GuildId, DeliveryState.InDoubt), (FakeDiscord.JoinedGuildId, DeliveryState.Sent)],
                discord.Store.Deliveries.Select(row => (row.guild_id, row.state)));

            await discord.RunNotificationJobAsync();

            Assert.Equal([(FakeDiscord.GuildId, DeliveryState.Unconfirmed), (FakeDiscord.JoinedGuildId, DeliveryState.Sent)],
                discord.Store.Deliveries.Select(row => (row.guild_id, row.state)));
            Assert.Single(discord.Recorder.PeekEvents(1000), row => row.kind == BotEvents.PostsUnconfirmed);
            Assert.Equal(2, Sends(discord).Count(step => step.EmbedTitles.Contains(ArmorTitle)));
        }

        // A channel whose reads never get an answer while Discord answers the bot's other posts, with the message or a refusal
        // of its own, is that channel alone, not an outage: its post counts as sent an hour after its attempt
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ChannelThatNeverAnswersWhileDiscordAnswersCountsAsSentAtItsHour(bool otherRefused)
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.ChannelFailures[FakeDiscord.ChannelId] = (503, 0);

            if (otherRefused)
                discord.ChannelFailures[FakeDiscord.JoinedChannelId] = (403, 50013);

            await discord.RunNotificationJobAsync();

            discord.ChannelFailures.TryRemove(FakeDiscord.ChannelId, out _);
            discord.HistoryFailures[FakeDiscord.ChannelId] = (503, 0);
            discord.MoveClock(TimeSpan.FromMinutes(50));
            await discord.RunNotificationJobAsync();

            Assert.Equal(DeliveryState.InDoubt, discord.Store.Deliveries[0].state);

            await discord.RunNotificationJobAsync();

            Assert.Equal((FakeDiscord.GuildId, DeliveryState.Unconfirmed), (discord.Store.Deliveries[0].guild_id, discord.Store.Deliveries[0].state));
            Assert.Equal(otherRefused ? DeliveryState.Failed : DeliveryState.Sent, discord.Store.Deliveries[1].state);
            Assert.Single(discord.Recorder.PeekEvents(1000), row => row.kind == BotEvents.PostsUnconfirmed);
        }

        // So does a read Discord refuses: past the hour, with no post answered since, a read with no answer looks at the next
        // message, whose read Discord refuses, so it's that channel alone, and its post counts as sent at the next run
        [Fact]
        public async Task ReadDiscordRefusesTellsAnOutageApartToo()
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Unanswered[FakeDiscord.ChannelId] = true;
            discord.Unanswered[FakeDiscord.JoinedChannelId] = true;
            await discord.RunNotificationJobAsync();

            discord.Unanswered.Clear();
            discord.HistoryFailures[FakeDiscord.ChannelId] = (0, 0);
            discord.HistoryFailures[FakeDiscord.JoinedChannelId] = (403, 50001);
            discord.MoveClock(TimeSpan.FromHours(1));
            await discord.RunNotificationJobAsync();

            Assert.Equal([(FakeDiscord.GuildId, DeliveryState.InDoubt), (FakeDiscord.JoinedGuildId, DeliveryState.Unconfirmed)],
                discord.Store.Deliveries.Select(row => (row.guild_id, row.state)));

            await discord.RunNotificationJobAsync();

            Assert.Equal([(FakeDiscord.GuildId, DeliveryState.Unconfirmed), (FakeDiscord.JoinedGuildId, DeliveryState.Unconfirmed)],
                discord.Store.Deliveries.Select(row => (row.guild_id, row.state)));
        }

        // But a channel that alone never answers, whose post was the bot's last, can't be told from an outage: Discord answered
        // the bot's posts before its attempt, which says nothing of it, and nothing since, as when nothing new comes for a
        // while. Past its hour the post waits, its channel read at each run, until Discord answers a post the bot makes after
        // it, here a lobby's in the other server, and counts as sent at the run after.
        [Fact]
        public async Task ChannelThatNeverAnswersAfterTheBotsLastPostWaitsForDiscordToAnswerAnother()
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);
            discord.Gate.RequestLimit = TimeSpan.FromMilliseconds(300);
            discord.Store.SetSwitches(Switches(FakeDiscord.GuildId, lobbies: false));
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.ChannelFailures[FakeDiscord.ChannelId] = (403, 50013);

            Assert.Equal("1 armor: posted in 1 of 2 servers, 1 failed; 1 item waits for 1 server", await discord.RunNotificationJobAsync());

            // The refused server's next try is its run's only post, and Discord answers neither it nor the reads of its channel
            discord.ChannelFailures.Clear();
            discord.Hangs[FakeDiscord.ChannelId] = true;
            discord.HistoryHangs[FakeDiscord.ChannelId] = true;

            Assert.Equal("1 armor: posted in 0 of 1 server, 1 not answered", await discord.RunNotificationJobAsync());

            var waiting = Assert.Single(discord.Store.Deliveries, row => row.state == DeliveryState.InDoubt);

            // An hour after the attempt, and two runs more, with nothing new to post
            discord.MoveClock(TimeSpan.FromMinutes(55));
            var reads = discord.Steps.Count(step => step.Name == ReadStep);

            for (var run = 0; run < 3; run++)
                Assert.Equal(PostingTexts.NothingNew, await discord.RunNotificationJobAsync());

            Assert.Equal(reads + 3, discord.Steps.Count(step => step.Name == ReadStep));
            Assert.Equal(DeliveryState.InDoubt, discord.Store.Deliveries.Single(row => row.attempt_key == waiting.attempt_key).state);

            // A lobby for the other server only: Discord's answer to its post is the first to a request made since the attempt
            discord.Steam.Lobbies.Add(Lobby(NewId));

            Assert.Equal("1 lobby: posted in 1 of 1 server", await discord.RunNotificationJobAsync());
            Assert.Equal(DeliveryState.InDoubt, discord.Store.Deliveries.Single(row => row.attempt_key == waiting.attempt_key).state);

            await discord.RunNotificationJobAsync();

            Assert.Equal(DeliveryState.Unconfirmed, discord.Store.Deliveries.Single(row => row.attempt_key == waiting.attempt_key).state);
            Assert.Single(discord.Recorder.PeekEvents(1000), row => row.kind == BotEvents.PostsUnconfirmed);
        }

        // An outage longer than an hour, whether Discord answers with its own errors or the bot's connection to it hangs: the
        // post tried in it is held in doubt, and while Discord answers none of the posts' requests, the look that gets no
        // answer keeps it waiting past its hour. The first look Discord answers finds it wasn't made, and it's sent once.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OutageLongerThanAnHourEndsWithThePostSentOnce(bool hangs)
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Gate.RequestLimit = TimeSpan.FromMilliseconds(300);

            if (hangs)
            {
                discord.Hangs[FakeDiscord.ChannelId] = true;
                discord.HistoryHangs[FakeDiscord.ChannelId] = true;
            }
            else
            {
                discord.ChannelFailures[FakeDiscord.ChannelId] = (503, 0);
                discord.HistoryFailures[FakeDiscord.ChannelId] = (503, 0);
            }

            Assert.Equal("1 armor: posted in 0 of 1 server, 1 not answered", await discord.RunNotificationJobAsync());

            // 70 minutes of it
            for (var run = 0; run < 14; run++)
                Assert.Equal(PostingTexts.NothingNew, await discord.RunNotificationJobAsync());

            var waiting = Assert.Single(discord.Store.Deliveries);
            Assert.Equal((DeliveryState.InDoubt, (byte)0), (waiting.state, waiting.checks));
            Assert.Contains($"Discord didn't answer the read of channel {FakeDiscord.ChannelId}'s messages in guild {FakeDiscord.GuildId} "
                + $"({(hangs ? "TaskCanceledException" : "HTTP 503")}), nor, before this run's look, any of the posts' requests since the armor notification was tried, so it waits for Discord to answer",
                discord.ConsoleText);

            discord.Hangs.Clear();
            discord.HistoryHangs.Clear();
            discord.ChannelFailures.Clear();
            discord.HistoryFailures.Clear();
            discord.Gate.RequestLimit = PostingLimits.RequestLimit;

            Assert.Equal("1 armor: posted in 1 of 1 server", await discord.RunNotificationJobAsync());

            Assert.Single(discord.MessagesIn(FakeDiscord.ChannelId));
            Assert.Equal([(DeliveryState.Failed, (string?)DeliveryReasons.NotSent), (DeliveryState.Sent, null)], discord.Store.Deliveries.Select(row => (row.state, row.reason)));
            Assert.DoesNotContain(discord.Recorder.PeekEvents(1000), row => row.kind == BotEvents.PostsUnconfirmed);
        }

        // A restart in the outage: the new process knows of no answer from Discord yet, so past the hour a read with no answer
        // still keeps the post waiting, and once Discord answers, the look sends it once
        [Fact]
        public async Task OutageThroughARestartStillWaitsForDiscord()
        {
            await using var before = await StartWithBaselineAsync();
            before.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            before.ChannelFailures[FakeDiscord.ChannelId] = (503, 0);
            await before.RunNotificationJobAsync();

            await using var after = await before.RestartAsync(TimeSpan.FromMinutes(70));
            after.ChannelFailures[FakeDiscord.ChannelId] = (503, 0);
            after.HistoryFailures[FakeDiscord.ChannelId] = (503, 0);

            Assert.Equal(PostingTexts.NothingNew, await after.RunNotificationJobAsync());
            Assert.Equal(PostingTexts.NothingNew, await after.RunNotificationJobAsync());
            Assert.Equal(DeliveryState.InDoubt, Assert.Single(after.Store.Deliveries).state);

            after.ChannelFailures.Clear();
            after.HistoryFailures.Clear();

            Assert.Equal("1 armor: posted in 1 of 1 server", await after.RunNotificationJobAsync());
            Assert.Single(after.MessagesIn(FakeDiscord.ChannelId));
            Assert.DoesNotContain(after.Recorder.PeekEvents(1000), row => row.kind == BotEvents.PostsUnconfirmed);
        }

        // Past its kind's time limit, when it's no news any more, a post the outage kept waiting counts as sent: armor after 7
        // days, and a lobby at the hour itself, as a lobby is no news after half an hour
        [Theory]
        [InlineData(PostSource.Armor)]
        [InlineData(PostSource.Lobbies)]
        public async Task OutageWaitsNoLongerThanItsKindsTimeLimit(string source)
        {
            await using var discord = await StartWithBaselineAsync();

            if (source == PostSource.Armor)
                discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            else
                discord.Steam.Lobbies.Add(Lobby(NewId));

            discord.ChannelFailures[FakeDiscord.ChannelId] = (503, 0);
            discord.HistoryFailures[FakeDiscord.ChannelId] = (503, 0);
            await discord.RunNotificationJobAsync();

            discord.MoveClock(PostingLimits.OutageLookFor(source) - TimeSpan.FromMinutes(10));
            await discord.RunNotificationJobAsync();

            Assert.Equal(DeliveryState.InDoubt, Assert.Single(discord.Store.Deliveries).state);

            discord.MoveClock(TimeSpan.FromMinutes(5));
            await discord.RunNotificationJobAsync();

            Assert.Equal(DeliveryState.Unconfirmed, Assert.Single(discord.Store.Deliveries).state);
            Assert.Equal(source == PostSource.Armor ? TimeSpan.FromDays(7) : PostingLimits.LookFor, PostingLimits.OutageLookFor(source));
        }

        // In an outage, a run past the hour reads at most two messages' channels, the second only to tell an outage from one
        // channel, so the look spends at most two requests' limits a run
        [Fact]
        public async Task OutageLooksAtTwoMessagesARunAtMost()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            discord.ChannelFailures[FakeDiscord.ChannelId] = (503, 0);
            discord.HistoryFailures[FakeDiscord.ChannelId] = (503, 0);
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal(3, discord.Store.Deliveries.Count(row => row.state == DeliveryState.InDoubt));

            discord.MoveClock(TimeSpan.FromHours(1));
            var reads = discord.Steps.Count(step => step.Name == ReadStep);
            await discord.RunNotificationJobAsync();

            Assert.Equal(reads + 2, discord.Steps.Count(step => step.Name == ReadStep));
            Assert.All(discord.Store.Deliveries, row => Assert.Equal(DeliveryState.InDoubt, row.state));
        }

        // An outage of over an hour ends, and in the first run back Discord doesn't answer one of the two servers' reads, as
        // a recovery can bring a stray error: the look goes by what Discord had answered when it began, so that server's post
        // waits as in the outage, whether its read came before or after the one Discord answered, and the next run's look,
        // which Discord answers, sends it once. Neither post is lost.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ReadThatMissesOnceAsAnOutageEndsWaitsForTheNextRun(bool firstServersRead)
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);
            var joinedBefore = discord.MessagesIn(FakeDiscord.JoinedChannelId).Count;
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));

            foreach (var channel in new[] { FakeDiscord.ChannelId, FakeDiscord.JoinedChannelId })
            {
                discord.ChannelFailures[channel] = (503, 0);
                discord.HistoryFailures[channel] = (503, 0);
            }

            Assert.Equal("1 armor: posted in 0 of 2 servers, 2 not answered; Discord didn't answer 2 posts, so this run stopped posting",
                await discord.RunNotificationJobAsync());

            // 70 minutes of it
            for (var run = 0; run < 14; run++)
                Assert.Equal(PostingTexts.NothingNew, await discord.RunNotificationJobAsync());

            Assert.All(discord.Store.Deliveries, row => Assert.Equal(DeliveryState.InDoubt, row.state));

            // Back, but one read gets one more 503: the first server's, which the look makes first, or the second's
            var (missedChannel, missedGuild) = firstServersRead ? (FakeDiscord.ChannelId, FakeDiscord.GuildId) : (FakeDiscord.JoinedChannelId, FakeDiscord.JoinedGuildId);
            discord.ChannelFailures.Clear();
            discord.HistoryFailures.Clear();
            discord.HistoryFailures[missedChannel] = (503, 0);

            Assert.Equal("1 armor: posted in 1 of 1 server", await discord.RunNotificationJobAsync());
            Assert.Equal(DeliveryState.InDoubt, discord.Store.Deliveries.Single(row => row.guild_id == missedGuild).state);

            discord.HistoryFailures.Clear();

            Assert.Equal("1 armor: posted in 1 of 1 server", await discord.RunNotificationJobAsync());
            Assert.Single(discord.MessagesIn(FakeDiscord.ChannelId));
            Assert.Equal(joinedBefore + 1, discord.MessagesIn(FakeDiscord.JoinedChannelId).Count);
            Assert.DoesNotContain(discord.Store.Deliveries, row => DeliveryState.IsOpen(row.state) || row.state == DeliveryState.Unconfirmed);
            Assert.DoesNotContain(discord.Recorder.PeekEvents(1000), row => row.kind == BotEvents.PostsUnconfirmed);
        }

        // Past the hour, the look spends at most two requests' limits a run on reads with no answer, whether their messages
        // wait for Discord or count as sent: here Discord answered the other server's posts after the three messages of a
        // channel that never answers were tried, as when that server's part of Discord is still down, so their reads count
        // them as sent, two in one run and the third at the next
        [Fact]
        public async Task LookSpendsAtMostTwoReadsWithNoAnswerARunPastTheHour()
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);
            AddNewItems(discord);
            discord.ChannelFailures[FakeDiscord.ChannelId] = (503, 0);
            discord.HistoryFailures[FakeDiscord.ChannelId] = (0, 0);
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal([PostSource.Armor, PostSource.Maps, PostSource.Lobbies],
                discord.Store.Deliveries.Where(row => row.guild_id == FakeDiscord.GuildId && row.state == DeliveryState.InDoubt).Select(row => row.source));
            Assert.Equal(3, discord.Store.Deliveries.Count(row => row.guild_id == FakeDiscord.JoinedGuildId && row.state == DeliveryState.Sent));

            // An hour after the last of them was tried
            discord.MoveClock(TimeSpan.FromMinutes(55));
            var reads = discord.Steps.Count(step => step.Name == ReadStep);
            await discord.RunNotificationJobAsync();

            Assert.Equal(reads + 2, discord.Steps.Count(step => step.Name == ReadStep));
            Assert.Equal([DeliveryState.Unconfirmed, DeliveryState.Unconfirmed, DeliveryState.InDoubt],
                discord.Store.Deliveries.Where(row => row.guild_id == FakeDiscord.GuildId).Select(row => row.state));

            await discord.RunNotificationJobAsync();

            Assert.Equal(reads + 3, discord.Steps.Count(step => step.Name == ReadStep));
            Assert.All(discord.Store.Deliveries.Where(row => row.guild_id == FakeDiscord.GuildId), row => Assert.Equal(DeliveryState.Unconfirmed, row.state));
            Assert.Equal(3, discord.Recorder.PeekEvents(1000).Count(row => row.kind == BotEvents.PostsUnconfirmed));
        }

        // MinerBot's database not answering the look's reads ends the look for the run: the message stays open, holding its
        // kind, the run posts the rest, and the next run looks again
        [Fact]
        public async Task LookTheDatabaseDoesntAnswerWaitsForTheNextRun()
        {
            var store = new FailingNotificationData(new());
            await using var discord = await FakeDiscord.StartAsync(store: store);
            await discord.RunNotificationJobAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Unanswered[FakeDiscord.ChannelId] = true;
            await discord.RunNotificationJobAsync();

            discord.Unanswered.Clear();
            discord.Steam.Lobbies.Add(Lobby(NewId));
            store.FailNext(nameof(FailingNotificationData.ListMessageIdsAsync), new TimeoutException("The database didn't answer within 15 seconds"));

            Assert.Equal("1 lobby: posted in 1 of 1 server", await discord.RunNotificationJobAsync());

            Assert.Equal(DeliveryState.InDoubt, store.Inner.Deliveries[0].state);
            Assert.DoesNotContain(ReadStep, discord.StepNames);
            Assert.Contains($"MinerBot's database didn't answer the check of the armor notification to channel {FakeDiscord.ChannelId} of guild {FakeDiscord.GuildId} "
                + "(TimeoutException), so the next run checks again", discord.ConsoleText);

            await discord.RunNotificationJobAsync();

            Assert.Equal(DeliveryState.Sent, store.Inner.Deliveries[0].state);
            Assert.Single(Sends(discord), step => step.EmbedTitles.Contains(ArmorTitle));
        }

        // A server in an outage can't be looked at: its message waits, counting no look, however long, and is looked for once
        // the server is back
        [Fact]
        public async Task PostOfAServerInAnOutageWaitsForItToComeBack()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Unanswered[FakeDiscord.ChannelId] = true;
            await discord.RunNotificationJobAsync();

            discord.Unanswered.Clear();
            await discord.MakeGuildUnavailableAsync();

            for (var run = 0; run < 4; run++)
                await discord.RunNotificationJobAsync();

            Assert.DoesNotContain(ReadStep, discord.StepNames);

            var waiting = Assert.Single(discord.Store.Deliveries);
            Assert.Equal((DeliveryState.InDoubt, (byte)0), (waiting.state, waiting.checks));

            await discord.MakeGuildAvailableAsync();
            await discord.RunNotificationJobAsync();

            var found = Assert.Single(discord.Store.Deliveries);
            Assert.Equal((DeliveryState.Sent, (ulong?)Assert.Single(discord.MessagesIn(FakeDiscord.ChannelId))), (found.state, found.message_id));
            Assert.Single(Sends(discord));
        }

        // Discord's own trouble, and the post wasn't made: the next run doesn't find it in the channel's messages, so it's sent
        // again, once, and the first try is recorded as not sent, which counts against no server
        [Fact]
        public async Task MessageMissingFromTheHistoryIsSentOnce()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.ChannelFailures[FakeDiscord.ChannelId] = (500, 0);
            await discord.RunNotificationJobAsync();

            discord.ChannelFailures.Clear();

            Assert.Equal("1 armor: posted in 1 of 1 server", await discord.RunNotificationJobAsync());
            Assert.Equal(PostingTexts.NothingNew, await discord.RunNotificationJobAsync());

            Assert.Equal(2, Sends(discord).Count);
            Assert.Single(discord.MessagesIn(FakeDiscord.ChannelId));
            Assert.Single(discord.Steps, step => step.Name == ReadStep);
            Assert.Equal([(DeliveryState.Failed, (string?)DeliveryReasons.NotSent), (DeliveryState.Sent, null)], discord.Store.Deliveries.Select(row => (row.state, row.reason)));
            Assert.Equal((0u, (DateTime?)null), (Status(discord, FakeDiscord.GuildId, PostSource.Armor)!.failures, Status(discord, FakeDiscord.GuildId, PostSource.Armor)!.failing_since));
            Assert.Contains($"The armor notification to channel {FakeDiscord.ChannelId} of guild {FakeDiscord.GuildId} isn't in the channel's messages, so this run sends it again",
                discord.ConsoleText);
        }

        // A map post Discord neither made nor answered, and the map updated in its cooldown before the look finds the post
        // missing: the post that goes again shows the map as it is then, update and all, and records that update with it, so
        // when the cooldown is over only the server that got the first version is sent the update
        [Fact]
        public async Task UpdateShownInAPostSentAgainIsNotPostedThereAgain()
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);
            discord.ChannelFailures[FakeDiscord.ChannelId] = (503, 0);

            var map = NewMap(NewId);
            discord.Steam.LatestItems.Insert(0, map);
            await discord.RunNotificationJobAsync();

            discord.ChannelFailures.Clear();
            map.TimeUpdated = map.TimeUpdated.AddMinutes(30);

            Assert.Equal("1 map: posted in 1 of 1 server", await discord.RunNotificationJobAsync());

            discord.MoveClock(PostingLimits.Cooldown);
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            var update = $"<t:{Detector.Revision(map)}:f>";
            var first = Sends(discord).Where(step => step.SentTo == FakeDiscord.ChannelId && step.EmbedTitles.Contains(NewMapTitle)).ToList();
            var second = Sends(discord).Where(step => step.SentTo == FakeDiscord.JoinedChannelId && step.EmbedTitles.Contains(NewMapTitle)).ToList();

            Assert.Equal([NewMapHeading, NewMapHeading], first.Select(step => step.Content));
            Assert.Equal(update, UpdateShown(first[^1]));
            Assert.Equal([NewMapHeading, UpdatedMapHeading], second.Select(step => step.Content));
            Assert.Equal(update, UpdateShown(second[^1]));
            Assert.All(Queue(discord, FakeDiscord.GuildId, PostSource.Maps), row => Assert.Equal(QueueState.Sent, row.state));
        }

        // A busy channel: the look reads a page of 100 messages at a time from 5 s before the attempt, and reads on from the
        // page's newest while a full page ends before 2 minutes past the attempt, at most three pages. The post found on the
        // second page went; none, once the read is past those 2 minutes or at the channel's end, wasn't made and is sent
        // again; three full pages still short of them are a look that couldn't tell.
        [Theory]
        [InlineData("found on the second page", true, 150, 0, 2)]
        [InlineData("the channel's end on the second page", false, 150, 0, 2)]
        [InlineData("past the 2 minutes on the second page", false, 100, 100, 2)]
        [InlineData("too busy to read that far", false, 350, 0, 3)]
        public async Task BusyChannelIsReadOnUntilTwoMinutesPastTheAttempt(string why, bool made, int within, int later, int reads)
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            var attempt = discord.Clock.UtcNow + TimeSpan.FromMinutes(5);

            // Others' messages in the seconds before the post, when it's made, so it's behind them all; or after the
            // attempt, and later ones past the 2 minutes
            discord.AddOthersMessages(FakeDiscord.ChannelId, Enumerable.Range(0, within)
                .Select(i => made ? attempt.AddSeconds(-4).AddMilliseconds(10 * i) : attempt.AddSeconds(1).AddMilliseconds(100 * i)));
            discord.AddOthersMessages(FakeDiscord.ChannelId, Enumerable.Range(0, later).Select(i => attempt.AddMinutes(2).AddSeconds(1 + i)));

            if (made)
                discord.Unanswered[FakeDiscord.ChannelId] = true;
            else
                discord.ChannelFailures[FakeDiscord.ChannelId] = (503, 0);

            await discord.RunNotificationJobAsync();
            discord.Unanswered.Clear();
            discord.ChannelFailures.Clear();

            await discord.RunNotificationJobAsync();

            Assert.True(reads == discord.Steps.Count(step => step.Name == ReadStep), why);

            var first = discord.Store.Deliveries[0];

            if (made)
            {
                Assert.Single(Sends(discord));
                Assert.Equal((DeliveryState.Sent, (ulong?)discord.MessagesIn(FakeDiscord.ChannelId)[^1]), (first.state, first.message_id));
            }
            else if (reads == 2)
            {
                Assert.Equal(2, Sends(discord).Count);
                Assert.Equal((DeliveryState.Failed, DeliveryReasons.NotSent), (first.state, first.reason!));
            }
            else
            {
                Assert.Single(Sends(discord));
                Assert.Equal((DeliveryState.InDoubt, (byte)1), (first.state, first.checks));
                Assert.Contains($"Couldn't check whether the armor notification to channel {FakeDiscord.ChannelId} of guild {FakeDiscord.GuildId} arrived, because the channel is too busy "
                    + "to read 2 minutes past the post in 3 pages (look 1 of 3)", discord.ConsoleText);
            }
        }

        // Without Read Message History the bot can't look: each run's look couldn't tell, and at the third the post counts as
        // sent, so it's never sent again, and the posts.unconfirmed event says it may not have arrived. Its items aren't
        // posted twice; they may be missed.
        [Fact]
        public async Task WithoutReadMessageHistoryItIsUnconfirmedAndNeverSentAgain()
        {
            await using var discord = await StartWithBaselineAsync();

            // A channel where the bot's role gives it View Channel, Send Messages and Embed Links, and nothing gives it Read
            // Message History
            discord.Guilds.AllGuilds = [FakeGuildData.CreateGuild(FakeDiscord.AnnouncementChannelId)];
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Unanswered[FakeDiscord.AnnouncementChannelId] = true;
            await discord.RunNotificationJobAsync();

            var attempted = Assert.Single(discord.Store.Deliveries).attempted_at;
            discord.Unanswered.Clear();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId + 1));

            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            var doubted = Assert.Single(discord.Store.Deliveries);
            Assert.Equal((DeliveryState.InDoubt, (byte)2), (doubted.state, doubted.checks));
            Assert.Single(Sends(discord));

            await discord.RunNotificationJobAsync();

            Assert.DoesNotContain(ReadStep, discord.StepNames);

            var unconfirmed = discord.Store.Deliveries[0];
            Assert.Equal((DeliveryState.Unconfirmed, (string?)DeliveryReasons.Unconfirmed, (ulong?)null), (unconfirmed.state, unconfirmed.reason, unconfirmed.message_id));
            Assert.Equal(QueueState.Sent, Queue(discord, FakeDiscord.GuildId, PostSource.Armor).Single(row => row.item_id == NewId).state);

            // The armor that waited behind it goes now; the unconfirmed one never again
            var sends = Sends(discord);
            Assert.Equal(2, sends.Count);
            Assert.Contains("Armor 101", sends[1].EmbedDescriptions.Single());
            Assert.DoesNotContain("Armor 100", sends[1].EmbedDescriptions.Single());

            Assert.Equal([$"an armor post to server {FakeDiscord.GuildId}, channel {FakeDiscord.AnnouncementChannelId}, at {attempted:yyyy-MM-dd HH:mm:ss} UTC may not have arrived: "
                + "the bot didn't learn whether it went, and couldn't check that channel's messages"],
                discord.Recorder.PeekEvents(1000).Where(row => row.kind == BotEvents.PostsUnconfirmed).Select(row => row.detail));
            Assert.Equal(EventLevel.Warning, BotEvents.LevelOf(BotEvents.PostsUnconfirmed));
            Assert.Contains($"Couldn't check whether the armor notification to channel {FakeDiscord.AnnouncementChannelId} of guild {FakeDiscord.GuildId} arrived, because the bot "
                + "is missing Read Message History there (look 1 of 3)", discord.ConsoleText);

            await discord.RunNotificationJobAsync();
            Assert.Equal(2, Sends(discord).Count);
        }

        // Discord made the post and its answer never came, which HttpClient ends as a TaskCanceledException: it's sent once,
        // held in doubt as not answered, and the next run finds it in the channel
        [Fact]
        public async Task TimedOutSendIsNeverResentByTheLibrary()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Unanswered[FakeDiscord.ChannelId] = true;

            Assert.Equal("1 armor: posted in 0 of 1 server, 1 not answered", await discord.RunNotificationJobAsync());

            Assert.Single(Sends(discord));
            Assert.Equal(RetryMode.RetryRatelimit, Deliverer.SendOptions(CancellationToken.None).RetryMode);

            // Every request also ends by itself within 15 seconds, on its own token (PostingGate.Limit), so one under way at a
            // stop has ended before the drain's wait for it is over. Discord.Net's own Timeout, the same 15 s, bounds only its
            // wait for a rate limit's window.
            Assert.Equal(TimeSpan.FromSeconds(15), PostingLimits.RequestLimit);
            Assert.Equal(PostingLimits.RequestLimit, discord.Gate.RequestLimit);
            Assert.Equal(15000, Deliverer.SendOptions(CancellationToken.None).Timeout);
            Assert.True(PostingLimits.RequestLimit < new PostingDrain(discord.Gate, new FakeLifetime(), new RecordingLogger<PostingDrain>()).Limit);

            var doubted = Assert.Single(discord.Store.Deliveries);
            Assert.Equal((DeliveryState.InDoubt, (string?)DeliveryReasons.NoAnswer), (doubted.state, doubted.reason));

            discord.Unanswered.Clear();
            await discord.RunNotificationJobAsync();

            Assert.Single(Sends(discord));

            var found = Assert.Single(discord.Store.Deliveries);
            Assert.Equal((DeliveryState.Sent, (ulong?)Assert.Single(discord.MessagesIn(FakeDiscord.ChannelId))), (found.state, found.message_id));
        }

        // Discord made the post and the gateway in front of it answered 502: Discord.Net's default retry mode would send it
        // again, making a second post, and the posts' mode never does, so it's held in doubt, and the next run finds it
        [Fact]
        public async Task BadGatewayIsNeverSentAgainByTheLibrary()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.BadGateways[FakeDiscord.ChannelId] = 1;

            Assert.Equal("1 armor: posted in 0 of 1 server, 1 not answered", await discord.RunNotificationJobAsync());

            Assert.Single(Sends(discord));
            var made = Assert.Single(discord.MessagesIn(FakeDiscord.ChannelId));
            var doubted = Assert.Single(discord.Store.Deliveries);
            Assert.Equal((DeliveryState.InDoubt, (string?)DeliveryReasons.NoAnswer, (ushort?)502), (doubted.state, doubted.reason, doubted.http_status));

            await discord.RunNotificationJobAsync();

            Assert.Single(Sends(discord));
            var found = Assert.Single(discord.Store.Deliveries);
            Assert.Equal((DeliveryState.Sent, (ulong?)made), (found.state, found.message_id));
        }

        // A send Discord never answers ends at its own limit, as Discord.Net's Timeout doesn't end it and HttpClient's
        // would only after 100 s: it's held in doubt as not answered, the run ends without a stop, and the next run
        // finds it wasn't made and sends it once
        [Fact]
        public async Task SendDiscordNeverAnswersEndsAtItsOwnLimit()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Hangs[FakeDiscord.ChannelId] = true;
            discord.Gate.RequestLimit = TimeSpan.FromMilliseconds(300);

            Assert.Equal("1 armor: posted in 0 of 1 server, 1 not answered", await discord.RunNotificationJobAsync());

            Assert.True(discord.Gate.IsOpen);
            Assert.Empty(discord.MessagesIn(FakeDiscord.ChannelId));
            var doubted = Assert.Single(discord.Store.Deliveries);
            Assert.Equal((DeliveryState.InDoubt, (string?)DeliveryReasons.NoAnswer), (doubted.state, doubted.reason));

            discord.Hangs.Clear();
            discord.Gate.RequestLimit = PostingLimits.RequestLimit;
            await discord.RunNotificationJobAsync();

            Assert.Equal(2, Sends(discord).Count);
            var sent = Assert.Single(discord.MessagesIn(FakeDiscord.ChannelId));
            Assert.Equal([(DeliveryState.Failed, (ulong?)null), (DeliveryState.Sent, sent)], discord.Store.Deliveries.Select(row => (row.state, row.message_id)));
        }

        // So does a read of a channel's messages Discord never answers: no look is counted, the run posts the rest, and the
        // next run looks again
        [Fact]
        public async Task ReadDiscordNeverAnswersEndsAtItsOwnLimit()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Unanswered[FakeDiscord.ChannelId] = true;
            await discord.RunNotificationJobAsync();

            discord.Unanswered.Clear();
            discord.Gate.RequestLimit = TimeSpan.FromMilliseconds(300);
            discord.HistoryHangs[FakeDiscord.ChannelId] = true;
            discord.Steam.Lobbies.Add(Lobby(NewId));

            Assert.Equal("1 lobby: posted in 1 of 1 server", await discord.RunNotificationJobAsync());

            Assert.Equal((DeliveryState.InDoubt, (byte)0), (discord.Store.Deliveries[0].state, discord.Store.Deliveries[0].checks));
            Assert.Contains($"Discord didn't answer the read of channel {FakeDiscord.ChannelId}'s messages in guild {FakeDiscord.GuildId} (TaskCanceledException)",
                discord.ConsoleText);

            discord.HistoryHangs.Clear();
            await discord.RunNotificationJobAsync();

            Assert.Equal(DeliveryState.Sent, discord.Store.Deliveries[0].state);
            Assert.Single(Sends(discord), step => step.EmbedTitles.Contains(ArmorTitle));
        }

        // A post Discord makes and never answers, with the real wait: it ends at its own limit, held in doubt as one Discord
        // didn't answer; the look's read of a channel that doesn't answer ends at its limit too and counts no look; and once
        // Discord answers, the look finds the message, which isn't sent again
        [Fact]
        public async Task PostDiscordMadeButNeverAnsweredEndsAtItsLimitAndIsFound()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Gate.RequestLimit = TimeSpan.FromMilliseconds(300);
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Silent[FakeDiscord.ChannelId] = true;

            Assert.Equal("1 armor: posted in 0 of 1 server, 1 not answered", await discord.RunNotificationJobAsync());

            var made = Assert.Single(discord.MessagesIn(FakeDiscord.ChannelId));
            var doubted = Assert.Single(discord.Store.Deliveries);
            Assert.Equal((DeliveryState.InDoubt, (string?)DeliveryReasons.NoAnswer), (doubted.state, doubted.reason));

            await discord.RunNotificationJobAsync();

            Assert.Single(discord.Steps, step => step.Name == ReadStep);
            Assert.Equal((DeliveryState.InDoubt, (byte)0), (discord.Store.Deliveries[0].state, discord.Store.Deliveries[0].checks));
            Assert.Contains($"Discord didn't answer the read of channel {FakeDiscord.ChannelId}'s messages in guild {FakeDiscord.GuildId} (TaskCanceledException), so the other "
                + "checks wait for the next run", discord.ConsoleText);

            discord.Silent.Clear();
            await discord.RunNotificationJobAsync();

            Assert.Single(Sends(discord));
            var found = Assert.Single(discord.Store.Deliveries);
            Assert.Equal((DeliveryState.Sent, (ulong?)made), (found.state, found.message_id));
        }

        // The host's stop that comes between the claim and the send: Discord.Net throws a TimeoutException before its first
        // try, so nothing was made, and the message's rows wait again, to be sent at the next start with no look in the channel
        [Fact]
        public async Task StopBeforeTheSendLeavesItsRowsWaiting()
        {
            var store = new FailingNotificationData(new());
            await using var before = await FakeDiscord.StartAsync(store: store);
            await before.RunNotificationJobAsync();
            before.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            var drain = new PostingDrain(before.Gate, new FakeLifetime(), new RecordingLogger<PostingDrain>()) { Limit = TimeSpan.FromMilliseconds(50) };
            store.AfterClaim = _ => drain.StopAsync(CancellationToken.None);

            Assert.Equal("1 armor: posted in 0 of 1 server, 1 failed; 1 item waits for 1 server; the bot is stopping, so this run stopped posting",
                await before.RunNotificationJobAsync());

            store.AfterClaim = null;
            Assert.Empty(Sends(before));
            Assert.Empty(before.MessagesIn(FakeDiscord.ChannelId));
            var notSent = Assert.Single(store.Inner.Deliveries);
            Assert.Equal((DeliveryState.Failed, (string?)DeliveryReasons.NotSent), (notSent.state, notSent.reason));
            Assert.Equal(QueueState.Pending, Assert.Single(store.Inner.Queue).state);
            Assert.Null(Status(before, FakeDiscord.GuildId, PostSource.Armor));

            await using var after = await before.RestartAsync(TimeSpan.FromMinutes(1));

            Assert.Equal("1 armor: posted in 1 of 1 server", await after.RunNotificationJobAsync());

            Assert.DoesNotContain(ReadStep, after.StepNames);
            Assert.Single(after.MessagesIn(FakeDiscord.ChannelId));
            Assert.Equal(QueueState.Sent, Assert.Single(store.Inner.Queue).state);
        }

        // A settle MinerBot's database didn't take waits in memory, and the next run writes it before it reads anything, with
        // no look in the channel; until it can, the run fails saying so, and posts wait
        [Fact]
        public async Task SettleTheDatabaseDidNotTakeIsWrittenAtTheNextRun()
        {
            var store = new FailingNotificationData(new());
            await using var discord = await FakeDiscord.StartAsync(store: store);
            await discord.RunNotificationJobAsync();
            AddNewItems(discord);
            store.FailNext(nameof(FailingNotificationData.SettleAsync), new TimeoutException("The database didn't answer within 15 seconds"));

            Assert.Equal($"1 armor: posted in 1 of 1 server; 2 items wait for 1 server; {DatabaseStop}", await discord.RunNotificationJobAsync());

            Assert.Equal([ArmorTitle], SentTitles(discord));
            Assert.Equal(DeliveryState.Sending, Assert.Single(store.Inner.Deliveries).state);
            Assert.Equal(1, discord.Pending.Count);
            Assert.Contains($"MinerBot's database didn't take how the armor notification to channel {FakeDiscord.ChannelId} of guild {FakeDiscord.GuildId} ended (TimeoutException), "
                + "so this run sends nothing more", discord.ConsoleText);

            store.FailNext(nameof(FailingNotificationData.SettleAsync), new TimeoutException("The database didn't answer within 15 seconds"));
            var failed = await Assert.ThrowsAsync<JobFailedException>(discord.RunNotificationJobAsync);

            Assert.Equal("posts wait: MinerBot's database didn't take what an earlier run sent (TimeoutException)", failed.Message);
            Assert.Equal(1, discord.Pending.Count);

            Assert.Equal("1 map: posted in 1 of 1 server; 1 lobby: posted in 1 of 1 server", await discord.RunNotificationJobAsync());

            Assert.Equal(0, discord.Pending.Count);
            Assert.Equal([ArmorTitle, NewMapTitle, LobbyTitle], SentTitles(discord));
            Assert.DoesNotContain(ReadStep, discord.StepNames);
            var armor = store.Inner.Deliveries[0];
            Assert.Equal((DeliveryState.Sent, (ulong?)discord.MessagesIn(FakeDiscord.ChannelId)[0]), (armor.state, armor.message_id));
            Assert.All(store.Inner.Queue, row => Assert.Equal(QueueState.Sent, row.state));
        }

        // A message this process claimed and never settled, which only a fault leaves, since a settle MinerBot's database
        // didn't take waits in memory: it holds its server's kind while it may still be under way, and once it's 5 minutes
        // old the look in the channel settles it, so the kind isn't held until a restart
        [Fact]
        public async Task StaleSendingRowOfThisProcessIsChecked()
        {
            var store = new FailingNotificationData(new());
            await using var discord = await FakeDiscord.StartAsync(store: store);
            await discord.RunNotificationJobAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            store.LoseNextSettle();
            await discord.RunNotificationJobAsync();

            var lost = Assert.Single(store.Inner.Deliveries);
            Assert.Equal((DeliveryState.Sending, discord.Recorder.RunId), (lost.state, lost.run_id));
            Assert.Equal(0, discord.Pending.Count);

            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId + 1));
            await discord.RunNotificationJobAsync(TimeSpan.FromMinutes(2));

            Assert.DoesNotContain(ReadStep, discord.StepNames);
            Assert.Single(Sends(discord));

            await discord.RunNotificationJobAsync(TimeSpan.FromMinutes(3));

            Assert.Single(discord.Steps, step => step.Name == ReadStep);
            var checkedRow = store.Inner.Deliveries[0];
            Assert.Equal((DeliveryState.Sent, (ulong?)discord.MessagesIn(FakeDiscord.ChannelId)[0]), (checkedRow.state, checkedRow.message_id));
            Assert.Equal(2, Sends(discord).Count);
            Assert.Contains("Armor 101", Sends(discord)[1].EmbedDescriptions.Single());
        }

        // The host's stop: no message is claimed from its first sign, and PostingDrain waits for the one in flight to settle,
        // so it's recorded as what Discord answered
        [Fact]
        public async Task StopWaitsForTheMessageInFlightAndClaimsNoMore()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            var drain = new PostingDrain(discord.Gate, new FakeLifetime(), new RecordingLogger<PostingDrain>()) { Limit = WaitLimit };
            await drain.StartAsync(TestContext.Current.CancellationToken);

            var hold = new TaskCompletionSource();
            discord.SendHold = hold.Task;
            var run = discord.RunNotificationJobAsync();
            await WaitUntilAsync(() => discord.StepNames.Contains(SendStep), "The run never reached its send");

            var stopping = drain.StopAsync(CancellationToken.None);

            Assert.False(discord.Gate.IsOpen);
            Assert.False(stopping.IsCompleted);

            hold.SetResult();
            await stopping.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.Equal("1 armor: posted in 1 of 1 server; 2 items wait for 1 server; the bot is stopping, so this run stopped posting",
                await run.WaitAsync(WaitLimit, TestContext.Current.CancellationToken));
            Assert.Single(Sends(discord));

            var settled = Assert.Single(discord.Store.Deliveries);
            Assert.Equal((DeliveryState.Sent, (ulong?)Assert.Single(discord.MessagesIn(FakeDiscord.ChannelId))), (settled.state, settled.message_id));
            Assert.Equal(2, discord.Store.Queue.Count(row => row.state == QueueState.Pending));
        }

        // A post still under way when the drain's limit comes is abandoned, held in doubt for the next start to look for,
        // and the drain says so. Its own limit here outlasts the run's wait, so only the stop's abandon can end it, as for a
        // send that began late in the drain's wait.
        [Fact]
        public async Task StopAbandonsAPostStillUnderWayAtItsLimit()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            var logger = new RecordingLogger<PostingDrain>();
            var drain = new PostingDrain(discord.Gate, new FakeLifetime(), logger) { Limit = TimeSpan.FromMilliseconds(200) };

            discord.Gate.RequestLimit = TimeSpan.FromMinutes(5);
            discord.Hangs[FakeDiscord.ChannelId] = true;
            var run = discord.RunNotificationJobAsync();
            await WaitUntilAsync(() => discord.StepNames.Contains(SendStep), "The run never reached its send");

            await drain.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.Equal("1 armor: posted in 0 of 1 server, 1 not answered; the bot is stopping, so this run stopped posting",
                await run.WaitAsync(WaitLimit, TestContext.Current.CancellationToken));
            Assert.Empty(discord.MessagesIn(FakeDiscord.ChannelId));
            var abandoned = Assert.Single(discord.Store.Deliveries);
            Assert.Equal((DeliveryState.InDoubt, (string?)null), (abandoned.state, abandoned.reason));
            Assert.True(logger.Logged(LogLevel.Warning, "A post was still under way 0.2 s into the stop"));
        }

        // The host's stop during the look's read of a post tried over an hour ago, as at the first run after the bot was down,
        // while Discord answers the bot's other posts, so it isn't an outage: the stop ended the read, not Discord, so the post
        // counts no look and isn't counted as sent at its hour. It waits for the next start's look, which finds it wasn't made
        // and sends it once.
        [Fact]
        public async Task StopDuringALookLeavesItsPostForTheNextStartsLook()
        {
            await using var before = await StartWithBaselineAsync();
            await AddSecondServerAsync(before);
            before.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            before.ChannelFailures[FakeDiscord.ChannelId] = (503, 0);

            Assert.Equal("1 armor: posted in 1 of 2 servers, 1 not answered", await before.RunNotificationJobAsync());

            before.ChannelFailures.Clear();
            before.MoveClock(TimeSpan.FromMinutes(61));
            before.HistoryHangs[FakeDiscord.ChannelId] = true;
            var drain = new PostingDrain(before.Gate, new FakeLifetime(), new RecordingLogger<PostingDrain>()) { Limit = WaitLimit };
            await drain.StartAsync(TestContext.Current.CancellationToken);

            var run = before.RunNotificationJobAsync();
            await WaitUntilAsync(() => before.StepNames.Contains(ReadStep), "The run never reached its look");
            await drain.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.Equal(PostingTexts.NothingNew, await run.WaitAsync(WaitLimit, TestContext.Current.CancellationToken));
            var waiting = before.Store.Deliveries.Single(row => row.guild_id == FakeDiscord.GuildId);
            Assert.Equal((DeliveryState.InDoubt, (byte)0), (waiting.state, waiting.checks));
            Assert.DoesNotContain(before.Recorder.PeekEvents(1000), row => row.kind == BotEvents.PostsUnconfirmed);
            Assert.Empty(before.MessagesIn(FakeDiscord.ChannelId));

            await using var after = await before.RestartAsync(TimeSpan.FromMinutes(1));

            Assert.Equal("1 armor: posted in 1 of 1 server", await after.RunNotificationJobAsync());
            Assert.Single(after.MessagesIn(FakeDiscord.ChannelId));
            Assert.Equal([(DeliveryState.Failed, (string?)DeliveryReasons.NotSent), (DeliveryState.Sent, null)],
                after.Store.Deliveries.Where(row => row.guild_id == FakeDiscord.GuildId).Select(row => (row.state, row.reason)));
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
