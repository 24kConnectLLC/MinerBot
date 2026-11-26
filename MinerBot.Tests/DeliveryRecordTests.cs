using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Posting;
using Discord;
using MinerBot.Shared.Delivery;
using MinerBot_2._0.Posting;
using static MinerBot.Tests.Posts;

namespace MinerBot.Tests
{
    // What the notification run records of each message: a row for every message it sent or tried and every refusal its
    // cache saw first, with Discord's message ID or its numbers, never its text, each server's status by kind, and the run's
    // detail
    [Collection("Discord")]
    public class DeliveryRecordTests
    {
        [Fact]
        public async Task EverySentMessageHasARowWithItsMessageId()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);

            await discord.RunNotificationJobAsync();

            var deliveries = discord.Store.Deliveries;
            var made = discord.MessagesIn(FakeDiscord.ChannelId);
            Assert.Equal([(PostSource.Armor, made[0]), (PostSource.Maps, made[1]), (PostSource.Lobbies, made[2])],
                deliveries.Select(row => (row.source, row.message_id!.Value)));
            Assert.All(deliveries, row => Assert.Equal((DeliveryState.Sent, FakeDiscord.GuildId, FakeDiscord.ChannelId, (byte)1, (ushort)1, discord.Recorder.RunId),
                (row.state, row.guild_id, row.channel_id, row.part, row.item_count, row.run_id)));
            Assert.All(discord.Store.Queue, row => Assert.Equal((QueueState.Sent, deliveries.Single(delivery => delivery.source == row.source).id), (row.state, row.delivery_id!.Value)));
            Assert.All(PostSource.Posted, source => Assert.Equal(deliveries.Single(row => row.source == source).message_id, Status(discord, FakeDiscord.GuildId, source)!.last_message_id));
        }

        // An armor post too long for one message is two, each with its own row, and each armor's row names the message that
        // listed it
        [Fact]
        public async Task SplitArmorPostHasOneRowPerMessageAndItsArmor()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Armor.AllArmor.AddRange(Enumerable.Range(NewId, 200).Select(id => ArmorEmbedTests.Armor(id, $"Long armor style name {id}")));

            await discord.RunNotificationJobAsync();

            var messages = Sends(discord);
            var deliveries = discord.Store.Deliveries;

            Assert.Equal(2, messages.Count);
            Assert.Equal([(byte)1, 2], deliveries.Select(row => row.part));

            for (var part = 0; part < 2; part++)
            {
                var named = ArmorEmbedTests.NamesIn(messages[part].EmbedDescriptions.Single());
                var carried = discord.Store.Queue.Where(row => row.delivery_id == deliveries[part].id).Select(row => $"Long armor style name {row.item_id}").ToList();

                Assert.Equal(named, carried);
                Assert.Equal(named.Count, deliveries[part].item_count);
            }

            Assert.Equal(200, discord.Store.Queue.Count(row => row.state == QueueState.Sent));
        }

        // Discord's refusal is recorded by its numbers: the HTTP status and Discord's own code, read as the reason they mean.
        // Its items wait for the next run, and the server's kind counts a failure. Discord's words are nowhere: not in a row,
        // the run's detail, an event or the log.
        [Fact]
        public async Task RefusalRecordsTheHttpStatusAndDiscordsCode()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            discord.ChannelFailures[FakeDiscord.ChannelId] = (403, 50013);

            var detail = await discord.RunNotificationJobAsync();

            Assert.Equal(3, Sends(discord).Count);
            Assert.All(discord.Store.Deliveries, row => Assert.Equal((DeliveryState.Failed, DeliveryReasons.DiscordPermissions, (ushort?)403, (uint?)50013, (ulong?)null),
                (row.state, row.reason, row.http_status, row.discord_code, row.message_id)));
            Assert.All(discord.Store.Queue, row => Assert.Equal((QueueState.Pending, (ulong?)null), (row.state, row.delivery_id)));
            Assert.All(discord.Store.Statuses, row => Assert.Equal((1u, DeliveryReasons.DiscordPermissions, (ushort?)403, (uint?)50013, (DateTime?)null),
                (row.failures, row.last_reason, row.last_http_status, row.last_discord_code, row.last_sent_at)));

            Assert.Equal("1 armor: posted in 0 of 1 server, 1 failed; 1 map: posted in 0 of 1 server, 1 failed; 1 lobby: posted in 0 of 1 server, 1 failed; 3 items wait for 1 server",
                detail);
            Assert.Contains($"Couldn't send the armor notification to channel {FakeDiscord.ChannelId} of guild {FakeDiscord.GuildId}: discord_permissions (HTTP 403, Discord 50013)",
                discord.ConsoleText);

            Assert.DoesNotContain(FakeDiscord.DiscordText, discord.ConsoleText);
            Assert.DoesNotContain(FakeDiscord.DiscordText, detail);
            Assert.DoesNotContain(discord.Recorder.PeekEvents(1000), row => row.detail?.Contains(FakeDiscord.DiscordText) == true);
            Assert.DoesNotContain(discord.Store.Deliveries, row => row.ToString().Contains(FakeDiscord.DiscordText));
            Assert.DoesNotContain(discord.Store.Statuses, row => row.ToString().Contains(FakeDiscord.DiscordText));

            // The next run tries again, and they go
            discord.ChannelFailures.Clear();
            await discord.RunNotificationJobAsync();

            Assert.All(discord.Store.Queue, row => Assert.Equal(QueueState.Sent, row.state));
        }

        // Discord's own trouble, a 5xx with its code 0, which says nothing: the message may have been made, so it's held in
        // doubt, by the HTTP status alone, and its items wait for the bot to find out, which it looks for once the message is
        // a minute old
        [Fact]
        public async Task DiscordsOwnErrorIsRecordedByItsStatus()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.ChannelFailures[FakeDiscord.ChannelId] = (500, 0);

            var detail = await discord.RunNotificationJobAsync();

            var row = Assert.Single(discord.Store.Deliveries);
            Assert.Equal((DeliveryState.InDoubt, DeliveryReasons.NoAnswer, (ushort?)500, (uint?)null), (row.state, row.reason, row.http_status, row.discord_code));
            Assert.Equal(QueueState.Sending, Assert.Single(discord.Store.Queue).state);
            Assert.Empty(discord.Store.Statuses);
            Assert.Equal("1 armor: posted in 0 of 1 server, 1 not answered", detail);
            Assert.Contains($"Discord didn't answer the armor notification to channel {FakeDiscord.ChannelId} of guild {FakeDiscord.GuildId} (HTTP 500), so it may or may not have arrived",
                discord.ConsoleText);

            // Its server's armor gets no new message while it's in doubt, as in a run asked for half a minute later
            discord.ChannelFailures.Clear();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId + 1));
            await discord.RunNotificationJobAsync(TimeSpan.FromSeconds(30));

            Assert.Single(Sends(discord));
        }

        // A permission the post needs, missing in the bot's copy of the server, is a refusal recorded with the mask of what's
        // missing, and costs Discord no request. Without Send Messages, Discord takes Embed Links away too, and so does the copy.
        [Theory]
        [InlineData(FakeDiscord.NoSendChannelId, DeliveryReasons.SendMessages | DeliveryReasons.EmbedLinks, "Send Messages and Embed Links")]
        [InlineData(FakeDiscord.NoEmbedChannelId, DeliveryReasons.EmbedLinks, "Embed Links")]
        [InlineData(FakeDiscord.HiddenChannelId, DeliveryReasons.ViewChannel | DeliveryReasons.SendMessages | DeliveryReasons.EmbedLinks, "View Channel, Send Messages and Embed Links")]
        public async Task MissingPermissionIsCaughtFromTheCacheWithoutARequest(ulong channelId, int mask, string names)
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Guilds.AllGuilds = [FakeGuildData.CreateGuild(channelId)];
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));

            Assert.Equal("1 armor: posted in 0 of 1 server, 1 failed; 1 item waits for 1 server", await discord.RunNotificationJobAsync());

            Assert.DoesNotContain(SendStep, discord.StepNames);

            var row = Assert.Single(discord.Store.Deliveries);
            Assert.Equal((DeliveryState.Failed, DeliveryReasons.MissingPermissions, (byte?)mask, (byte)0, (ushort)1, (ushort?)null),
                (row.state, row.reason, row.missing, row.part, row.item_count, row.http_status));
            Assert.Equal((1u, (byte?)mask), (Status(discord, FakeDiscord.GuildId, PostSource.Armor)!.failures, Status(discord, FakeDiscord.GuildId, PostSource.Armor)!.last_missing));
            Assert.Equal(QueueState.Pending, Assert.Single(discord.Store.Queue).state);
            Assert.Contains($"Couldn't send the armor notification to channel {channelId} of guild {FakeDiscord.GuildId}, because the bot is missing {names} there", discord.ConsoleText);
        }

        // Each server is owed each item: one whose send was refused gets it once it can, though another server got it already
        [Fact]
        public async Task ServerThatFailedGetsTheItemWhenItCanEvenIfAnotherGotIt()
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);
            AddNewItems(discord);
            discord.ChannelFailures[FakeDiscord.JoinedChannelId] = (403, 50001);

            await discord.RunNotificationJobAsync();

            var first = Sends(discord).Count;
            discord.ChannelFailures.Clear();
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            var resent = Sends(discord).Skip(first).ToList();
            Assert.Equal([FakeDiscord.JoinedChannelId, FakeDiscord.JoinedChannelId, FakeDiscord.JoinedChannelId], resent.Select(step => step.SentTo));
            Assert.Equal([ArmorTitle, NewMapTitle, LobbyTitle], resent.SelectMany(step => step.EmbedTitles));
            Assert.All(discord.Store.Queue, row => Assert.Equal(QueueState.Sent, row.state));
            Assert.Equal(DeliveryReasons.MissingAccess, discord.Store.Deliveries.First(row => row.state == DeliveryState.Failed).reason);
        }

        // The status keeps the last post that went, and from the first refusal on, when failing started and the tries in a
        // row, until a post goes again
        [Fact]
        public async Task StatusKeepsTheLastSuccessAndWhenFailingStarted()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            await discord.RunNotificationJobAsync();

            var posted = Status(discord, FakeDiscord.GuildId, PostSource.Armor)!;
            var message = Assert.Single(discord.MessagesIn(FakeDiscord.ChannelId));
            Assert.Equal((message, (DateTime?)null, 0u), (posted.last_message_id!.Value, posted.failing_since, posted.failures));

            discord.ChannelFailures[FakeDiscord.ChannelId] = (403, 50013);
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId + 1));
            await discord.RunNotificationJobAsync();
            var firstFailure = Status(discord, FakeDiscord.GuildId, PostSource.Armor)!.failing_since;
            await discord.RunNotificationJobAsync();

            var failing = Status(discord, FakeDiscord.GuildId, PostSource.Armor)!;
            Assert.Equal((posted.last_sent_at, message, firstFailure, 2u), (failing.last_sent_at, failing.last_message_id!.Value, failing.failing_since, failing.failures));
            Assert.NotNull(firstFailure);
            Assert.True(failing.last_failed_at > firstFailure);

            // After its wait of 15 minutes, which the second failure in a row brings
            discord.ChannelFailures.Clear();
            discord.MoveClock(TimeSpan.FromMinutes(10));
            await discord.RunNotificationJobAsync();

            var again = Status(discord, FakeDiscord.GuildId, PostSource.Armor)!;
            Assert.Equal(((DateTime?)null, 0u, DeliveryReasons.DiscordPermissions), (again.failing_since, again.failures, again.last_reason));
            Assert.True(again.last_sent_at > failing.last_failed_at);
        }

        // A kind the server turned off stops failing, so no page shows it as failing while nothing is posted for it, and its
        // last failure stays as history
        [Fact]
        public async Task KindTurnedOffClearsItsFailingStatus()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.ChannelFailures[FakeDiscord.ChannelId] = (403, 50013);
            await discord.RunNotificationJobAsync();

            Assert.Equal(1u, Status(discord, FakeDiscord.GuildId, PostSource.Armor)!.failures);

            discord.Guilds.AllGuilds[0].armor_channel_id = Guild.ChannelOff;
            await discord.RunNotificationJobAsync();

            var cleared = Status(discord, FakeDiscord.GuildId, PostSource.Armor)!;
            Assert.Equal(((DateTime?)null, 0u, (DateTime?)null, (ulong?)null), (cleared.failing_since, cleared.failures, cleared.next_try_at, cleared.waiting_on));
            Assert.Equal((DeliveryReasons.DiscordPermissions, (uint?)50013), (cleared.last_reason, cleared.last_discord_code));
            Assert.Equal((QueueState.Dropped, DeliveryReasons.ChannelOff), Queue(discord, FakeDiscord.GuildId, PostSource.Armor).Select(row => (row.state, row.reason)).Single());
        }

        // A voice channel's chat takes posts, as before, and the record shows whether they arrive; a forum takes none, and is a
        // refusal from the cache
        [Fact]
        public async Task VoiceChannelChatGetsPostsAndForumIsNoChannel()
        {
            const ulong Voice = 3007, Forum = 3008;

            await using var discord = await StartWithBaselineAsync();
            await discord.DeliverAsync("CHANNEL_CREATE", FakeDiscord.ChannelEventJson(FakeDiscord.GuildId, Voice, 5, type: (int)ChannelType.Voice));
            await discord.DeliverAsync("CHANNEL_CREATE", FakeDiscord.ChannelEventJson(FakeDiscord.GuildId, Forum, 6, type: (int)ChannelType.Forum));
            discord.Guilds.AllGuilds = [new Guild { guild_id = FakeDiscord.GuildId, armor_channel_id = Voice, lobby_channel_id = Forum }];
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Steam.Lobbies.Add(Lobby(NewId));

            await discord.RunNotificationJobAsync();

            Assert.Equal(Voice, Assert.Single(Sends(discord)).SentTo);
            Assert.Equal((DeliveryState.Sent, Voice), discord.Store.Deliveries.Where(row => row.source == PostSource.Armor).Select(row => (row.state, row.channel_id)).Single());
            Assert.Equal((DeliveryState.Failed, DeliveryReasons.NoChannel, Forum),
                discord.Store.Deliveries.Where(row => row.source == PostSource.Lobbies).Select(row => (row.state, row.reason!, row.channel_id)).Single());
        }

        // A server in an outage isn't failing: its items wait, with no row, and the other servers get theirs
        [Fact]
        public async Task OutageOfOneServerWritesNoFailure()
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);
            AddNewItems(discord);
            await discord.MakeGuildUnavailableAsync();

            Assert.Equal("1 armor: posted in 1 of 1 server; 1 map: posted in 1 of 1 server; 1 lobby: posted in 1 of 1 server; 3 items wait for 1 server",
                await discord.RunNotificationJobAsync());

            Assert.All(discord.Store.Deliveries, row => Assert.Equal((FakeDiscord.JoinedGuildId, DeliveryState.Sent), (row.guild_id, row.state)));
            Assert.Null(Status(discord, FakeDiscord.GuildId, PostSource.Armor));
            Assert.All(Queue(discord, FakeDiscord.GuildId, PostSource.Armor), row => Assert.Equal(QueueState.Pending, row.state));
        }

        // A server whose map post waited through an outage while the map was updated gets the map as it is then, update and
        // all, so when the cooldown is over only the server that got the first version gets the update
        [Fact]
        public async Task ServerWhosePostWaitedThroughAnUpdateIsNotSentItAgain()
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);
            await discord.MakeGuildUnavailableAsync();

            var map = NewMap(NewId);
            discord.Steam.LatestItems.Insert(0, map);
            await discord.RunNotificationJobAsync();

            map.TimeUpdated = map.TimeUpdated.AddMinutes(30);
            await discord.RunNotificationJobAsync();
            await discord.MakeGuildAvailableAsync();
            await discord.RunNotificationJobAsync();

            discord.MoveClock(PostingLimits.Cooldown);
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal([(FakeDiscord.JoinedChannelId, NewMapHeading), (FakeDiscord.ChannelId, NewMapHeading), (FakeDiscord.JoinedChannelId, UpdatedMapHeading)],
                Sends(discord).Where(step => step.EmbedTitles.Contains(NewMapTitle)).Select(step => (step.SentTo, step.Content!)));
        }

        // A post that goes from the map's details, as when Steam's list of the latest maps fails, shows an update no read has
        // found yet: the post records it, so when a read finds the update after the cooldown, only the server that got the
        // first version is sent it
        [Fact]
        public async Task ServerWhosePostShowedAnUpdateNoReadFoundIsNotSentItAgain()
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);
            await discord.MakeGuildUnavailableAsync();

            var map = NewMap(NewId);
            discord.Steam.LatestItems.Insert(0, map);
            await discord.RunNotificationJobAsync();

            map.TimeUpdated = map.TimeUpdated.AddMinutes(30);
            discord.Steam.LatestFails = true;
            await discord.MakeGuildAvailableAsync();
            await discord.RunNotificationJobAsync();

            discord.Steam.LatestFails = false;
            discord.MoveClock(PostingLimits.Cooldown);
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            var sends = Sends(discord).Where(step => step.EmbedTitles.Contains(NewMapTitle)).ToList();

            Assert.Equal([(FakeDiscord.JoinedChannelId, NewMapHeading), (FakeDiscord.ChannelId, NewMapHeading), (FakeDiscord.JoinedChannelId, UpdatedMapHeading)],
                sends.Select(step => (step.SentTo, step.Content!)));
            Assert.Equal($"<t:{Detector.Revision(map)}:f>", UpdateShown(sends[1]));
        }

        // A server's map post refused by Discord gives back the map and, when the map was updated meanwhile, the update it
        // showed; switched off, the server has both dropped. Switched on again before the update's cooldown is over, it isn't
        // owed the map any more, but it is owed the update the read after the cooldown queues, as the other server is, whether
        // its refused post came after the update or before it.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ServerSwitchedOffAndOnAfterARefusedPostGetsTheUpdateFoundAfterIt(bool refusedAfterTheUpdate)
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);
            await discord.MakeGuildUnavailableAsync();

            var map = NewMap(NewId);
            discord.Steam.LatestItems.Insert(0, map);
            await discord.RunNotificationJobAsync();

            // The author's fix half an hour after the upload, in its cooldown, before the refused post or after it
            if (refusedAfterTheUpdate)
                map.TimeUpdated = map.TimeUpdated.AddMinutes(30);

            await discord.MakeGuildAvailableAsync();
            discord.ChannelFailures[FakeDiscord.ChannelId] = (403, 50013);
            await discord.RunNotificationJobAsync();

            discord.Store.SetSwitches(Switches(FakeDiscord.GuildId, maps: false));
            await discord.RunNotificationJobAsync();

            if (!refusedAfterTheUpdate)
                map.TimeUpdated = map.TimeUpdated.AddMinutes(30);

            discord.Store.SetSwitches(Switches(FakeDiscord.GuildId) with { version = 2 });
            discord.ChannelFailures.Clear();
            await discord.RunNotificationJobAsync();

            discord.MoveClock(PostingLimits.Cooldown);
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            var sends = Sends(discord).Where(step => step.EmbedTitles.Contains(NewMapTitle)).ToList();

            Assert.Equal([NewMapHeading, UpdatedMapHeading], sends.Where(step => step.SentTo == FakeDiscord.ChannelId).Select(step => step.Content!));
            Assert.Equal([NewMapHeading, UpdatedMapHeading], sends.Where(step => step.SentTo == FakeDiscord.JoinedChannelId).Select(step => step.Content!));
            Assert.Equal($"<t:{Detector.Revision(map)}:f>", UpdateShown(sends.Last(step => step.SentTo == FakeDiscord.ChannelId)));
        }

        // A map post that showed an update and never left the bot gives back the map's rows for both versions, so the next
        // message carries both rows of its one map: it's recorded as one map, as the cache's refusal of them is
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MessageCountsItsMapsNotTheirRows(bool refused)
        {
            await using var discord = await StartWithBaselineAsync();
            discord.NoConnection[FakeDiscord.ChannelId] = true;

            var map = NewMap(NewId);
            discord.Steam.LatestItems.Insert(0, map);
            await discord.RunNotificationJobAsync();

            map.TimeUpdated = map.TimeUpdated.AddMinutes(30);
            await discord.RunNotificationJobAsync();

            discord.NoConnection.Clear();

            if (refused)
                discord.Guilds.AllGuilds = [FakeGuildData.CreateGuild(FakeDiscord.NoSendChannelId)];

            await discord.RunNotificationJobAsync();

            Assert.Equal(2, Queue(discord, FakeDiscord.GuildId, PostSource.Maps).Count);
            var last = discord.Store.Deliveries[^1];
            Assert.Equal((refused ? DeliveryState.Failed : DeliveryState.Sent, (ushort)1), (last.state, last.item_count));
        }

        // The run's detail says what it posted, kind by kind, in how many of the servers it tried, and what still waits
        [Fact]
        public async Task RunDetailSaysWhatWasPosted()
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);
            AddNewItems(discord);
            discord.Steam.LatestItems.Insert(0, NewMap(NewId + 1));
            discord.ChannelFailures[FakeDiscord.JoinedChannelId] = (403, 50013);

            var detail = await discord.RunNotificationJobAsync();

            Assert.Equal("1 armor: posted in 1 of 2 servers, 1 failed; 2 maps: posted in 1 of 2 servers, 1 failed; 1 lobby: posted in 1 of 2 servers, 1 failed; "
                + "4 items wait for 1 server", detail);

            discord.ChannelFailures.Clear();

            Assert.Equal("1 armor: posted in 1 of 1 server; 2 maps: posted in 1 of 1 server; 1 lobby: posted in 1 of 1 server", await discord.RunNotificationJobAsync());
            Assert.Equal("nothing new to post", await discord.RunNotificationJobAsync());
        }

        // Never more than job_runs keeps, however much it has to say
        [Fact]
        public void RunDetailIsAtMost300Characters()
        {
            var report = new DeliveryReport();

            foreach (var source in PostSource.Posted)
            {
                var tally = report.Kind(source);
                tally.Items.UnionWith(Enumerable.Range(1, 60000).Select(id => (ulong)id));
                (tally.Tried, tally.Posted, tally.Failed, tally.Unanswered) = (60000, 20000, 20000, 20000);
            }

            var detail = RunSummary.Of([new(PostSource.Armor, BaselineReason.First, 60000), new(PostSource.Maps, BaselineReason.OlderBot, 20, DateTime.UtcNow)],
                "MySqlException, MySQL error 1045", report, foundNew: true);

            Assert.Equal(RunSummary.Limit, detail.Length);
            Assert.EndsWith("...", detail);
        }

        // One armor row the read can't take stops armor posts, so the run's detail names it; the maps and lobbies still go
        [Fact]
        public async Task UnreadableArmorIsNamedInTheRunsDetail()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            discord.Armor.Failure = new InvalidCastException("Unable to cast the row's value");

            Assert.Equal("armor list couldn't be read (InvalidCastException); 1 map: posted in 1 of 1 server; 1 lobby: posted in 1 of 1 server",
                await discord.RunNotificationJobAsync());
        }
    }
}
