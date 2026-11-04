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

        [Fact]
        public async Task LobbyOfAnUnsupportedVersionIsNotAnnounced()
        {
            await using var discord = await StartWithBaselineAsync();
            discord.Steam.Lobbies.Add(Lobby(1, version: 80));
            discord.Steam.Lobbies.Add(Lobby(2));

            await discord.RunNotificationJobAsync();

            var announced = Assert.Single(discord.Steps, step => step.Name == SendStep).EmbedDescriptions.Single();
            Assert.Contains("Host 2", announced);
            Assert.DoesNotContain("Host 1", announced);
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
            Assert.Equal("__**New & Updated Map(s)!**__", messages[0].Content);
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
            Assert.Contains("Gave up on 1 new maps", discord.ConsoleText);
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
            map.TimeUpdated = FakeGetData.CreateItem(1).TimeUpdated.AddDays(-daysOlder);
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

            // An update puts a map on top of Steam's list
            map.TimeUpdated = FakeGetData.CreateItem(NewId).TimeUpdated;
            discord.Steam.LatestItems.Remove(map);
            discord.Steam.LatestItems.Insert(0, map);
            await discord.RunNotificationJobAsync();
            await discord.RunNotificationJobAsync();

            Assert.Equal(["Map 6"], SentTitles(discord));
        }

        [Fact]
        public async Task MapMadePublicAfterTheFirstReadIsAnnounced()
        {
            await using var discord = await StartWithBaselineAsync();

            // It was hidden during the first read, so its time can be older than that read
            var map = FakeGetData.CreateItem(6);
            map.TimeUpdated = FakeGetData.CreateItem(3).TimeUpdated;
            discord.Steam.LatestItems.Insert(0, map);
            await discord.RunNotificationJobAsync();

            Assert.Equal(["Map 6"], SentTitles(discord));
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
