using DataAccessLibrary.Discord.Models;
using Discord;
using MinerBot_2._0.Jobs;
using Steam.Models;
using SteamWebAPIAccess.Models;

namespace MinerBot.Tests
{
    // Each run is a new job object on the real Discord.Net pipeline, the same as JobHandler runs it
    [Collection("Discord")]
    public class NotificationJobTests
    {
        const string ArmorTitle = "New Armor Styles:";
        const string LobbyTitle = "__NEW__ Murder Miners Public Lobbies";
        const string SendStep = "send message";

        const int NewId = 100;
        const string NewMapTitle = "Map 100";

        const string NewMapHeading = "__**New Map!**__";
        const string UpdatedMapHeading = "__**Updated Map!**__";

        static LobbyInfo Lobby(ulong id, uint version = 105) => new()
        {
            LobbyId = id,
            VersionNumber = version,
            HostName = $"Host {id}",
            MapName = "Map",
            Modes = "Mode",
            CurrentPlayers = 1,
            MaxPlayers = 8,
            JoinURL = "https://example.com/join"
        };

        static List<string> SentTitles(FakeDiscord discord) => discord.Steps
            .Where(step => step.Name == SendStep)
            .SelectMany(step => step.EmbedTitles)
            .ToList();

        // Only the first message of a map post has text
        static List<string> MapHeadings(FakeDiscord discord) => discord.Steps
            .Where(step => step.Name == SendStep && !string.IsNullOrEmpty(step.Content))
            .Select(step => step.Content!)
            .ToList();

        // An update puts a map on top of Steam's list
        static void UpdateMap(FakeDiscord discord, PublishedFileDetailsModel map, int day)
        {
            map.TimeUpdated = DateTime.UnixEpoch.AddDays(day);
            discord.Steam.LatestItems.Remove(map);
            discord.Steam.LatestItems.Insert(0, map);
        }

        static void AddNewItems(FakeDiscord discord)
        {
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.Steam.LatestItems.Insert(0, FakeGetData.CreateItem(NewId));
            discord.Steam.Lobbies.Add(Lobby(NewId));
        }

        static async Task<FakeDiscord> StartWithBaselineAsync()
        {
            var discord = await FakeDiscord.StartAsync();
            await discord.RunNotificationJobAsync();
            return discord;
        }

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
            Assert.Contains("Armor 100", discord.Steps.First(step => step.Name == SendStep).EmbedDescriptions.Single());
        }

