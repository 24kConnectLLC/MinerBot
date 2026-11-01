using System.Text.RegularExpressions;
using Discord;
using Microsoft.Extensions.Caching.Memory;
using MinerBot_2._0.Services;
using SteamWebAPIAccess.Models;

namespace MinerBot.Tests
{
    public class LobbyListLimitTests
    {
        const string RowStart = "- **Host:**";

        readonly FakeGetData steam = new(_ => { });
        readonly SteamAPIService service;

        public LobbyListLimitTests()
        {
            service = new SteamAPIService(steam, new MemoryCache(new MemoryCacheOptions()));
        }

        // Rows of the same size as real ones, the host name is the only part that varies
        internal static LobbyInfo Lobby(int id, uint version = 105, int hostLength = 12) => new()
        {
            LobbyId = (ulong)id,
            VersionNumber = version,
            HostName = $"{id} ".PadRight(hostLength, 'a'),
            MapName = "Skyline X by Murder Miners 12",
            Modes = "Team Murder Match",
            CurrentPlayers = 3,
            MaxPlayers = 8,
            JoinURL = $"https://murderminershub.com/join-lobby?lobbyId=109775241234567890&ownerId=76561198012345678"
        };

        static List<LobbyInfo> Lobbies(int firstId, int count, uint version = 105) =>
            Enumerable.Range(firstId, count).Select(id => Lobby(id, version)).ToList();

        static int Rows(string description) => description.Split(RowStart).Length - 1;

        static int Hidden(string description)
        {
            var match = Regex.Match(description, @"\.\.\.and (\d+) more lobb");
            return match.Success ? int.Parse(match.Groups[1].Value) : 0;
        }

        async Task<string> Description() => (await service.LobbyListEmbed()).Description;

        [Fact]
        public async Task LobbiesThatFitShowNoEnding()
        {
            steam.Lobbies = Lobbies(1, 5);

            var description = await Description();

            Assert.Equal(5, Rows(description));
            Assert.DoesNotContain("more lobb", description);
        }

        [Theory]
        [InlineData(20)]
        [InlineData(50)]
        [InlineData(1000)]
        public async Task ManyLobbiesFitInOneEmbed(int count)
        {
            steam.Lobbies = Lobbies(1, count);

            var description = await Description();

            Assert.InRange(description.Length, 1, EmbedBuilder.MaxDescriptionLength);
            Assert.True(Hidden(description) > 0);
            Assert.Equal(count, Rows(description) + Hidden(description));
        }

        [Fact]
        public async Task ListEndsWithTheCountAndTheLink()
        {
            steam.Lobbies = Lobbies(1, 50);

            var description = await Description();

            Assert.EndsWith(
                $"\n\n...and {Hidden(description)} more lobbies\n\n[See them all in Murder Miners](https://murderminershub.com/open-murder-miners)",
                description);
        }

        [Fact]
        public async Task OneHiddenLobbyIsCalledALobby()
        {
            for (int count = 1; ; count++)
            {
                steam.Lobbies = Lobbies(1, count);

                var description = await Description();

                if (Hidden(description) == 0)
                    continue;

                Assert.Contains("...and 1 more lobby\n", description);
                return;
            }
        }

        [Fact]
        public async Task AllVersionGroupsShareTheBudget()
        {
            // Each group fits by itself
            steam.Lobbies = [.. Lobbies(1, 15), .. Lobbies(101, 15, version: 92), .. Lobbies(201, 15, version: 87)];

            var description = await Description();

            Assert.InRange(description.Length, 1, EmbedBuilder.MaxDescriptionLength);
            Assert.Equal(45, Rows(description) + Hidden(description));
            Assert.Contains("__Update 41:__", description);
            Assert.Contains("__Update 40:__", description);
            Assert.DoesNotContain("__Update 39:__", description);
        }

