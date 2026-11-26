using DataAccessLibrary.Posting;
using DataAccessLibrary.Requests.Models;
using Microsoft.Extensions.DependencyInjection;
using MinerBot.Shared.Delivery;
using MinerBot.Shared.Requests;
using MinerBot_2._0.Jobs;
using MinerBot_2._0.Posting;
using MinerBot_2._0.Requests;
using MinerBot_2._0.Status;
using Steam.Models;
using SteamWebAPIAccess.Models;

namespace MinerBot.Tests
{
    // Pausing the notification job's armor, map and lobby posts from the dashboard: what's found while paused is queued, not
    // posted, and goes out at the first run after the resume, a restart between them too. Until the bot has read whether
    // posts are paused, they wait too, unless the bot has no BotConnection, when they never do. Each run here is a new job on
    // the real Discord.Net pipeline.
    [Collection("Discord")]
    public class PostsPauseTests
    {
        const string ArmorTitle = "New Armor Styles:";
        const string SendStep = "send message";
        const int NewId = 100;
        const string PauseUnknown = "posts wait until the bot has read whether they're paused; what this run found is queued";

        static List<string> SentTitles(FakeDiscord discord) => discord.Steps.Where(step => step.Name == SendStep).SelectMany(step => step.EmbedTitles).ToList();

        static LobbyInfo Lobby(ulong id) => Posts.Lobby(id);

        static void AddNewItems(FakeDiscord discord)
        {
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Steam.LatestItems.Insert(0, FakeGetData.CreateItem(NewId));
            discord.Steam.Lobbies.Add(Lobby(NewId));
        }

        // One run as JobHandler makes it, 5 minutes after the last, with its detail
        static async Task<string?> RunAsync(FakeDiscord discord)
        {
            discord.MoveClock(TimeSpan.FromMinutes(5));
            return await ActivatorUtilities.CreateInstance<GuildNotificationJob>(discord.Services).NotifyAllGuilds().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }

        static PostsPause Pause(FakeDiscord discord) => discord.Services.GetRequiredService<PostsPause>();

        static void Paused(FakeDiscord discord, bool paused) => Pause(discord).Load(paused, paused ? "Maintenance" : null, discord.Clock.UtcNow.UtcDateTime);

        // How many of Steam's latest maps the last run read: five again once nothing comes after a gap
        static int LastRead(FakeDiscord discord) => discord.Steam.LatestAsked!.Value.Maps;

        // What a paused run found is queued for its servers, and nothing is sent, however many runs the pause lasts
        [Fact]
        public async Task PausedRunPostsNothingAndQueuesWhatItFound()
        {
            await using var discord = await FakeDiscord.StartAsync();
            await RunAsync(discord);

            Paused(discord, true);
            AddNewItems(discord);

            for (var run = 0; run < 3; run++)
                await RunAsync(discord);

            Assert.Empty(SentTitles(discord));
            Assert.Equal([(PostSource.Armor, QueueState.Pending), (PostSource.Maps, QueueState.Pending), (PostSource.Lobbies, QueueState.Pending)],
                discord.Store.Queue.Select(row => (row.source, row.state)));
            Assert.Empty(discord.Store.Deliveries);
        }

        // An armor approved while paused goes out at the first run after the resume, days later too, within its 7 days
        [Fact]
        public async Task ResumePostsWhatWaited()
        {
            await using var discord = await FakeDiscord.StartAsync();
            await RunAsync(discord);
            Paused(discord, true);

            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));

            for (var run = 0; run < 3; run++)
                await RunAsync(discord);

            discord.MoveClock(TimeSpan.FromDays(3));
            Paused(discord, false);
            await RunAsync(discord);