        [Fact]
        public async Task EveryGuildGetsTheNewItems()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Guilds.AllGuilds.Add(FakeGuildData.CreateGuild(FakeDiscord.ChannelId));
            AddNewItems(discord);

            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, NewMapTitle, LobbyTitle, ArmorTitle, NewMapTitle, LobbyTitle], SentTitles(discord));
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

            var announced = Assert.Single(discord.Steps, step => step.Name == SendStep).EmbedDescriptions.Single();
            Assert.Contains("__Update 41 (v105):__\n\n- **Host:** `Host 2`", announced);
            Assert.Contains($"__{heading}:__\n\n- **Host:** `Host 1`", announced);
        }

        [Fact]
        public async Task LobbiesThatPassTheEmbedLimitAreStillAnnounced()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Steam.Lobbies = Enumerable.Range(1, 100).Select(id => LobbyListLimitTests.Lobby(id)).ToList();

            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            var announced = Assert.Single(discord.Steps, step => step.Name == SendStep);
            Assert.Contains("more lobbies", announced.EmbedDescriptions.Single());
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
            discord.Steam.LatestItems = Enumerable.Range(NewId, 5).Select(id => MapEmbedFlowTests.LongMap(id)).ToList();

            await discord.RunNotificationJobAsync();

            var messages = discord.Steps.Where(step => step.Name == SendStep).ToList();

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

            var announced = Assert.Single(discord.Steps, step => step.Name == SendStep).EmbedDescriptions.Single();
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
        public async Task EmptyLobbyListIsAGoodFirstRead()
        {
            await using var discord = await StartWithBaselineAsync();

            discord.Steam.Lobbies.Add(Lobby(NewId));
            await discord.RunNotificationJobAsync();

            Assert.Equal([LobbyTitle], SentTitles(discord));
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

        [Fact]
        public async Task EachSourceHasItsOwnBaseline()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.Lobbies.Add(Lobby(1));
            discord.Steam.LobbiesAvailable = false;
            var maps = discord.Steam.LatestItems;
            discord.Steam.LatestItems = new();
            await discord.RunNotificationJobAsync();

            // Armor has its baseline by now, but the other two get theirs on this run
            discord.Steam.LobbiesAvailable = true;
            discord.Steam.LatestItems = maps;
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle], SentTitles(discord));
        }

        // Keeping what wasn't delivered

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

        [Fact]
        public async Task FailedGuildListKeepsEverythingForTheNextRun()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            discord.Guilds.Failure = new TimeoutException("Database timed out");

            await Assert.ThrowsAsync<TimeoutException>(discord.RunNotificationJobAsync);

            discord.Guilds.Failure = null;
            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, NewMapTitle, LobbyTitle], SentTitles(discord));
        }

        [Fact]
        public async Task FailedGuildListKeepsTheBaselinesOfThatRun()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.Lobbies.Add(Lobby(1));
            discord.Guilds.Failure = new TimeoutException("Database timed out");

            await Assert.ThrowsAsync<TimeoutException>(discord.RunNotificationJobAsync);

            discord.Guilds.Failure = null;
            await discord.RunNotificationJobAsync();

            Assert.Empty(SentTitles(discord));
        }

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

        [Fact]
        public async Task BadEmbedKeepsItsBatchAndLeavesTheOtherSourcesAlone()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            discord.Steam.LatestItems[0].Title = new string('a', 300);

            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, LobbyTitle], SentTitles(discord));

            discord.Steam.LatestItems[0].Title = NewMapTitle;
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, LobbyTitle, NewMapTitle], SentTitles(discord));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task BatchThatReachedOneGuildIsNotSentAgain(bool brokenGuildFirst)
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Guilds.AllGuilds.Insert(brokenGuildFirst ? 0 : 1, FakeGuildData.CreateGuild(FakeDiscord.DeletedChannelId));
            AddNewItems(discord);

            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal([ArmorTitle, NewMapTitle, LobbyTitle], SentTitles(discord));
        }

        [Fact]
        public async Task BatchIsStillSentOnTheLastTry()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            discord.FailStep = SendStep;

            for (int run = 1; run < NotificationState.MaxFailedRuns; run++)
                await discord.RunNotificationJobAsync();

            discord.FailStep = null;
            var refused = discord.Steps.Count;
            await discord.RunNotificationJobAsync();

            Assert.Equal(3, discord.Steps.Skip(refused).Count(step => step.Name == SendStep));
            Assert.DoesNotContain("Gave up", discord.ConsoleText);
        }

        [Fact]
        public async Task BatchThatKeepsFailingIsDroppedSoItCanNotBlockItsSource()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            discord.FailStep = SendStep;

            for (int run = 0; run < NotificationState.MaxFailedRuns; run++)
                await discord.RunNotificationJobAsync();

            Assert.Contains("Gave up on 1 new armor styles", discord.ConsoleText);
            Assert.Contains("Gave up on 1 new or updated maps", discord.ConsoleText);
            Assert.Contains("Gave up on 1 new lobbies", discord.ConsoleText);

            discord.FailStep = null;
            var refused = discord.Steps.Count;
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId + 1));

            await discord.RunNotificationJobAsync();

            var sent = Assert.Single(discord.Steps.Skip(refused), step => step.Name == SendStep);
            Assert.Equal([ArmorTitle], sent.EmbedTitles);
            Assert.Contains("Armor 101", sent.EmbedDescriptions.Single());
            Assert.DoesNotContain("Armor 100", sent.EmbedDescriptions.Single());
        }

        [Fact]
        public async Task NewItemIsNotDroppedWithAnOlderOneThatKeepsFailing()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId));
            discord.FailStep = SendStep;

            for (int run = 1; run < NotificationState.MaxFailedRuns; run++)
                await discord.RunNotificationJobAsync();

            // The older armor gets its last try on this run, and the newer one its first
            discord.Armor.AllArmor.Add(FakeArmorData.CreateArmor(NewId + 1));
            await discord.RunNotificationJobAsync();

            Assert.Contains("Gave up on 1 new armor styles", discord.ConsoleText);

            discord.FailStep = null;
            var refused = discord.Steps.Count;
            await discord.RunNotificationJobAsync();

            var sent = Assert.Single(discord.Steps.Skip(refused), step => step.Name == SendStep).EmbedDescriptions.Single();
            Assert.Contains("Armor 101", sent);
            Assert.DoesNotContain("Armor 100", sent);
        }

        // A map given up on is found again when it's updated, and that update gets its own runs, as a new item does
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task UpdateOfAMapThatWasGivenUpGetsRunsOfItsOwn(bool readFailsInBetween)
        {
            await using var discord = await StartWithBaselineAsync();
            var map = discord.Steam.LatestItems[2];
            UpdateMap(discord, map, day: 200);
            discord.FailStep = SendStep;

            for (int run = 0; run < NotificationState.MaxFailedRuns; run++)
                await discord.RunNotificationJobAsync();

            // A failed read in between changes no counts
            if (readFailsInBetween)
            {
                discord.Steam.Failure = new TimeoutException("Steam timed out");
                await discord.RunNotificationJobAsync();
                discord.Steam.Failure = null;
            }

            // Updated again, and the next run still can't send
            UpdateMap(discord, map, day: 201);
            await discord.RunNotificationJobAsync();

            discord.FailStep = null;
            var refused = discord.Steps.Count;
            await discord.RunNotificationJobAsync();

            Assert.Single(discord.ConsoleText.Split("Gave up on 1 new or updated maps").Skip(1));
            var sent = Assert.Single(discord.Steps.Skip(refused), step => step.Name == SendStep);
            Assert.Equal(["Map 3"], sent.EmbedTitles);
            Assert.Equal(UpdatedMapHeading, sent.Content);
        }

        [Fact]
        public async Task ItemThatLeftItsSourceStartsAgainIfItComesBack()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Steam.Lobbies.Add(Lobby(NewId));
            discord.FailStep = SendStep;

            for (int run = 1; run < NotificationState.MaxFailedRuns; run++)
                await discord.RunNotificationJobAsync();

            discord.Steam.Lobbies.Clear();
            await discord.RunNotificationJobAsync();

            discord.Steam.Lobbies.Add(Lobby(NewId));
            await discord.RunNotificationJobAsync();

            Assert.DoesNotContain("Gave up", discord.ConsoleText);
        }

        // Maps count their runs apart from SeenItems
        [Fact]
        public async Task MapThatLeftTheReadStartsAgainIfItComesBack()
        {
            await using var discord = await StartWithBaselineAsync();
            var map = FakeGetData.CreateItem(NewId);
            discord.Steam.LatestItems.Insert(0, map);
            discord.FailStep = SendStep;

            for (int run = 1; run < NotificationState.MaxFailedRuns; run++)
                await discord.RunNotificationJobAsync();

            discord.Steam.LatestItems.Remove(map);
            await discord.RunNotificationJobAsync();

            discord.Steam.LatestItems.Insert(0, map);
            await discord.RunNotificationJobAsync();

            Assert.DoesNotContain("Gave up", discord.ConsoleText);
        }

        [Fact]
        public async Task MapsOfAMessageThatFailedAreSentWithoutRepeatingTheOthers()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Steam.LatestItems = Enumerable.Range(NewId, 5).Select(id => MapEmbedFlowTests.LongMap(id)).ToList();
            discord.FailStep = SendStep;
            discord.FailStepAfter = 1;

            await discord.RunNotificationJobAsync();

            var firstRun = discord.Steps.Where(step => step.Name == SendStep).ToList();
            Assert.Equal(2, firstRun.Count);

            discord.FailStep = null;
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            var delivered = firstRun.Take(1).Concat(discord.Steps.Where(step => step.Name == SendStep).Skip(2));

            Assert.Equal(discord.Steam.LatestItems.Select(map => map.Title), delivered.SelectMany(step => step.EmbedTitles));
        }

        [Fact]
        public async Task MapsOfTheGuildThatGotTheMostAreRecorded()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Guilds.AllGuilds.Add(FakeGuildData.CreateGuild(FakeDiscord.ChannelId));
            discord.Steam.LatestItems = Enumerable.Range(NewId, 5).Select(id => MapEmbedFlowTests.LongMap(id)).ToList();
            discord.FailStep = SendStep;
            discord.FailStepAfter = 3;

            // The first guild gets both messages, and the second one only the first
            await discord.RunNotificationJobAsync();

            var firstRun = discord.Steps.Count;
            discord.FailStep = null;
            await discord.RunNotificationJobAsync();

            Assert.DoesNotContain(discord.Steps.Skip(firstRun), step => step.Name == SendStep);
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

            Assert.Equal(3, discord.Notifications.Armor.Count);
            Assert.Equal(6, discord.Notifications.Maps.Count);
            Assert.Equal(2, discord.Notifications.Lobbies.Count);
        }

        [Fact]
        public async Task ItemFoundTwiceInOneReadIsAnnouncedOnce()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Steam.Lobbies.Add(Lobby(NewId));
            discord.Steam.Lobbies.Add(Lobby(NewId));

            await discord.RunNotificationJobAsync();

            var announced = Assert.Single(discord.Steps, step => step.Name == SendStep).EmbedDescriptions.Single();
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

            // Then the map is in the part of the batch that reached no guild, where a second copy would make Commit throw
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

            Assert.Equal([NewMapTitle], SentTitles(discord));
        }

        // Maps from under the five of a read

        // Steam ranks maps by their last update, so a map under a read is no newer than the oldest map of that read
        static PublishedFileDetailsModel MapUnderTheRead(int id, int daysOlder = 1)
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

        [Fact]
        public async Task MapMadePublicAfterTheFirstReadIsAnnounced()
        {
            await using var discord = await StartWithBaselineAsync();

            // It was hidden during the first read, so its time can be older than that read
            var map = FakeGetData.CreateItem(6);
            map.TimeCreated = map.TimeUpdated = FakeGetData.CreateItem(3).TimeUpdated;
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
            map.TimeUpdated = FakeGetData.CreateItem(3).TimeUpdated;
            discord.Steam.LatestItems.Insert(0, map);
            await discord.RunNotificationJobAsync();

            Assert.Equal(["Map 6"], SentTitles(discord));
            Assert.Equal([UpdatedMapHeading], MapHeadings(discord));
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

            discord.MoveClock(SeenMaps.Cooldown);
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
            discord.MoveClock(SeenMaps.Cooldown - TimeSpan.FromMinutes(15));
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

            discord.MoveClock(SeenMaps.Cooldown);
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
            discord.MoveClock(SeenMaps.Cooldown - TimeSpan.FromMinutes(5));
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
            discord.MoveClock(SeenMaps.Cooldown);
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

        // It wasn't posted either, so its next update goes out at once
        [Fact]
        public async Task MapGivenUpOnStartsNoCooldown()
        {
            await using var discord = await StartWithBaselineAsync();
            var map = discord.Steam.LatestItems[2];
            UpdateMap(discord, map, day: 200);
            discord.FailStep = SendStep;

            for (int run = 0; run < NotificationState.MaxFailedRuns; run++)
                await discord.RunNotificationJobAsync();

            Assert.Contains("Gave up on 1 new or updated maps", discord.ConsoleText);

            discord.FailStep = null;
            var refused = discord.Steps.Count;
            UpdateMap(discord, map, day: 201);
            await discord.RunNotificationJobAsync();

            var sent = Assert.Single(discord.Steps.Skip(refused), step => step.Name == SendStep);
            Assert.Equal(["Map 3"], sent.EmbedTitles);
            Assert.Equal(UpdatedMapHeading, sent.Content);
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

            // Maps of the first read, which stay in the read under the new ones
            for (int id = 1; id <= updatedMaps; id++)
                discord.Steam.LatestItems[id - 1].TimeUpdated = DateTime.UnixEpoch.AddDays(200 + id);

            for (int id = NewId; id < NewId + newMaps; id++)
                discord.Steam.LatestItems.Insert(0, FakeGetData.CreateItem(id));

            await discord.RunNotificationJobAsync();

            var post = Assert.Single(discord.Steps, step => step.Name == SendStep);
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

            discord.Steam.LatestItems[0].TimeUpdated = DateTime.UnixEpoch.AddDays(200);
            discord.Steam.LatestItems[1].TimeUpdated = DateTime.UnixEpoch.AddDays(201);

            for (int id = NewId; id < NewId + 3; id++)
                discord.Steam.LatestItems.Insert(0, MapEmbedFlowTests.LongMap(id));

            await discord.RunNotificationJobAsync();

            var messages = discord.Steps.Where(step => step.Name == SendStep).ToList();

            Assert.Equal(2, messages.Count);
            Assert.All(messages, message => Assert.InRange(message.EmbedsLength, 1, EmbedBuilder.MaxEmbedLength));
            Assert.Equal(["Map 102", "Map 101", "Map 100", "Map 1", "Map 2"], SentTitles(discord));
            Assert.Equal(["New Map", "New Map", "New Map", "Updated Map", "Updated Map"], messages.SelectMany(message => message.EmbedAuthors));
            Assert.Equal("__**New & Updated Maps!**__", messages[0].Content);
            Assert.True(string.IsNullOrEmpty(messages[1].Content));
        }

        // Each guild's channels

        // For as many runs as it takes to give up on items that reached no guild. Items that no guild wants aren't a failure.
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

            for (int run = 0; run < NotificationState.MaxFailedRuns; run++)
                await discord.RunNotificationJobAsync();

            Assert.Equal(new[] { ArmorTitle, NewMapTitle, LobbyTitle }.Where(sent => sent != title), SentTitles(discord));
            Assert.DoesNotContain($"{kind} notification", discord.ConsoleText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Gave up", discord.ConsoleText);
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

            var announced = Assert.Single(discord.Steps, step => step.Name == SendStep).EmbedDescriptions.Single();
            Assert.Contains($"Host {NewId}", announced);
            Assert.DoesNotContain("Host 1`", announced);
        }

        [Fact]
        public async Task NotificationThatCanNotBeSentIsLoggedWithItsKindAndGuild()
        {
            await using var discord = await StartWithBaselineAsync();
            AddNewItems(discord);
            discord.FailStep = SendStep;

            await discord.RunNotificationJobAsync();

            Assert.Contains($"Couldn't send the armor notification to channel {FakeDiscord.ChannelId} of guild {FakeDiscord.GuildId}", discord.ConsoleText);
            Assert.Contains($"Couldn't send the map notification to channel {FakeDiscord.ChannelId} of guild {FakeDiscord.GuildId}", discord.ConsoleText);
            Assert.Contains($"Couldn't send the lobby notification to channel {FakeDiscord.ChannelId} of guild {FakeDiscord.GuildId}", discord.ConsoleText);
        }
    }
}