        [Fact]
        public async Task LaterGroupsCountAsHidden()
        {
            steam.Lobbies = [.. Lobbies(1, 100), .. Lobbies(201, 5, version: 92)];

            var description = await Description();

            Assert.DoesNotContain("__Update 40:__", description);
            Assert.Equal(105, Rows(description) + Hidden(description));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(12)]
        [InlineData(32)]
        public async Task EveryHeadingHasARowUnderIt(int hostLength)
        {
            for (int firstGroup = 1; firstGroup <= 40; firstGroup++)
            {
                steam.Lobbies =
                [
                    .. Enumerable.Range(1, firstGroup).Select(id => Lobby(id, hostLength: hostLength)),
                    .. Enumerable.Range(101, 40).Select(id => Lobby(id, version: 92, hostLength: hostLength))
                ];

                var description = await Description();

                Assert.InRange(description.Length, 1, EmbedBuilder.MaxDescriptionLength);
                Assert.DoesNotMatch(@":__\n\n(\.\.\.and|$)", description);
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public async Task RowsOfAnySizeStayInOrderAndWithinTheLimit(int seed)
        {
            var random = new Random(seed);

            for (int run = 0; run < 200; run++)
            {
                steam.Lobbies = Enumerable.Range(1, 60).Select(id => Lobby(id, hostLength: random.Next(1, 33))).ToList();

                var description = await Description();
                var shown = Regex.Matches(description, @"\*\*Host:\*\* `(\d+) ?a*`").Select(match => int.Parse(match.Groups[1].Value));

                Assert.InRange(description.Length, 1, EmbedBuilder.MaxDescriptionLength);
                Assert.Equal(Enumerable.Range(1, Rows(description)), shown);
                Assert.Equal(60, Rows(description) + Hidden(description));
            }
        }

        [Fact]
        public async Task LobbiesInNoGroupAreNotCounted()
        {
            const uint versionWithNoGroup = 10;
            steam.Lobbies = [.. Lobbies(1, 50), .. Lobbies(101, 5, versionWithNoGroup)];

            var description = await Description();

            Assert.Equal(50, Rows(description) + Hidden(description));
        }

        [Fact]
        public async Task OlderGroupsAreNotCountedWhenTheyAreNotShown()
        {
            service.shouldShowPrevUpdate = false;
            steam.Lobbies = [.. Lobbies(1, 50), .. Lobbies(101, 5, version: 92)];

            var description = await Description();

            Assert.InRange(description.Length, 1, EmbedBuilder.MaxDescriptionLength);
            Assert.Equal(50, Rows(description) + Hidden(description));
        }
    }

    [Collection("Discord")]
    public class LobbyListCommandTests
    {
        const string Unreachable = "Steam can't be reached right now";
        const string NoLobbies = "No lobbies available!";

        static List<LobbyInfo> ManyLobbies() => Enumerable.Range(1, 100).Select(id => LobbyListLimitTests.Lobby(id)).ToList();

        [Fact]
        public async Task SlashCommandShowsAFullGame()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.Lobbies = ManyLobbies();

            await discord.SendSlashCommandAsync("murder-miners", "lobbies");

            Assert.Contains("more lobbies", Assert.Single(discord.Single("followup").EmbedDescriptions));
        }

        [Fact]
        public async Task PrefixCommandShowsAFullGame()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.Lobbies = ManyLobbies();

            await discord.SendMessageAsync("+Lobbies");

            Assert.Contains("more lobbies", Assert.Single(discord.Single("send message").EmbedDescriptions));
        }

        [Fact]
        public async Task SlashCommandSaysWhenSteamCanNotBeReached()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.LobbiesAvailable = false;

            await discord.SendSlashCommandAsync("murder-miners", "lobbies");

            var description = Assert.Single(discord.Single("followup").EmbedDescriptions);
            Assert.StartsWith(Unreachable, description);
            Assert.DoesNotContain(NoLobbies, description);
        }

        [Fact]
        public async Task PrefixCommandSaysWhenSteamCanNotBeReached()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.LobbiesAvailable = false;

            await discord.SendMessageAsync("+Lobbies");

            Assert.StartsWith(Unreachable, Assert.Single(discord.Single("send message").EmbedDescriptions));
        }

        [Fact]
        public async Task EmptyGameStillSaysThereAreNoLobbies()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("murder-miners", "lobbies");

            var description = Assert.Single(discord.Single("followup").EmbedDescriptions);
            Assert.StartsWith(NoLobbies, description);
            Assert.DoesNotContain(Unreachable, description);
        }
    }
}
