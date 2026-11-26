using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Posting;
using Discord;
using MinerBot.Shared.Delivery;
using MinerBot_2._0.Models;
using MinerBot_2._0.Posting;
using static MinerBot.Tests.Posts;

namespace MinerBot.Tests
{
    // Each run is a new job object on the real Discord.Net pipeline, the same as JobHandler runs it, over MinerBot's
    // database in memory
    [Collection("Discord")]
    public class NotificationJobTests
    {
        const string WaitingDetail = "Discord hasn't sent the bot's servers yet, so this run found and posted nothing";

        [Fact]
        public async Task FirstRunAnnouncesNothing()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.Lobbies.Add(Lobby(1));

            await discord.RunNotificationJobAsync();

            Assert.Empty(SentTitles(discord));
        }

        [Fact]
        public async Task NewItemsAreAnnouncedOnce()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);

            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, NewMapTitle, LobbyTitle], SentTitles(discord));
            Assert.Contains("Armor 100", Sends(discord)[0].EmbedDescriptions.Single());
        }

        // No post pings anyone, whatever a name in it says
        [Fact]
        public async Task EveryPostPingsNobody()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);

            await discord.RunNotificationJobAsync();

            Assert.Equal(3, Sends(discord).Count);
            Assert.All(Sends(discord), step => Assert.True(step.AllowsNoMentions));
        }

        // Each server gets its own posts, kind by kind
        [Fact]
        public async Task EveryGuildGetsTheNewItems()
        {
            await using var discord = await StartWithBaselineAsync();
            await AddSecondServerAsync(discord);
            AddNewItems(discord);

            await discord.RunNotificationJobAsync();

            foreach (var channel in new[] { FakeDiscord.ChannelId, FakeDiscord.JoinedChannelId })
                Assert.Equal([ArmorTitle, NewMapTitle, LobbyTitle], Sends(discord).Where(step => step.SentTo == channel).SelectMany(step => step.EmbedTitles));
        }

        // Every lobby the list shows is announced, under the same heading
        [Theory]
        [InlineData(0, "2012 Alpha (v0)")]
        [InlineData(39, "Legacy Egg Hunt Branch (v39)")]
        [InlineData(53, "Update 19 (Xbox 360 Branch) (v53)")]
        [InlineData(61, "Update 29 (v61)")]
        [InlineData(80, "Update 35 (Xbox One Branch) (v80)")]
        [InlineData(106, "Other versions")]
        public async Task LobbiesOfEveryVersionAreAnnounced(uint version, string heading)
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Steam.Lobbies.Add(Lobby(1, version));
            discord.Steam.Lobbies.Add(Lobby(2));

            await discord.RunNotificationJobAsync();

            var announced = Assert.Single(Sends(discord)).EmbedDescriptions.Single();
            Assert.Contains("__Update 41 (v105):__\n\n- **Host:** `Host 2`", announced);
            Assert.Contains($"__{heading}:__\n\n- **Host:** `Host 1`", announced);
        }

        // Every one of them counts as posted, the ones past the embed's room too
        [Fact]
        public async Task LobbiesThatPassTheEmbedLimitAreStillAnnounced()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Steam.Lobbies = Enumerable.Range(1, 100).Select(id => LobbyListLimitTests.Lobby(id)).ToList();

            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            var announced = Assert.Single(Sends(discord));
            Assert.Contains("more lobbies", announced.EmbedDescriptions.Single());
            Assert.All(Queue(discord, FakeDiscord.GuildId, PostSource.Lobbies), row => Assert.Equal(QueueState.Sent, row.state));
        }

        [Fact]
        public async Task MapWithALongDescriptionIsAnnounced()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Steam.LatestItems.Insert(0, MapEmbedFlowTests.LongMap(NewId));

            await discord.RunNotificationJobAsync();

            Assert.Equal([NewMapTitle], SentTitles(discord));
        }

        [Fact]
        public async Task MapsThatPassTheMessageLimitAreAnnouncedInSeveralMessages()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Steam.LatestItems = Enumerable.Range(NewId, 5).Reverse().Select(NewLongMap).ToList();

            await discord.RunNotificationJobAsync();

            var messages = Sends(discord);

            Assert.Equal(2, messages.Count);
            Assert.All(messages, message => Assert.InRange(message.EmbedsLength, 1, EmbedBuilder.MaxEmbedLength));
            Assert.Equal(discord.Steam.LatestItems.Select(map => map.Title), SentTitles(discord));
            Assert.Equal("__**New Maps!**__", messages[0].Content);
            Assert.True(string.IsNullOrEmpty(messages[1].Content));
        }

        // Baselines

        [Fact]
        public async Task LobbiesOfAFailedFirstReadAreNotAnnounced()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.Lobbies.Add(Lobby(1));
            discord.Steam.LobbiesAvailable = false;
            await discord.RunNotificationJobAsync();

            discord.Steam.LobbiesAvailable = true;
            await discord.RunNotificationJobAsync();

            Assert.Empty(SentTitles(discord));

            discord.Steam.Lobbies.Add(Lobby(NewId));
            await discord.RunNotificationJobAsync();

            var announced = Assert.Single(Sends(discord)).EmbedDescriptions.Single();
            Assert.Contains($"Host {NewId}", announced);
            Assert.DoesNotContain("Host 1`", announced);
        }

        [Fact]
        public async Task LobbiesOfAFirstReadThatThrowsAreNotAnnounced()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.Lobbies.Add(Lobby(1));
            discord.Steam.LobbyFailure = new TimeoutException("Steam timed out");
            await discord.RunNotificationJobAsync();

            discord.Steam.LobbyFailure = null;
            await discord.RunNotificationJobAsync();

            Assert.Empty(SentTitles(discord));
        }

        [Fact]
        public async Task MapsOfAFailedFirstReadAreNotAnnounced()
        {
            await using var discord = await FakeDiscord.StartAsync();
            var maps = discord.Steam.LatestItems;
            discord.Steam.LatestItems = new();
            await discord.RunNotificationJobAsync();

            discord.Steam.LatestItems = maps;
            await discord.RunNotificationJobAsync();

            Assert.Empty(SentTitles(discord));
        }

        [Fact]
        public async Task MapsOfAFirstReadThatThrowsAreNotAnnounced()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.Failure = new TimeoutException("Steam timed out");
            await discord.RunNotificationJobAsync();

            discord.Steam.Failure = null;
            await discord.RunNotificationJobAsync();

            Assert.Empty(SentTitles(discord));
        }

        [Fact]
        public async Task ArmorOfAFirstReadThatThrowsIsNotAnnounced()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Armor.Failure = new TimeoutException("Database timed out");
            await discord.RunNotificationJobAsync();

            discord.Armor.Failure = null;
            await discord.RunNotificationJobAsync();

            Assert.Empty(SentTitles(discord));
        }

        [Fact]
        public async Task ArmorOfAnEmptyFirstReadIsNotAnnounced()
        {
            await using var discord = await FakeDiscord.StartAsync();
            var armor = discord.Armor.AllArmor;
            discord.Armor.AllArmor = new();
            await discord.RunNotificationJobAsync();

            discord.Armor.AllArmor = armor;
            await discord.RunNotificationJobAsync();

            Assert.Empty(SentTitles(discord));
        }

        // A source that fails waits for its next good read

        [Fact]
        public async Task FailedLobbyReadLeavesTheOtherSourcesAlone()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            discord.Steam.LobbyFailure = new TimeoutException("Steam timed out");

            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, NewMapTitle], SentTitles(discord));
            Assert.Contains("Steam timed out", discord.ConsoleText);

            discord.Steam.LobbyFailure = null;
            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, NewMapTitle, LobbyTitle], SentTitles(discord));
        }

        [Fact]
        public async Task UnavailableLobbiesLeaveTheOtherSourcesAlone()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            discord.Steam.LobbiesAvailable = false;

            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, NewMapTitle], SentTitles(discord));

            discord.Steam.LobbiesAvailable = true;
            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, NewMapTitle, LobbyTitle], SentTitles(discord));
        }

        [Fact]
        public async Task FailedArmorReadLeavesTheOtherSourcesAlone()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            discord.Armor.Failure = new TimeoutException("Database timed out");

            await discord.RunNotificationJobAsync();

            Assert.Equal([NewMapTitle, LobbyTitle], SentTitles(discord));

            discord.Armor.Failure = null;
            await discord.RunNotificationJobAsync();

            Assert.Equal([NewMapTitle, LobbyTitle, ArmorTitle], SentTitles(discord));
        }

        [Fact]
        public async Task FailedMapReadLeavesTheOtherSourcesAlone()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            var maps = discord.Steam.LatestItems;
            discord.Steam.LatestItems = new();

            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, LobbyTitle], SentTitles(discord));

            discord.Steam.LatestItems = maps;
            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, LobbyTitle, NewMapTitle], SentTitles(discord));
        }

        // The website's channel rows are read before anything is found, so the run fails having queued nothing, and the
        // next one finds the same items
        [Fact]
        public async Task FailedGuildListKeepsEverythingForTheNextRun()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            discord.Guilds.Failure = new TimeoutException("Database timed out");

            var failed = await Assert.ThrowsAsync<JobFailedException>(discord.RunNotificationJobAsync);
            Assert.Equal("posts wait: the website's database didn't give the servers' channels (TimeoutException)", failed.Message);

            discord.Guilds.Failure = null;
            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, NewMapTitle, LobbyTitle], SentTitles(discord));
        }

        // Nothing of that run is saved, so the next run takes the baselines
        [Fact]
        public async Task FailedGuildListLeavesTheBaselinesToTheNextRun()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.Lobbies.Add(Lobby(1));
            discord.Guilds.Failure = new TimeoutException("Database timed out");

            await Assert.ThrowsAsync<JobFailedException>(discord.RunNotificationJobAsync);

            Assert.Empty(discord.Store.Baselines);

            discord.Guilds.Failure = null;
            await discord.RunNotificationJobAsync();

            Assert.Empty(SentTitles(discord));
            Assert.Equal([PostSource.Armor, PostSource.Lobbies, PostSource.Maps], discord.Store.Baselines.Select(row => row.source));
        }

        // A refusal puts the items back to waiting, and the next run sends them
        [Fact]
        public async Task BatchThatReachedNoGuildIsSentOnTheNextRun()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            discord.FailStep = SendStep;

            await discord.RunNotificationJobAsync();

            discord.FailStep = null;
            var refused = discord.Steps.Count;
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            var sent = discord.Steps.Skip(refused).Where(step => step.Name == SendStep).SelectMany(step => step.EmbedTitles);
            Assert.Equal([ArmorTitle, NewMapTitle, LobbyTitle], sent);
        }

        // A post that can't be made, such as a map whose title is too long for an embed, waits, and the others go
        [Fact]
        public async Task BadEmbedKeepsItsBatchAndLeavesTheOtherSourcesAlone()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            discord.Steam.LatestItems[0].Title = new string('a', 300);

            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, LobbyTitle], SentTitles(discord));
            Assert.Contains($"Couldn't make the map notification for guild {FakeDiscord.GuildId} (ArgumentException), so its items wait for the next run", discord.ConsoleText);

            discord.Steam.LatestItems[0].Title = NewMapTitle;
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, LobbyTitle, NewMapTitle], SentTitles(discord));
        }

        // An item waits for each server by its own age, so a newer one isn't given up with an older one that keeps failing
        [Fact]
        public async Task EachItemIsGivenUpByItsOwnAge()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.FailStep = SendStep;
            await discord.RunNotificationJobAsync();

            discord.MoveClock(TimeSpan.FromDays(6));
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId + 1));
            await discord.RunNotificationJobAsync();

            // A week after the first armor was found, and a day after the second
            discord.MoveClock(TimeSpan.FromDays(1));
            discord.FailStep = null;
            var refused = discord.Steps.Count;
            await discord.RunNotificationJobAsync();

            var sent = Assert.Single(discord.Steps.Skip(refused), step => step.Name == SendStep).EmbedDescriptions.Single();
            Assert.Contains("Armor 101", sent);
            Assert.DoesNotContain("Armor 100", sent);
            Assert.Equal([(QueueState.Dropped, DeliveryReasons.TooOld), (QueueState.Sent, null)],
                Queue(discord, FakeDiscord.GuildId, PostSource.Armor).Select(row => (row.state, row.reason)));
        }

        // Armor after 7 days, maps after 2, lobbies after 30 minutes, by the time each waited, not the runs that tried it
        [Theory]
        [InlineData(PostSource.Armor, 7 * 24 * 60)]
        [InlineData(PostSource.Maps, 2 * 24 * 60)]
        [InlineData(PostSource.Lobbies, 30)]
        public async Task PendingItemsAreGivenUpAtTheirLimits(string source, int minutes)
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            discord.FailStep = SendStep;
            await discord.RunNotificationJobAsync();

            // The last try inside the limit: the run comes 5 minutes after the clock's move
            discord.MoveClock(TimeSpan.FromMinutes(minutes - 5));
            await discord.RunNotificationJobAsync();

            Assert.Equal(QueueState.Pending, Assert.Single(Queue(discord, FakeDiscord.GuildId, source)).state);

            await discord.RunNotificationJobAsync();

            var row = Assert.Single(Queue(discord, FakeDiscord.GuildId, source));
            Assert.Equal((QueueState.Dropped, DeliveryReasons.TooOld), (row.state, row.reason));
        }

        // A lobby that closed before its post is dropped once a good read no longer lists it, and never posted later
        [Fact]
        public async Task LobbyClosedBeforeItsPostIsDropped()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Steam.Lobbies.Add(Lobby(NewId));
            discord.FailStep = SendStep;
            await discord.RunNotificationJobAsync();

            discord.Steam.Lobbies.Clear();
            discord.FailStep = null;
            var refused = discord.Steps.Count;
            await discord.RunNotificationJobAsync();

            discord.Steam.Lobbies.Add(Lobby(NewId));
            await discord.RunNotificationJobAsync();

            Assert.DoesNotContain(discord.Steps.Skip(refused), step => step.Name == SendStep);
            Assert.Equal((QueueState.Dropped, DeliveryReasons.Closed), Queue(discord, FakeDiscord.GuildId, PostSource.Lobbies).Select(row => (row.state, row.reason)).Single());
        }

        // An open lobby's record says when it was last seen, which its retention goes by: written again once an hour while it
        // stays open, not at every run
        [Fact]
        public async Task OpenLobbyIsSeenAgainOnceAnHour()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Steam.Lobbies.Add(Lobby(NewId));
            await discord.RunNotificationJobAsync();

            var posted = Seen(discord, PostSource.Lobbies, NewId)!.last_seen_at;
            discord.MoveClock(TimeSpan.FromMinutes(50));
            await discord.RunNotificationJobAsync();

            Assert.Equal(posted, Seen(discord, PostSource.Lobbies, NewId)!.last_seen_at);

            await discord.RunNotificationJobAsync();

            Assert.Equal(posted.AddHours(1), Seen(discord, PostSource.Lobbies, NewId)!.last_seen_at);
            Assert.Equal(posted, Seen(discord, PostSource.Lobbies, NewId)!.first_seen_at);
            Assert.Single(Sends(discord));
        }

        // A map that waits and is pushed out of the read is posted from its own details, and dropped once Steam doesn't show
        // it any more; while Steam doesn't answer the look-up, it waits
        [Fact]
        public async Task MapThatWaitsOutOfTheReadIsPostedFromItsDetails()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Steam.LatestItems.Insert(0, NewMap(NewId));
            discord.FailStep = SendStep;
            await discord.RunNotificationJobAsync();

            for (var id = NewId + 1; id <= NewId + PostingLimits.LatestMaps; id++)
                discord.Steam.LatestItems.Insert(0, NewMap(id));

            discord.Steam.ItemsFailure = new HttpRequestException("Steam didn't answer");
            discord.FailStep = null;
            var refused = discord.Steps.Count;
            await discord.RunNotificationJobAsync();

            Assert.Equal(["Map 105", "Map 104", "Map 103", "Map 102", "Map 101"], discord.Steps.Skip(refused).Where(step => step.Name == SendStep).SelectMany(step => step.EmbedTitles));
            Assert.Equal(QueueState.Pending, Queue(discord, FakeDiscord.GuildId, PostSource.Maps).Single(row => row.item_id == NewId).state);
            Assert.Contains("Couldn't look up the maps that wait on Steam (HttpRequestException), so they wait for the next run", discord.ConsoleText);

            discord.Steam.ItemsFailure = null;
            await discord.RunNotificationJobAsync();

            Assert.Equal([NewMapTitle], Sends(discord)[^1].EmbedTitles);
            Assert.Equal([(ulong)NewId], Assert.Single(discord.Steam.ItemsAsked.Skip(1)));
        }

        [Fact]
        public async Task MapHiddenWhileItWaitsIsDropped()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Steam.LatestItems.Insert(0, NewMap(NewId));
            discord.FailStep = SendStep;
            await discord.RunNotificationJobAsync();

            discord.Steam.LatestItems.RemoveAt(0);
            discord.FailStep = null;
            await discord.RunNotificationJobAsync();

            Assert.Equal((QueueState.Dropped, DeliveryReasons.Hidden), Queue(discord, FakeDiscord.GuildId, PostSource.Maps).Select(row => (row.state, row.reason)).Single());
        }

        // A post of several messages whose later one was refused sends only that one's maps again
        [Fact]
        public async Task MapsOfAMessageThatFailedAreSentWithoutRepeatingTheOthers()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Steam.LatestItems = Enumerable.Range(NewId, 5).Reverse().Select(NewLongMap).ToList();
            discord.FailStep = SendStep;
            discord.FailStepAfter = 1;

            await discord.RunNotificationJobAsync();

            var firstRun = Sends(discord);
            Assert.Equal(2, firstRun.Count);

            discord.FailStep = null;
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            var delivered = firstRun.Take(1).Concat(Sends(discord).Skip(2));

            Assert.Equal(discord.Steam.LatestItems.Select(map => map.Title), delivered.SelectMany(step => step.EmbedTitles));
        }

        // Seen IDs

        [Fact]
        public async Task SeenListsKeepOneEntryPerItem()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.Lobbies.Add(Lobby(1));

            for (int run = 0; run < 5; run++)
                await discord.RunNotificationJobAsync();

            AddNewItems(discord);

            for (int run = 0; run < 5; run++)
                await discord.RunNotificationJobAsync();

            Assert.Equal(3, discord.Store.Seen.Count(row => row.source == PostSource.Armor));
            Assert.Equal(6, discord.Store.Seen.Count(row => row.source == PostSource.Maps));
            Assert.Equal(2, discord.Store.Seen.Count(row => row.source == PostSource.Lobbies));
            Assert.Equal(3, discord.Store.Queue.Count);
        }

        [Fact]
        public async Task ItemFoundTwiceInOneReadIsAnnouncedOnce()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Steam.Lobbies.Add(Lobby(NewId));
            discord.Steam.Lobbies.Add(Lobby(NewId));

            await discord.RunNotificationJobAsync();

            var announced = Assert.Single(Sends(discord)).EmbedDescriptions.Single();
            Assert.Single(announced.Split($"Host {NewId}").Skip(1));
        }

        // The ranking can move between two page requests, so a read can have the same map twice, from two details calls
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MapFoundTwiceInOneReadIsAnnouncedOnce(bool firstTryFails)
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Steam.LatestItems.Insert(0, FakeGetData.CreateItem(NewId));
            discord.Steam.LatestItems.Insert(1, FakeGetData.CreateItem(NewId));

            if (firstTryFails)
            {
                discord.FailStep = SendStep;
                await discord.RunNotificationJobAsync();
                discord.FailStep = null;
            }

            var refused = discord.Steps.Count;
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            var sent = Assert.Single(discord.Steps.Skip(refused), step => step.Name == SendStep);
            Assert.Equal([NewMapTitle], sent.EmbedTitles);
        }

        [Fact]
        public async Task MapThatFallsOutOfTheLatestListAndComesBackIsNotAnnouncedAgain()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Steam.LatestItems.Insert(0, FakeGetData.CreateItem(NewId));
            await discord.RunNotificationJobAsync();

            discord.Steam.LatestItems.RemoveAt(0);
            await discord.RunNotificationJobAsync();

            discord.Steam.LatestItems.Insert(0, FakeGetData.CreateItem(NewId));
            await discord.RunNotificationJobAsync();

            Assert.Equal([NewMapTitle], SentTitles(discord));
        }

        // Maps from under the five of a read

        // Steam ranks maps by their last update, so a map under a read is no newer than the oldest map of that read
        static Steam.Models.PublishedFileDetailsModel MapUnderTheRead(int id, int daysOlder = 1)
        {
            var map = FakeGetData.CreateItem(id);
            map.TimeCreated = map.TimeUpdated = FakeGetData.CreateItem(1).TimeUpdated.AddDays(-daysOlder);
            return map;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(0)]
        public async Task MapThatMovesUpWhenOneAboveIsRemovedIsNotAnnounced(int daysOlder)
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.LatestItems.Add(MapUnderTheRead(6, daysOlder));
            await discord.RunNotificationJobAsync();

            discord.Steam.LatestItems.RemoveAt(2);
            await discord.RunNotificationJobAsync();

            Assert.Empty(SentTitles(discord));
        }

        // A baseline reads twenty deep, so the maps above are seen at it. One from under those twenty moves up into them when a
        // map above is removed, and a deep read after a gap finds it: never seen, but made and updated before the baseline's
        // time, so it moved up from below, which isn't news, though it's no older than the others
        [Fact]
        public async Task MapFromUnderTheBaselinesTwentyThatADeepReadFindsIsNotAnnounced()
        {
            const ulong Below = PostingLimits.DeepMaps + 1;
            await using var discord = await FakeDiscord.StartAsync();
            var newest = FakeGetData.CreateItem(1).TimeUpdated.AddDays(30);

            // Steam's latest first, a minute apart, so none is old news beside the newest
            discord.Steam.LatestItems = Enumerable.Range(1, (int)Below).Select(id =>
            {
                var map = FakeGetData.CreateItem(id);
                map.TimeCreated = map.TimeUpdated = newest.AddMinutes(-id);
                return map;
            }).ToList();

            await discord.RunNotificationJobAsync();

            Assert.Equal(PostingLimits.DeepMaps, discord.Steam.LatestAsked!.Value.Maps);
            Assert.Null(Seen(discord, PostSource.Maps, Below));

            discord.Steam.LatestItems.RemoveAt(2);
            await discord.RunNotificationJobAsync(PostingLimits.RecentRead + TimeSpan.FromMinutes(1));

            Assert.Equal(PostingLimits.DeepMaps, discord.Steam.LatestAsked!.Value.Maps);
            Assert.Empty(SentTitles(discord));
            Assert.Equal(SeenState.Baseline, Seen(discord, PostSource.Maps, Below)?.state);
        }

        [Fact]
        public async Task MapsThatMoveUpOneAfterAnotherAreNotAnnounced()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.LatestItems.Add(MapUnderTheRead(6));
            discord.Steam.LatestItems.Add(MapUnderTheRead(7, daysOlder: 2));
            await discord.RunNotificationJobAsync();

            for (int run = 0; run < 2; run++)
            {
                discord.Steam.LatestItems.RemoveAt(0);
                await discord.RunNotificationJobAsync();
            }

            Assert.Empty(SentTitles(discord));

            discord.Steam.LatestItems.Insert(0, FakeGetData.CreateItem(NewId));
            await discord.RunNotificationJobAsync();

            Assert.Equal([NewMapTitle], SentTitles(discord));
        }

        // Most maps were made long before their last update, so the baseline and the move-up rule must go by the update
        [Fact]
        public async Task MapUpdatedBeforeTheFirstReadIsNotAnnouncedWhenItMovesUp()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.LatestItems.Add(MapUnderTheRead(6));

            foreach (var map in discord.Steam.LatestItems)
                map.TimeCreated = map.TimeUpdated.AddDays(-30);

            await discord.RunNotificationJobAsync();

            discord.Steam.LatestItems.RemoveAt(2);
            await discord.RunNotificationJobAsync();

            Assert.Empty(SentTitles(discord));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task MapThatMovesUpIsNotAnnouncedAfterAFailedFirstRead(bool readThrows)
        {
            await using var discord = await FakeDiscord.StartAsync();
            var maps = discord.Steam.LatestItems;
            maps.Add(MapUnderTheRead(6));

            if (readThrows)
                discord.Steam.Failure = new TimeoutException("Steam timed out");
            else
                discord.Steam.LatestItems = new();

            await discord.RunNotificationJobAsync();

            discord.Steam.Failure = null;
            discord.Steam.LatestItems = maps;
            await discord.RunNotificationJobAsync();

            maps.RemoveAt(2);
            await discord.RunNotificationJobAsync();

            Assert.Empty(SentTitles(discord));
        }

        [Fact]
        public async Task MapThatMovedUpIsAnnouncedOnceItIsUpdated()
        {
            await using var discord = await FakeDiscord.StartAsync();
            var map = MapUnderTheRead(6);
            discord.Steam.LatestItems.Add(map);
            await discord.RunNotificationJobAsync();

            discord.Steam.LatestItems.RemoveAt(2);
            await discord.RunNotificationJobAsync();

            Assert.Empty(SentTitles(discord));

            // It was made before the first read, so it's posted as updated
            UpdateMap(discord, map, day: NewId);
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal(["Map 6"], SentTitles(discord));
            Assert.Equal([UpdatedMapHeading], MapHeadings(discord));
        }

        // Two days older than the newest of the read, which a read minutes after the last still posts
        [Fact]
        public async Task MapMadePublicAfterTheFirstReadIsAnnounced()
        {
            await using var discord = await StartWithBaselineAsync();

            // It was hidden during the first read, so its time can be older than that read
            var map = FakeGetData.CreateItem(6);
            map.TimeCreated = map.TimeUpdated = FakeGetData.CreateItem(2).TimeUpdated;
            discord.Steam.LatestItems.Insert(0, map);
            await discord.RunNotificationJobAsync();

            Assert.Equal(["Map 6"], SentTitles(discord));
            Assert.Equal([NewMapHeading], MapHeadings(discord));
        }

        // A map made before the first read, then hidden during it
        [Fact]
        public async Task MapUpdatedWhileItWasHiddenIsAnnouncedAsUpdated()
        {
            await using var discord = await StartWithBaselineAsync();

            var map = FakeGetData.CreateItem(6);
            map.TimeCreated = FakeGetData.CreateItem(1).TimeCreated.AddDays(-1);
            map.TimeUpdated = FakeGetData.CreateItem(2).TimeUpdated;
            discord.Steam.LatestItems.Insert(0, map);
            await discord.RunNotificationJobAsync();

            Assert.Equal(["Map 6"], SentTitles(discord));
            Assert.Equal([UpdatedMapHeading], MapHeadings(discord));
        }

        // A map posted, then updated while it was hidden, and shown again two days later, when another map's update is 30 hours
        // newer: the read minutes after the last posts the update, however old Steam dates it
        [Fact]
        public async Task PostedMapUpdatedWhileHiddenAndShownADayLaterIsAnnouncedAgain()
        {
            await using var discord = await StartWithBaselineAsync();
            var map = NewMap(NewId);
            discord.Steam.LatestItems.Insert(0, map);
            await discord.RunNotificationJobAsync();

            discord.Steam.LatestItems.Remove(map);
            discord.MoveClock(TimeSpan.FromDays(2));
            map.TimeUpdated = map.TimeUpdated.AddHours(1);
            var other = NewMap(NewId + 1);
            other.TimeCreated = other.TimeUpdated = map.TimeUpdated.AddHours(30);
            discord.Steam.LatestItems.Insert(0, other);
            await discord.RunNotificationJobAsync();

            discord.Steam.LatestItems.Insert(1, map);
            await discord.RunNotificationJobAsync();

            Assert.Equal([NewMapTitle, "Map 101", NewMapTitle], SentTitles(discord));
            Assert.Equal([NewMapHeading, NewMapHeading, UpdatedMapHeading], MapHeadings(discord));
        }

        // New and updated maps

        [Fact]
        public async Task MapOfTheFirstReadIsAnnouncedWhenItIsUpdated()
        {
            await using var discord = await StartWithBaselineAsync();

            UpdateMap(discord, discord.Steam.LatestItems[2], day: NewId);
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal(["Map 3"], SentTitles(discord));
            Assert.Equal([UpdatedMapHeading], MapHeadings(discord));
        }

        [Fact]
        public async Task NewMapIsAnnouncedAgainWhenItIsUpdatedADayLater()
        {
            await using var discord = await StartWithBaselineAsync();
            var map = FakeGetData.CreateItem(NewId);
            discord.Steam.LatestItems.Insert(0, map);
            await discord.RunNotificationJobAsync();

            discord.MoveClock(PostingLimits.Cooldown);
            UpdateMap(discord, map, day: NewId + 1);
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal([NewMapTitle, NewMapTitle], SentTitles(discord));
            Assert.Equal([NewMapHeading, UpdatedMapHeading], MapHeadings(discord));
        }

        // Steam's times move by days here, but the cooldown is on the bot's clock
        [Fact]
        public async Task UpdatesWithinADayOfAPostAreAnnouncedOnceTheDayIsOver()
        {
            await using var discord = await StartWithBaselineAsync();
            var map = FakeGetData.CreateItem(NewId);
            discord.Steam.LatestItems.Insert(0, map);
            await discord.RunNotificationJobAsync();

            UpdateMap(discord, map, day: 200);
            await discord.RunNotificationJobAsync();

            // The last run before the day is over
            UpdateMap(discord, map, day: 300);
            discord.MoveClock(PostingLimits.Cooldown - TimeSpan.FromMinutes(15));
            await discord.RunNotificationJobAsync();

            Assert.Equal([NewMapHeading], MapHeadings(discord));

            // A day to the minute after the post
            await discord.RunNotificationJobAsync();

            Assert.Equal([NewMapHeading, UpdatedMapHeading], MapHeadings(discord));

            await discord.RunNotificationJobAsync();

            Assert.Equal([NewMapTitle, NewMapTitle], SentTitles(discord));
        }

        // The day counts from the last post, whether it said new or updated
        [Fact]
        public async Task UpdatedPostStartsTheCooldownToo()
        {
            await using var discord = await StartWithBaselineAsync();
            var map = discord.Steam.LatestItems[2];
            UpdateMap(discord, map, day: 200);
            await discord.RunNotificationJobAsync();

            UpdateMap(discord, map, day: 201);
            await discord.RunNotificationJobAsync();

            Assert.Equal([UpdatedMapHeading], MapHeadings(discord));

            discord.MoveClock(PostingLimits.Cooldown);
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal(["Map 3", "Map 3"], SentTitles(discord));
            Assert.Equal([UpdatedMapHeading, UpdatedMapHeading], MapHeadings(discord));
        }

        // Only a second later on Steam's clock, but a day later on the bot's
        [Fact]
        public async Task CooldownIsMeasuredOnTheBotsClock()
        {
            await using var discord = await StartWithBaselineAsync();
            var map = FakeGetData.CreateItem(NewId);
            discord.Steam.LatestItems.Insert(0, map);
            await discord.RunNotificationJobAsync();

            map.TimeUpdated = map.TimeUpdated.AddSeconds(1);
            discord.MoveClock(PostingLimits.Cooldown - TimeSpan.FromMinutes(5));
            await discord.RunNotificationJobAsync();

            Assert.Equal([NewMapHeading, UpdatedMapHeading], MapHeadings(discord));
        }

        // An update the cooldown held back isn't recorded, so it's still there to post when the map comes back
        [Fact]
        public async Task UpdateHeldBackWhileTheMapLeftTheReadIsAnnouncedWhenItComesBack()
        {
            await using var discord = await StartWithBaselineAsync();
            var map = FakeGetData.CreateItem(NewId);
            discord.Steam.LatestItems.Insert(0, map);
            await discord.RunNotificationJobAsync();

            UpdateMap(discord, map, day: 200);
            await discord.RunNotificationJobAsync();

            discord.Steam.LatestItems.Remove(map);
            discord.MoveClock(PostingLimits.Cooldown);
            await discord.RunNotificationJobAsync();

            Assert.Equal([NewMapHeading], MapHeadings(discord));

            discord.Steam.LatestItems.Insert(0, map);
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal([NewMapHeading, UpdatedMapHeading], MapHeadings(discord));
        }

        // Nothing was posted then, so a server that turns maps on gets the next update straight away
        [Fact]
        public async Task MapRecordedWhileNoServerHadMapsOnStartsNoCooldown()
        {
            await using var discord = await StartWithBaselineAsync();
            var guild = FakeGuildData.CreateGuild(FakeDiscord.ChannelId);
            guild.map_channel_id = Guild.ChannelOff;
            discord.Guilds.AllGuilds = [guild];

            var map = discord.Steam.LatestItems[2];
            UpdateMap(discord, map, day: 200);
            await discord.RunNotificationJobAsync();

            guild.map_channel_id = FakeDiscord.ChannelId;
            UpdateMap(discord, map, day: 201);
            await discord.RunNotificationJobAsync();

            Assert.Equal(["Map 3"], SentTitles(discord));
            Assert.Equal([UpdatedMapHeading], MapHeadings(discord));
        }

        [Theory]
        [InlineData(1, 0, "__**New Map!**__")]
        [InlineData(2, 0, "__**New Maps!**__")]
        [InlineData(0, 1, "__**Updated Map!**__")]
        [InlineData(0, 2, "__**Updated Maps!**__")]
        [InlineData(1, 1, "__**New & Updated Maps!**__")]
        [InlineData(2, 3, "__**New & Updated Maps!**__")]
        public async Task HeadingSaysWhatThePostHolds(int newMaps, int updatedMaps, string heading)
        {
            await using var discord = await StartWithBaselineAsync();

            // Maps of the first read, which stay in the read under the new ones, updated just before them
            for (int id = 1; id <= updatedMaps; id++)
                discord.Steam.LatestItems[id - 1].TimeUpdated = DateTime.UnixEpoch.AddDays(NewId).AddMinutes(-id);

            for (int id = NewId; id < NewId + newMaps; id++)
                discord.Steam.LatestItems.Insert(0, NewMap(id));

            await discord.RunNotificationJobAsync();

            var post = Assert.Single(Sends(discord));
            Assert.Equal(heading, post.Content);
            Assert.Equal(newMaps + updatedMaps, post.EmbedTitles.Count());

            // Only a post that has both says which each map is
            bool both = newMaps > 0 && updatedMaps > 0;
            Assert.Equal(
                Enumerable.Repeat(both ? "New Map" : null, newMaps).Concat(Enumerable.Repeat(both ? "Updated Map" : null, updatedMaps)),
                post.EmbedAuthors);
        }

        [Fact]
        public async Task EveryMapOfAPostOfBothSaysWhichItIs()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.LatestItems = Enumerable.Range(1, 5).Select(id => MapEmbedFlowTests.LongMap(id)).ToList();
            await discord.RunNotificationJobAsync();

            discord.Steam.LatestItems[0].TimeUpdated = DateTime.UnixEpoch.AddDays(NewId).AddMinutes(-2);
            discord.Steam.LatestItems[1].TimeUpdated = DateTime.UnixEpoch.AddDays(NewId).AddMinutes(-1);

            for (int id = NewId; id < NewId + 3; id++)
                discord.Steam.LatestItems.Insert(0, NewLongMap(id));

            await discord.RunNotificationJobAsync();

            var messages = Sends(discord);

            Assert.Equal(2, messages.Count);
            Assert.All(messages, message => Assert.InRange(message.EmbedsLength, 1, EmbedBuilder.MaxEmbedLength));
            Assert.Equal(["Map 102", "Map 101", "Map 100", "Map 1", "Map 2"], SentTitles(discord));
            Assert.Equal(["New Map", "New Map", "New Map", "Updated Map", "Updated Map"], messages.SelectMany(message => message.EmbedAuthors));
            Assert.Equal("__**New & Updated Maps!**__", messages[0].Content);
            Assert.True(string.IsNullOrEmpty(messages[1].Content));
        }

        // Each guild's channels

        // Items that no guild wants aren't a failure, and nothing waits for it
        [Theory]
        [InlineData("armor", ArmorTitle)]
        [InlineData("map", NewMapTitle)]
        [InlineData("lobby", LobbyTitle)]
        public async Task KindThatIsTurnedOffIsSkippedWithoutAnError(string kind, string title)
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Guilds.AllGuilds = [new Guild
            {
                guild_id = FakeDiscord.GuildId,
                armor_channel_id = kind == "armor" ? Guild.ChannelOff : FakeDiscord.ChannelId,
                map_channel_id = kind == "map" ? Guild.ChannelOff : FakeDiscord.ChannelId,
                lobby_channel_id = kind == "lobby" ? Guild.ChannelOff : FakeDiscord.ChannelId
            }];
            AddNewItems(discord);

            for (int run = 0; run < 3; run++)
                await discord.RunNotificationJobAsync();

            Assert.Equal(new[] { ArmorTitle, NewMapTitle, LobbyTitle }.Where(sent => sent != title), SentTitles(discord));
            Assert.DoesNotContain($"{kind} notification", discord.ConsoleText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, discord.Store.Queue.Count);
        }

        // Otherwise the first server to turn lobbies on would get the ones that opened while no server had them on, as new
        [Fact]
        public async Task KindThatIsTurnedOnGetsOnlyWhatIsNewFromThen()
        {
            await using var discord = await StartWithBaselineAsync();
            var guild = FakeGuildData.CreateGuild(FakeDiscord.ChannelId);
            guild.lobby_channel_id = Guild.ChannelOff;
            discord.Guilds.AllGuilds = [guild];
            discord.Steam.Lobbies.Add(Lobby(1));
            await discord.RunNotificationJobAsync();

            guild.lobby_channel_id = FakeDiscord.ChannelId;
            discord.Steam.Lobbies.Add(Lobby(NewId));
            await discord.RunNotificationJobAsync();

            var announced = Assert.Single(Sends(discord)).EmbedDescriptions.Single();
            Assert.Contains($"Host {NewId}", announced);
            Assert.DoesNotContain("Host 1`", announced);
        }

        // A row's channel must be one of its own server's: the bot is in that server too, so the channel is in the cache, but
        // the post would go to another server. No channel is looked up over Discord's API either, and it's a refusal.
        [Fact]
        public async Task RowNamingAnotherServersChannelGetsNoPost()
        {
            await using var discord = await StartWithBaselineAsync();
            await discord.JoinGuildAsync($"[{FakeDiscord.BotMemberJson(FakeDiscord.BotRoleId)}]");
            var row = FakeGuildData.CreateGuild(FakeDiscord.ChannelId);
            row.guild_id = FakeDiscord.JoinedGuildId;
            discord.Guilds.AllGuilds = [row];
            AddNewItems(discord);

            await discord.RunNotificationJobAsync();

            Assert.DoesNotContain(discord.Steps, step => step.Name == SendStep && step.SentTo == FakeDiscord.ChannelId);
            Assert.DoesNotContain("get channel", discord.StepNames);

            foreach (var kind in new[] { "armor", "map", "lobby" })
                Assert.Contains($"Couldn't send the {kind} notification to channel {FakeDiscord.ChannelId} of guild {FakeDiscord.JoinedGuildId}, because that channel isn't in that server",
                    discord.ConsoleText);

            Assert.All(discord.Store.Deliveries, delivery => Assert.Equal((DeliveryState.Failed, DeliveryReasons.NoChannel), (delivery.state, delivery.reason)));
        }

        // A server the bot isn't in, or left, keeps its row on the website until it's removed while the bot runs: it's owed
        // nothing, so nothing is queued or tried for it
        [Fact]
        public async Task RowOfAServerTheBotIsntInGetsNoPost()
        {
            await using var discord = await StartWithBaselineAsync();
            var row = FakeGuildData.CreateGuild(FakeDiscord.ChannelId);
            row.guild_id = 9999;
            discord.Guilds.AllGuilds = [row];
            AddNewItems(discord);

            await discord.RunNotificationJobAsync();

            Assert.DoesNotContain(SendStep, discord.StepNames);
            Assert.DoesNotContain("get channel", discord.StepNames);
            Assert.Empty(discord.Store.Queue);
        }

        // Discord.Net keeps the channels of a server in an outage, but Discord reports it unavailable. Its items wait for it,
        // with no failure recorded.
        [Fact]
        public async Task OnlyServerWithPostsOnGetsThemOnceItsBack()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            await discord.MakeGuildUnavailableAsync();

            await discord.RunNotificationJobAsync();

            Assert.DoesNotContain(SendStep, discord.StepNames);
            Assert.DoesNotContain("get channel", discord.StepNames);
            Assert.Contains($"Couldn't send the map notification to channel {FakeDiscord.ChannelId} of guild {FakeDiscord.GuildId}, because Discord reports that server unavailable",
                discord.ConsoleText);

            await discord.MakeGuildAvailableAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, NewMapTitle, LobbyTitle], SentTitles(discord));
        }

        // Until the gateway's first READY since the start, Discord.Net has none of the bot's servers, so no post can be checked
        // against its server. Those runs find and post nothing, and the items go out once READY comes.
        [Fact]
        public async Task RunsBeforeTheFirstReadyWaitForIt()
        {
            await using var before = await StartWithBaselineAsync();
            await using var discord = await before.RestartAsync(TimeSpan.FromMinutes(1), holdReady: true);

            Assert.Equal(WaitingDetail, await discord.RunNotificationJobAsync());

            AddNewItems(discord);

            for (var run = 0; run < 3; run++)
                Assert.Equal(WaitingDetail, await discord.RunNotificationJobAsync());

            Assert.DoesNotContain(SendStep, discord.StepNames);
            Assert.Empty(discord.Store.Queue);

            await discord.ReleaseReadyAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, NewMapTitle, LobbyTitle], SentTitles(discord));
        }

        // From a READY until Discord sends each server, at a start or a new session, Discord.Net has nothing of the servers but
        // their IDs. When the gateway drops in that window and stays down while Discord's API still works, as in an incident,
        // the runs wait as they do before the first READY. The items go out once Discord has sent a server.
        [Fact]
        public async Task RunsWhileDiscordHasSentNoneOfTheServersWaitForThem()
        {
            await using var before = await StartWithBaselineAsync();
            await using var discord = await before.RestartAsync(TimeSpan.FromMinutes(1), unavailableGuilds: [FakeDiscord.GuildId]);
            await discord.StopGatewayAsync();

            Assert.NotNull(discord.Client.CurrentUser);
            Assert.Null(discord.Client.GetGuild(FakeDiscord.GuildId).Name);

            AddNewItems(discord);

            for (var run = 0; run < 3; run++)
                Assert.Equal(WaitingDetail, await discord.RunNotificationJobAsync());

            Assert.DoesNotContain(SendStep, discord.StepNames);
            Assert.DoesNotContain("that channel isn't in that server", discord.ConsoleText);

            await discord.StartNewSessionAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, NewMapTitle, LobbyTitle], SentTitles(discord));
        }

        // A server Discord hasn't sent, while it has sent others, gets no post, and the log says why
        [Fact]
        public async Task ServerDiscordHasntSentGetsNoPostAndTheLogSaysWhy()
        {
            await using var discord = await FakeDiscord.StartAsync(unavailableGuilds: [FakeDiscord.GuildId]);
            await discord.JoinGuildAsync($"[{FakeDiscord.BotMemberJson(FakeDiscord.BotRoleId)}]");
            await discord.RunNotificationJobAsync();
            await discord.StopGatewayAsync();

            discord.Guilds.AllGuilds = [FakeGuildData.CreateGuild(FakeDiscord.ChannelId), SecondServer()];
            AddNewItems(discord);

            await discord.RunNotificationJobAsync();

            // The other server's posts, after its welcome
            Assert.Equal([ArmorTitle, NewMapTitle, LobbyTitle], SentTitles(discord));
            Assert.DoesNotContain(discord.Steps, step => step.Name == SendStep && step.SentTo == FakeDiscord.ChannelId);
            Assert.Contains($"Couldn't send the armor notification to channel {FakeDiscord.ChannelId} of guild {FakeDiscord.GuildId}, because Discord hasn't sent that server yet",
                discord.ConsoleText);
            Assert.DoesNotContain("that channel isn't in that server", discord.ConsoleText);
        }

        // A channel the cache doesn't have, such as one that was deleted, isn't asked of Discord's API, which a lookup of the
        // channel alone would do: the row gets no post, and it's a refusal
        [Fact]
        public async Task ChannelTheCacheDoesntHaveIsntLookedUp()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Guilds.AllGuilds = [FakeGuildData.CreateGuild(FakeDiscord.DeletedChannelId)];
            AddNewItems(discord);

            await discord.RunNotificationJobAsync();

            Assert.DoesNotContain("get channel", discord.StepNames);
            Assert.DoesNotContain(SendStep, discord.StepNames);
            Assert.Contains($"Couldn't send the lobby notification to channel {FakeDiscord.DeletedChannelId} of guild {FakeDiscord.GuildId}, because that channel isn't in that server",
                discord.ConsoleText);
        }

        // While the bot's own gateway is down, as during a reconnect, Discord.Net shows every server as unavailable, which isn't
        // Discord's outage: the posts still go through Discord's API
        [Fact]
        public async Task PostsGoWhileTheGatewayIsDown()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            await discord.StopGatewayAsync();

            Assert.False(discord.Client.GetGuild(FakeDiscord.GuildId).IsConnected);

            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, NewMapTitle, LobbyTitle], SentTitles(discord));
        }

        [Fact]
        public async Task NotificationThatCanNotBeSentIsLoggedWithItsKindAndGuild()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            discord.FailStep = SendStep;

            await discord.RunNotificationJobAsync();

            Assert.Contains($"Couldn't send the armor notification to channel {FakeDiscord.ChannelId} of guild {FakeDiscord.GuildId}: discord_permissions (HTTP 403, Discord 50013)", discord.ConsoleText);
            Assert.Contains($"Couldn't send the map notification to channel {FakeDiscord.ChannelId} of guild {FakeDiscord.GuildId}", discord.ConsoleText);
            Assert.Contains($"Couldn't send the lobby notification to channel {FakeDiscord.ChannelId} of guild {FakeDiscord.GuildId}", discord.ConsoleText);
        }
    }
}