            Assert.Equal([ArmorTitle], SentTitles(discord));
            Assert.Contains("Armor 100", discord.Steps.Single(step => step.Name == SendStep).EmbedDescriptions.Single());
        }

        // A map found while paused still goes out at the resume after five later maps have pushed it out of the latest five:
        // it was queued, and its details are looked up by its ID, while the read stays at five
        [Fact]
        public async Task MapPushedOutOfTheLatestFiveWhilePausedIsPostedAtTheResume()
        {
            await using var discord = await FakeDiscord.StartAsync();
            await RunAsync(discord);
            Paused(discord, true);

            discord.Steam.LatestItems.Insert(0, Posts.NewMap(NewId));
            await RunAsync(discord);

            for (var id = NewId + 1; id <= NewId + PostingLimits.LatestMaps; id++)
                discord.Steam.LatestItems.Insert(0, Posts.NewMap(id));

            await RunAsync(discord);

            Paused(discord, false);
            await RunAsync(discord);

            Assert.Equal(["Map 105", "Map 104", "Map 103", "Map 102", "Map 101", "Map 100"], SentTitles(discord));
            Assert.Equal(PostingLimits.LatestMaps, LastRead(discord));
        }

        // A map that waited and was hidden meanwhile is dropped as hidden, and the rest of what waited goes out
        [Fact]
        public async Task AMapThatWaitedAndWasHiddenIsDroppedAndTheRestPosted()
        {
            await using var discord = await FakeDiscord.StartAsync();
            await RunAsync(discord);
            Paused(discord, true);

            discord.Steam.LatestItems.Insert(0, Posts.NewMap(NewId));
            discord.Steam.LatestItems.Insert(0, Posts.NewMap(NewId + 1));
            await RunAsync(discord);

            discord.Steam.LatestItems.RemoveAll(map => map.PublishedFileId == NewId);
            Paused(discord, false);

            Assert.Equal("1 map: posted in 1 of 1 server", await RunAsync(discord));
            Assert.Equal(["Map 101"], SentTitles(discord));
            Assert.Equal((QueueState.Dropped, DeliveryReasons.Hidden), discord.Store.Queue.Where(row => row.item_id == NewId).Select(row => (row.state, row.reason)).Single());
            Assert.Equal("nothing new to post", await RunAsync(discord));
        }

        // Shown again unchanged, as when its author hid it for a moment: what was dropped as hidden goes out then, once
        [Fact]
        public async Task AMapThatWaitedAndWasHiddenIsPostedWhenItIsShownAgain()
        {
            await using var discord = await FakeDiscord.StartAsync();
            await RunAsync(discord);
            Paused(discord, true);

            var map = Posts.NewMap(NewId);
            discord.Steam.LatestItems.Insert(0, map);
            await RunAsync(discord);

            discord.Steam.LatestItems.Remove(map);
            Paused(discord, false);

            Assert.Equal("nothing new to post", await RunAsync(discord));
            Assert.Equal((QueueState.Dropped, DeliveryReasons.Hidden), discord.Store.Queue.Select(row => (row.state, row.reason)).Single());

            discord.Steam.LatestItems.Insert(0, map);

            Assert.Equal("1 map: posted in 1 of 1 server", await RunAsync(discord));
            Assert.Equal("nothing new to post", await RunAsync(discord));
            Assert.Equal([Posts.NewMapTitle], SentTitles(discord));
            Assert.Equal([Posts.NewMapHeading], Posts.MapHeadings(discord));
            Assert.Equal(QueueState.Sent, discord.Store.Queue.Single().state);
        }

        // Shown again while it can't go yet, while posts are paused or while its server is in an outage: what the run reopened
        // counts as what waits, and as news, and the map goes out once it can, once
        [Fact]
        public async Task AMapShownAgainWhileItCantGoYetCountsAsWaiting()
        {
            await using var discord = await FakeDiscord.StartAsync();
            await RunAsync(discord);
            Paused(discord, true);

            var map = Posts.NewMap(NewId);
            discord.Steam.LatestItems.Insert(0, map);
            await RunAsync(discord);

            discord.Steam.LatestItems.Remove(map);
            Paused(discord, false);
            Assert.Equal("nothing new to post", await RunAsync(discord));

            Paused(discord, true);
            discord.Steam.LatestItems.Insert(0, map);

            Assert.Equal("posts are paused: 1 map waits for 1 server", await RunAsync(discord));

            discord.Steam.LatestItems.Remove(map);
            Paused(discord, false);
            Assert.Equal("nothing new to post", await RunAsync(discord));
            Assert.Equal((QueueState.Dropped, DeliveryReasons.Hidden), discord.Store.Queue.Select(row => (row.state, row.reason)).Single());

            await discord.MakeGuildUnavailableAsync();
            discord.Steam.LatestItems.Insert(0, map);

            Assert.Equal("1 item waits for 1 server", await RunAsync(discord));

            await discord.MakeGuildAvailableAsync();

            Assert.Equal("1 map: posted in 1 of 1 server", await RunAsync(discord));
            Assert.Equal([Posts.NewMapTitle], SentTitles(discord));
            Assert.Equal(QueueState.Sent, discord.Store.Queue.Single().state);
        }

        // Shown again with its author's fix, made while Steam didn't show it: it goes out at once, as it is then, as a new map,
        // though the fix is in its cooldown, and the read after that cooldown doesn't post the fix again. Maps updated after it
        // meanwhile, which push it out of the read, don't matter.
        [Fact]
        public async Task AMapThatWaitedAndWasHiddenIsPostedAtOnceWhenItIsShownAgainWithAnUpdate()
        {
            await using var discord = await FakeDiscord.StartAsync();
            await RunAsync(discord);
            Paused(discord, true);

            var map = Posts.NewMap(NewId);
            discord.Steam.LatestItems.Insert(0, map);
            await RunAsync(discord);

            discord.Steam.LatestItems.Remove(map);
            map.TimeUpdated = map.TimeUpdated.AddMinutes(30);
            Paused(discord, false);

            Assert.Equal("nothing new to post", await RunAsync(discord));

            discord.Steam.LatestItems.Insert(0, map);

            Assert.Equal("1 map: posted in 1 of 1 server", await RunAsync(discord));

            for (var id = NewId + 1; id <= NewId + PostingLimits.LatestMaps; id++)
            {
                var other = Posts.NewMap(id);
                other.TimeCreated = other.TimeUpdated = map.TimeUpdated.AddHours(id - NewId);
                discord.Steam.LatestItems.Insert(0, other);
                await RunAsync(discord);
            }

            discord.MoveClock(PostingLimits.Cooldown);
            await RunAsync(discord);
            await RunAsync(discord);

            var posts = discord.Steps.Where(step => step.Name == SendStep && step.EmbedTitles.Contains(Posts.NewMapTitle)).ToList();

            Assert.Equal([Posts.NewMapHeading], posts.Select(step => step.Content));
            Assert.Equal($"<t:{Detector.Revision(map)}:f>", Posts.UpdateShown(posts[0]));
            Assert.Equal([(Detector.Revision(map) - 1800, QueueState.Sent), (Detector.Revision(map), QueueState.Sent)],
                discord.Store.Queue.Where(row => row.source == PostSource.Maps && row.item_id == NewId).Select(row => (row.revision, row.state)));
        }

        // A refused post of a map that showed its author's fix gives back the fix's row with the map's, and the map hidden then
        // drops both. Shown again with that fix, it goes out once, holding both rows, so neither the read after the fix's
        // cooldown nor any read after it posts it there again.
        [Fact]
        public async Task AMapWhosePostShowedAnUpdateAndWasRefusedIsPostedOnceWhenItIsShownAgain()
        {
            await using var discord = await FakeDiscord.StartAsync();
            await RunAsync(discord);
            Paused(discord, true);

            var map = Posts.NewMap(NewId);
            discord.Steam.LatestItems.Insert(0, map);
            await RunAsync(discord);

            map.TimeUpdated = map.TimeUpdated.AddMinutes(30);
            discord.ChannelFailures[FakeDiscord.ChannelId] = (403, 50013);
            Paused(discord, false);
            await RunAsync(discord);

            discord.ChannelFailures.Clear();
            discord.Steam.LatestItems.Remove(map);
            await RunAsync(discord);

            Assert.Equal([(QueueState.Dropped, DeliveryReasons.Hidden), (QueueState.Dropped, DeliveryReasons.Hidden)],
                discord.Store.Queue.Where(row => row.item_id == NewId).Select(row => (row.state, row.reason)));

            discord.Steam.LatestItems.Insert(0, map);

            Assert.Equal("1 map: posted in 1 of 1 server", await RunAsync(discord));

            discord.MoveClock(PostingLimits.Cooldown);
            await RunAsync(discord);
            await RunAsync(discord);
            await RunAsync(discord);

            Assert.Single(discord.MessagesIn(FakeDiscord.ChannelId));
            Assert.Equal([QueueState.Sent, QueueState.Sent], discord.Store.Queue.Where(row => row.item_id == NewId).Select(row => row.state));
        }

        // A map updated while its post waits goes out at the resume as it is then, with that update, which the read after its
        // cooldown doesn't post again
        [Fact]
        public async Task AMapUpdatedWhileItsPostWaitsIsPostedOnce()
        {
            await using var discord = await FakeDiscord.StartAsync();
            await RunAsync(discord);
            Paused(discord, true);

            var map = Posts.NewMap(NewId);
            discord.Steam.LatestItems.Insert(0, map);
            await RunAsync(discord);

            // Its author's fix, half an hour after the upload and so in its cooldown
            map.TimeUpdated = map.TimeUpdated.AddMinutes(30);
            await RunAsync(discord);

            Paused(discord, false);
            Assert.Equal("1 map: posted in 1 of 1 server", await RunAsync(discord));

            discord.MoveClock(PostingLimits.Cooldown);

            Assert.Equal("nothing new to post", await RunAsync(discord));
            Assert.Equal("nothing new to post", await RunAsync(discord));
            Assert.Equal([Posts.NewMapHeading], Posts.MapHeadings(discord));
            Assert.Equal([(Detector.Revision(map) - 1800, QueueState.Sent), (Detector.Revision(map), QueueState.Sent)],
                discord.Store.Queue.Select(row => (row.revision, row.state)));
        }

        // Maps posted shortly before the pause and updated again during it are in their cooldown, so they're neither posted
        // nor queued, but they take places in Steam's list above a map that waits, and push it out of the read: it's still
        // posted at the resume, from its details, and the read is five
        [Fact]
        public async Task AMapThatWaitsIsPostedAfterMapsInTheirCooldownAreUpdatedAboveIt()
        {
            await using var discord = await FakeDiscord.StartAsync();
            await RunAsync(discord);

            for (var id = NewId; id < NewId + PostingLimits.LatestMaps; id++)
                discord.Steam.LatestItems.Insert(0, Posts.NewMap(id));

            await RunAsync(discord);
            discord.Steam.LatestItems.Insert(0, Posts.NewMap(NewId + PostingLimits.LatestMaps));
            await RunAsync(discord);

            const int Waits = NewId + 100;
            Paused(discord, true);
            discord.Steam.LatestItems.Insert(0, Posts.NewMap(Waits));
            await RunAsync(discord);

            // The six maps posted before the pause are updated again, after the one that waits
            foreach (var id in Enumerable.Range(NewId, PostingLimits.LatestMaps + 1))
            {
                var map = discord.Steam.LatestItems.Single(item => item.PublishedFileId == (ulong)id);
                discord.Steam.LatestItems.Remove(map);
                map.TimeUpdated = DateTime.UnixEpoch.AddDays(NewId).AddHours(1).AddMinutes(id);
                discord.Steam.LatestItems.Insert(0, map);
            }

            await RunAsync(discord);

            var before = SentTitles(discord).Count;
            Paused(discord, false);

            Assert.Equal("1 map: posted in 1 of 1 server", await RunAsync(discord));
            Assert.Equal([$"Map {Waits}"], SentTitles(discord).Skip(before));
            Assert.Equal(PostingLimits.LatestMaps, LastRead(discord));
        }

        // Discord takes ten embeds in a message at most, so a resume that posts more maps sends them in several messages
        [Fact]
        public async Task MoreThanTenMapsThatWaitedGoOutInSeveralMessages()
        {
            await using var discord = await FakeDiscord.StartAsync();
            await RunAsync(discord);
            Paused(discord, true);

            for (var id = NewId; id < NewId + 12; id++)
            {
                discord.Steam.LatestItems.Insert(0, Posts.NewMap(id));
                await RunAsync(discord);
            }

            Paused(discord, false);
            await RunAsync(discord);

            Assert.Equal(Enumerable.Range(NewId, 12).Reverse().Select(id => $"Map {id}"), SentTitles(discord));
            Assert.Equal([10, 2], discord.Steps.Where(step => step.Name == SendStep).Select(step => step.EmbedTitles.Count()));
        }

        // When a later message of those fails, as when Discord stops taking posts partway through the run, the maps it held
        // still wait, below the ten newer ones that went out, and the next run posts them though nothing changed on Steam
        [Fact]
        public async Task MapsThatWaitedAndDidntGoOutWithTheNewerOnesArePostedAtTheNextRun()
        {
            await using var discord = await FakeDiscord.StartAsync();
            await RunAsync(discord);
            Paused(discord, true);

            for (var id = NewId; id < NewId + 12; id++)
            {
                discord.Steam.LatestItems.Insert(0, Posts.NewMap(id));
                await RunAsync(discord);
            }

            Paused(discord, false);
            discord.FailStep = SendStep;
            discord.FailStepAfter = 1;

            Assert.Equal("12 maps: posted in 0 of 1 server, 1 failed; 2 items wait for 1 server", await RunAsync(discord));

            discord.FailStep = null;
            var tried = SentTitles(discord).Count;

            Assert.Equal("2 maps: posted in 1 of 1 server", await RunAsync(discord));
            Assert.Equal(["Map 101", "Map 100"], SentTitles(discord).Skip(tried));
            Assert.Equal(PostingLimits.LatestMaps, LastRead(discord));
        }

        // A map posted shortly before the pause and updated again during it, in its cooldown, before the maps that wait, is
        // below all of them. The first run a day later reads deep, after the gap, so it finds the update, and posts it last.
        // When only the message with the update fails, the update waits, and the next run posts it from its details.
        [Fact]
        public async Task AnUpdateBelowTheMapsThatWaitedIsPostedAtTheNextRunWhenOnlyItsMessageFails()
        {
            await using var discord = await FakeDiscord.StartAsync();
            await RunAsync(discord);
            discord.Steam.LatestItems.Insert(0, Posts.NewMap(NewId));
            await RunAsync(discord);
            Paused(discord, true);

            discord.MoveClock(TimeSpan.FromHours(1));
            var map = discord.Steam.LatestItems.Single(item => item.PublishedFileId == NewId);
            discord.Steam.LatestItems.Remove(map);
            map.TimeUpdated = map.TimeUpdated.AddMinutes(1);
            discord.Steam.LatestItems.Insert(0, map);

            for (var id = NewId + 1; id <= NewId + 10; id++)
            {
                var newer = Posts.NewMap(id);
                newer.TimeCreated = newer.TimeUpdated = newer.TimeUpdated.AddMinutes(1);
                discord.Steam.LatestItems.Insert(0, newer);
                await RunAsync(discord);
            }

            discord.MoveClock(PostingLimits.Cooldown);
            Paused(discord, false);
            discord.FailStep = SendStep;
            discord.FailStepAfter = 1;
            var before = SentTitles(discord).Count;

            await RunAsync(discord);
            Assert.Equal(Enumerable.Range(NewId, 11).Reverse().Select(id => $"Map {id}"), SentTitles(discord).Skip(before));

            discord.FailStep = null;
            var tried = SentTitles(discord).Count;

            await RunAsync(discord);
            Assert.Equal([$"Map {NewId}"], SentTitles(discord).Skip(tried));
            Assert.Equal(PostingLimits.LatestMaps, LastRead(discord));
        }

        // Started with MinerBot's database, before the request worker read the row: the pause isn't known, so posts wait,
        // and what the runs found is queued
        [Fact]
        public async Task PauseUnknownAtStartHolds()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Settings["ConnectionStrings:BotConnection"] = RequestBot.Connection;

            Assert.Equal(PauseUnknown, await RunAsync(discord));

            AddNewItems(discord);

            Assert.Equal(PauseUnknown, await RunAsync(discord));
            Assert.Empty(SentTitles(discord));
            Assert.Equal(3, discord.Store.Queue.Count(row => row.state == QueueState.Pending));
            Assert.Null(Pause(discord).Read().Paused);
        }

        // Maps found before the bot has read whether posts are paused, as while MinerBot's database refuses it at a start,
        // are queued as they are while paused: a map that five later ones push out of the latest five meanwhile is still
        // posted by the first run after the read, and the read stays at five
        [Fact]
        public async Task MapPushedOutOfTheLatestFiveBeforeThePauseIsReadIsPostedAfterTheRead()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Settings["ConnectionStrings:BotConnection"] = RequestBot.Connection;
            await RunAsync(discord);

            discord.Steam.LatestItems.Insert(0, Posts.NewMap(NewId));
            await RunAsync(discord);

            for (var id = NewId + 1; id <= NewId + PostingLimits.LatestMaps; id++)
                discord.Steam.LatestItems.Insert(0, Posts.NewMap(id));

            Assert.Equal(PauseUnknown, await RunAsync(discord));

            Paused(discord, false);

            Assert.Equal("6 maps: posted in 1 of 1 server", await RunAsync(discord));
            Assert.Equal(["Map 105", "Map 104", "Map 103", "Map 102", "Map 101", "Map 100"], SentTitles(discord));
            Assert.Equal(PostingLimits.LatestMaps, LastRead(discord));
        }

        // README lets the bot run without MinerBot's database, and then nothing can pause its posts
        [Fact]
        public async Task WithoutBotConnectionPostsNeverWait()
        {
            await using var discord = await FakeDiscord.StartAsync();
            await RunAsync(discord);
            AddNewItems(discord);

            Assert.Equal("1 armor: posted in 1 of 1 server; 1 map: posted in 1 of 1 server; 1 lobby: posted in 1 of 1 server", await RunAsync(discord));
            Assert.Equal([ArmorTitle, "Map 100", "__NEW__ Murder Miners Public Lobbies"], SentTitles(discord));
            Assert.False(Pause(discord).Read().Paused);
        }

        // A new process over the same MinerBot's database: the next run reads the pause and holds, with an event that says so
        [Fact]
        public async Task PauseSurvivesARestart()
        {
            var first = await RequestBot.StartAsync();
            FakeRequestData data;

            try
            {
                first.Add(RequestKinds.PostsPause, argument: "Maintenance");
                await first.TickAsync();
                data = first.Data;
            }
            finally
            {
                await first.DisposeAsync();
            }

            await using var second = await RequestBot.StartAsync(data: data);
            await RunAsync(second.Discord);
            await second.TickAsync();
            AddNewItems(second.Discord);

            Assert.Equal("posts are paused: 1 armor, 1 map and 1 lobby wait for 1 server", await RunAsync(second.Discord));
            Assert.Empty(SentTitles(second.Discord));
            Assert.True(second.Pause.Read().Paused);
            Assert.Equal([(BotEvents.PostsPaused, (string?)"still paused since 2026-11-20 12:06:00 UTC, by request 1")], second.Events(BotEvents.PostsPaused));
        }

        [Fact]
        public async Task PausedDetailCountsWhatWasFound()
        {
            await using var discord = await FakeDiscord.StartAsync();
            await RunAsync(discord);
            Paused(discord, true);

            Assert.Equal("posts are paused, and nothing waits", await RunAsync(discord));

            AddNewItems(discord);
            discord.Steam.Lobbies.Add(Lobby(NewId + 1));

            Assert.Equal("posts are paused: 1 armor, 1 map and 2 lobbies wait for 1 server", await RunAsync(discord));

            discord.Steam.Lobbies.Clear();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId + 1));

            Assert.Equal("posts are paused: 2 armor, 1 map and 2 lobbies wait for 1 server", await RunAsync(discord));
        }

        // Taken down during the pause, it's no longer in the Approved read, so what waited for it is dropped as not approved
        [Fact]
        public async Task TakenDownWhilePausedIsNeverPosted()
        {
            await using var discord = await FakeDiscord.StartAsync();
            await RunAsync(discord);
            Paused(discord, true);

            var armor = FakeArmorData.CreateArmor(NewId);
            discord.Armor.AllArmor.Add(armor);
            await RunAsync(discord);
            discord.Armor.AllArmor.Remove(armor);

            Paused(discord, false);
            await RunAsync(discord);

            Assert.Empty(SentTitles(discord));
            Assert.Equal((QueueState.Dropped, DeliveryReasons.NotApproved), discord.Store.Queue.Select(row => (row.state, row.reason)).Single());
        }

        [Fact]
        public async Task PauseWhenPausedIsRefused()
        {
            await using var bot = await RequestBot.StartAsync();
            var first = bot.Add(RequestKinds.PostsPause, argument: "Maintenance");
            var second = bot.Add(RequestKinds.PostsPause, argument: "Again");

            await bot.TickAsync();

            Assert.Equal(RequestStates.Done, bot.Row(first).state);
            Assert.Equal((RequestStates.Refused, "Posts are already paused, since 12:06:00 UTC on 2026-11-20."), (bot.Row(second).state, bot.Row(second).result));
            Assert.Equal("Maintenance", bot.Data.Settings!.pause_reason);
            Assert.Equal([(BotEvents.PostsPaused, (string?)"by request 1")], bot.Events(BotEvents.PostsPaused));
        }

        [Fact]
        public async Task ResumeWhenNotPausedIsRefused()
        {
            await using var bot = await RequestBot.StartAsync();
            var id = bot.Add(RequestKinds.PostsResume);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused, "Posts aren't paused."), (bot.Row(id).state, bot.Row(id).result));
            Assert.Null(bot.Data.Settings);
        }

        // Its answer didn't come, though it landed: memory waits, and the next tick reads the row back and follows it
        [Fact]
        public async Task APauseThatDidntConfirmIsReadBack()
        {
            await using var bot = await RequestBot.StartAsync();
            bot.Data.FailNext(nameof(FakeRequestData.SavePause), new TimeoutException("The database didn't answer within 15 seconds"), lands: true);
            var id = bot.Add(RequestKinds.PostsPause, argument: "Maintenance");

            await bot.TickAsync();

            Assert.Equal((RequestStates.Unknown, "MinerBot's database didn't confirm the change within 15 seconds. The bot reads it back and follows what it holds."),
                (bot.Row(id).state, bot.Row(id).result));
            Assert.False(bot.Pause.Read().Paused);

            await bot.TickAsync();

            Assert.True(bot.Pause.Read().Paused);
            Assert.Equal([(BotEvents.PostsPaused, (string?)"by request 1")], bot.Events(BotEvents.PostsPaused));
        }

        // A write whose answer was a lost connection may have landed, so it's unknown, and neither its row nor the owner's
        // message says posts weren't paused: memory waits, and the next tick reads the row back and follows it, rather than
        // the next restart. MySql.Data reports a lost connection with no number and the stream's error inside, and 2013 is
        // the client error for one.
        [Theory]
        [InlineData(0, true)]
        [InlineData(2013, false)]
        public async Task APauseWhoseAnswerWasLostIsUnknownAndReadBack(int number, bool streamError)
        {
            await using var bot = await RequestBot.StartAsync();
            bot.Data.FailNext(nameof(FakeRequestData.SavePause), FakeDatabase.MySqlError(number, streamError ? new IOException("The connection was closed") : null), lands: true);
            var id = bot.Add(RequestKinds.PostsPause, argument: "Maintenance");

            await bot.TickAsync();

            const string Lost = "MinerBot's database didn't confirm the change: the call failed before its answer came. The bot reads it back and follows what it holds.";
            Assert.Equal((RequestStates.Unknown, Lost), (bot.Row(id).state, bot.Row(id).result));
            Assert.Equal($"Pause posts, asked for on the MinerBot dashboard by `Owner` (Discord ID {SignedRequests.OwnerId}), request 1: {Lost} The reason given: `Maintenance`.",
                Assert.Single(bot.Messages()));
            Assert.False(bot.Pause.Read().Paused);

            await bot.TickAsync();

            Assert.True(bot.Pause.Read().Paused);
            Assert.Equal([(BotEvents.PostsPaused, (string?)"by request 1")], bot.Events(BotEvents.PostsPaused));
        }

        // An exception that isn't MySQL's, such as the NullReferenceException MySql.Data throws for a statement that a KILL
        // QUERY ended, doesn't say the server refused the change either: unknown, as a lost answer is, and the next tick reads
        // the row back and follows it
        [Fact]
        public async Task APauseThatEndsWithAnExceptionNotMySqlsIsUnknownAndReadBack()
        {
            await using var bot = await RequestBot.StartAsync();
            bot.Data.FailNext(nameof(FakeRequestData.SavePause), new NullReferenceException(), lands: true);
            var id = bot.Add(RequestKinds.PostsPause, argument: "Maintenance");

            await bot.TickAsync();

            Assert.Equal((RequestStates.Unknown, "MinerBot's database didn't confirm the change: the call failed before its answer came. The bot reads it back and follows what it holds."),
                (bot.Row(id).state, bot.Row(id).result));
            Assert.False(bot.Pause.Read().Paused);

            await bot.TickAsync();

            Assert.True(bot.Pause.Read().Paused);
        }

        // A request after a change that's read back waits for the read, at the next tick, rather than be checked against what
        // the bot held before
        [Fact]
        public async Task ARequestAfterAChangeThatsReadBackWaitsForTheRead()
        {
            await using var bot = await RequestBot.StartAsync();
            bot.Data.FailNext(nameof(FakeRequestData.SavePause), FakeDatabase.MySqlError(2013), lands: true);
            var pause = bot.Add(RequestKinds.PostsPause, argument: "Maintenance");
            var resume = bot.Add(RequestKinds.PostsResume);

            await bot.TickAsync();

            Assert.Equal(RequestStates.Unknown, bot.Row(pause).state);
            Assert.Null(bot.Row(resume).state);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Done, "Posts are resumed. The next notification run, within 5 minutes, posts what waited, unless it waited too long to be news."),
                (bot.Row(resume).state, bot.Row(resume).result));
            Assert.False(bot.Pause.Read().Paused);
            Assert.False(bot.Data.Settings!.posts_paused);
        }

        // MySql.Data doesn't stop a statement the bot gave up on, so a pause that didn't confirm can land after the first
        // read back. The settings are read at every tick for 10 minutes after it, so memory follows the row once it lands, and
        // a resume is then taken rather than refused as "Posts aren't paused". Past the 10 minutes a tick reads them no more.
        [Fact]
        public async Task APauseThatLandsAfterTheReadBackIsFollowed()
        {
            await using var bot = await RequestBot.StartAsync();
            bot.Data.FailNext(nameof(FakeRequestData.SavePause), new TimeoutException("The database didn't answer within 15 seconds"));
            var pause = bot.Add(RequestKinds.PostsPause, argument: "Maintenance");

            await bot.TickAsync();
            await bot.TickAsync();

            Assert.Equal(RequestStates.Unknown, bot.Row(pause).state);
            Assert.False(bot.Pause.Read().Paused);

            // The statement the bot stopped waiting for lands now
            bot.Data.Settings = new BotSettingsRow
            {
                posts_paused = true,
                pause_reason = "Maintenance",
                pause_changed_at = bot.Clock,
                pause_changed_by = SignedRequests.OwnerId,
                pause_request_id = pause,
                updated_at = bot.Clock
            };
            bot.Move(TimeSpan.FromMinutes(1));

            await bot.TickAsync();

            Assert.True(bot.Pause.Read().Paused);
            Assert.Equal([(BotEvents.PostsPaused, (string?)"by request 1")], bot.Events(BotEvents.PostsPaused));

            var resume = bot.Add(RequestKinds.PostsResume);
            await bot.TickAsync();

            Assert.Equal((RequestStates.Done, "Posts are resumed. The next notification run, within 5 minutes, posts what waited, unless it waited too long to be news."),
                (bot.Row(resume).state, bot.Row(resume).result));
            Assert.False(bot.Pause.Read().Paused);

            bot.Move(TimeSpan.FromMinutes(10));
            var reads = bot.Data.Calls.Count(call => call == nameof(FakeRequestData.GetSettings));
            await bot.TickAsync();

            Assert.Equal(reads, bot.Data.Calls.Count(call => call == nameof(FakeRequestData.GetSettings)));
        }

        // A resume right behind a pause that didn't confirm isn't checked against memory the pause may have left wrong: the
        // tick ends, the next one reads the row back, and then takes the resume, so the owner's last request holds
        [Fact]
        public async Task ARequestBehindAChangeThatDidntConfirmWaitsForTheReadBack()
        {
            await using var bot = await RequestBot.StartAsync();
            bot.Data.FailNext(nameof(FakeRequestData.SavePause), new TimeoutException("The database didn't answer within 15 seconds"), lands: true);
            var pause = bot.Add(RequestKinds.PostsPause, argument: "Maintenance");
            var resume = bot.Add(RequestKinds.PostsResume);

            await bot.TickAsync();

            Assert.Equal(RequestStates.Unknown, bot.Row(pause).state);
            Assert.Null(bot.Row(resume).state);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Done, "Posts are resumed. The next notification run, within 5 minutes, posts what waited, unless it waited too long to be news."),
                (bot.Row(resume).state, bot.Row(resume).result));
            Assert.False(bot.Pause.Read().Paused);
            Assert.False(bot.Data.Settings!.posts_paused);
        }

        // MinerBot's database refused the change, with an error of its own, such as a lock wait that timed out or, past the
        // classic numbers, MariaDB's 4025 for a broken constraint, or MySql.Data never reached it (1042): posts stay as they
        // were, in memory and in the table, the request failed and says so, and the owner is told that. The next tick still
        // reads the settings back, as after any change that didn't end done, so the bot never trusts its memory over the row.
        [Theory]
        [InlineData(true, 1205)]
        [InlineData(false, 1205)]
        [InlineData(true, 4025)]
        [InlineData(true, 1042)]
        public async Task APauseOrResumeThatIsntSavedFails(bool pausing, int number)
        {
            await using var bot = await RequestBot.StartAsync();

            if (!pausing)
                bot.Data.Settings = new BotSettingsRow { posts_paused = true, pause_reason = "Held", pause_changed_at = RequestBot.Noon.UtcDateTime };

            bot.Data.FailNext(nameof(FakeRequestData.SavePause), FakeDatabase.MySqlError(number));
            var id = bot.Add(pausing ? RequestKinds.PostsPause : RequestKinds.PostsResume, argument: pausing ? "Maintenance" : null);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Failed, $"MinerBot's database didn't take the change, so posts weren't {(pausing ? "paused" : "resumed")}."),
                (bot.Row(id).state, bot.Row(id).result));
            Assert.Equal(!pausing, bot.Pause.Read().Paused);
            Assert.Equal(!pausing, bot.Data.Settings?.posts_paused ?? false);
            Assert.Empty(bot.Events(pausing ? BotEvents.PostsPaused : BotEvents.PostsResumed));
            Assert.Equal($"request 1: {(pausing ? "Pause posts" : "Resume posts")}, failed", Assert.Single(bot.Events(BotEvents.AlertSent)).Detail);

            await bot.TickAsync();

            Assert.Equal(2, bot.Data.Calls.Count(call => call == nameof(FakeRequestData.GetSettings)));
        }

        // A server admin's own announcement in their own server isn't one of the bot's posts
        [Fact]
        public async Task AnnounceIsNotPaused()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Guilds.Guild = new DataAccessLibrary.Discord.Models.Guild { guild_id = FakeDiscord.GuildId, announcements_channel_id = FakeDiscord.AnnouncementChannelId };
            Paused(discord, true);

            await discord.SendSlashCommandAsync("admin", "announce", """{"name":"message","type":3,"value":"Hello"}""");

            Assert.Equal("Hello", discord.Single(SendStep).Content);
        }

        // The run would find the new items and hold them anyway, so it isn't started
        [Fact]
        public async Task NotificationRunIsRefusedWhilePaused()
        {
            await using var bot = await RequestBot.StartAsync();
            var kind = RequestKinds.Find(RequestKinds.JobNotifications)!;
            var row = SignedRequests.Signed(kind.Word, bot.Clock);

            Assert.Equal("The bot hasn't read yet whether posts are paused, so the notification job didn't run. Try again in a minute.", bot.Actions.Refusal(kind, row, bot.Clock));

            bot.Data.Settings = new BotSettingsRow { posts_paused = true, pause_reason = "Held", pause_changed_at = RequestBot.Noon.UtcDateTime };
            var id = bot.Add(RequestKinds.JobNotifications);

            await bot.TickAsync();

            Assert.Equal((RequestStates.Refused, "Posts are paused, so the notification job didn't run. Resume posts first."), (bot.Row(id).state, bot.Row(id).result));
            Assert.Empty(bot.Discord.Recorder.PeekJobRuns(10));
        }
    }
}
