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
            // 100 lobbies never fit, so without an ending this fails instead of running forever
            for (int count = 1; count <= 100; count++)
            {
                steam.Lobbies = Lobbies(1, count);

                var description = await Description();

                if (Hidden(description) == 0)
                    continue;

                Assert.Contains("...and 1 more lobby\n", description);
                return;
            }

            Assert.Fail("No lobby was ever hidden");
        }

        [Fact]
        public async Task AllVersionGroupsShareTheBudget()
        {
            // Each group fits by itself
            steam.Lobbies = [.. Lobbies(1, 15), .. Lobbies(101, 15, version: 92), .. Lobbies(201, 15, version: 87)];

            var description = await Description();

            Assert.InRange(description.Length, 1, EmbedBuilder.MaxDescriptionLength);
            Assert.Equal(45, Rows(description) + Hidden(description));
            Assert.Contains("__Update 41 (v105):__", description);
            Assert.Contains("__Update 40 (v92):__", description);
            Assert.DoesNotContain("__Update 39 (v87):__", description);
        }

        [Theory]
        [InlineData(92, "Update 40 (v92)")]
        [InlineData(104, "Other versions")]
        public async Task LaterGroupsCountAsHidden(uint laterVersion, string laterHeading)
        {
            steam.Lobbies = [.. Lobbies(1, 100), .. Lobbies(201, 5, version: laterVersion)];

            var description = await Description();

            Assert.DoesNotContain($"__{laterHeading}:__", description);
            Assert.Equal(105, Rows(description) + Hidden(description));
        }

        // The rows of Other versions are longer, because they show the version
        [Theory]
        [InlineData(1, 92)]
        [InlineData(7, 92)]
        [InlineData(12, 92)]
        [InlineData(32, 92)]
        [InlineData(1, 104)]
        [InlineData(32, 104)]
        public async Task EveryHeadingHasARowUnderIt(int hostLength, uint secondVersion)
        {
            for (int firstGroup = 1; firstGroup <= 40; firstGroup++)
            {
                steam.Lobbies =
                [
                    .. Enumerable.Range(1, firstGroup).Select(id => Lobby(id, hostLength: hostLength)),
                    .. Enumerable.Range(101, 40).Select(id => Lobby(id, version: secondVersion, hostLength: hostLength))
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

        // They used to vanish from the list
        [Fact]
        public async Task LobbiesOfAVersionWithNoHeadingAreCounted()
        {
            const uint versionWithNoHeading = 10;
            steam.Lobbies = [.. Lobbies(1, 50), .. Lobbies(101, 5, versionWithNoHeading)];

            var description = await Description();

            Assert.Equal(55, Rows(description) + Hidden(description));
        }

        // Versions

        [Theory]
        [InlineData(105, "Update 41 (v105)")]
        [InlineData(92, "Update 40 (v92)")]
        [InlineData(87, "Update 39 (v87)")]
        [InlineData(80, "Update 35 (Xbox One Branch) (v80)")]
        [InlineData(61, "Update 29 (v61)")]
        [InlineData(53, "Update 19 (Xbox 360 Branch) (v53)")]
        [InlineData(0, "2012 Alpha (v0)")]
        [InlineData(39, "Legacy Egg Hunt Branch (v39)")]
        public async Task BranchHeadingSaysItsVersion(uint version, string heading)
        {
            steam.Lobbies = [Lobby(1, version)];

            var description = await Description();

            Assert.StartsWith($"__{heading}:__\n\n{RowStart}", description);
            Assert.Equal(1, Rows(description));
            Assert.DoesNotContain("**Version:**", description);
        }

        [Fact]
        public async Task BranchesKeepTheirOrderAndOtherVersionsComeLast()
        {
            uint[] versions = [10, 39, 0, 53, 104, 61, 80, 87, 92, 105];
            steam.Lobbies = versions.Select((version, index) => Lobby(index + 1, version)).ToList();

            var description = await Description();

            Assert.Equal(
                ["Update 41 (v105)", "Update 40 (v92)", "Update 39 (v87)", "Update 35 (Xbox One Branch) (v80)", "Update 29 (v61)",
                 "Update 19 (Xbox 360 Branch) (v53)", "2012 Alpha (v0)", "Legacy Egg Hunt Branch (v39)", "Other versions"],
                Regex.Matches(description, @"__(.+?):__").Select(match => match.Groups[1].Value));
        }

        // A Join link only works on the lobby's own version, so each of these rows says which that is
        [Fact]
        public async Task OtherVersionsShowTheVersionOfEachLobby()
        {
            uint[] versions = [104, 200, 93, 10, 106, 90];
            steam.Lobbies = [Lobby(1), .. versions.Select((version, index) => Lobby(index + 2, version))];

            var description = await Description();
            var otherVersions = description[description.IndexOf("__Other versions:__\n\n")..];

            Assert.Equal(versions.Length + 1, Rows(description));
            Assert.Equal(versions.Length, Rows(otherVersions));
            Assert.Equal(versions.Select(version => $"v{version}"),
                Regex.Matches(otherVersions, @"\*\*Version:\*\* `(v\d+)`").Select(match => match.Groups[1].Value));
        }

        [Fact]
        public async Task RowsSayWhatEachLobbyIs()
        {
            var lobby = Lobby(1);
            var otherLobby = Lobby(2, version: 200);
            steam.Lobbies = [lobby, otherLobby];

            var description = await Description();

            Assert.Equal(
                "__Update 41 (v105):__\n\n" +
                $"- **Host:** `1 aaaaaaaaaa` ​ **Map:** `Skyline X by Murder Miners 12`\n ​ **Mode:** `Team Murder Match` ​ **Players:** `3/8` ​ [Join]({lobby.JoinURL})\n\n" +
                "__Other versions:__\n\n" +
                $"- **Host:** `2 aaaaaaaaaa` ​ **Map:** `Skyline X by Murder Miners 12`\n ​ **Version:** `v200` ​ **Mode:** `Team Murder Match` ​ **Players:** `3/8` ​ [Join]({otherLobby.JoinURL})\n\n",
                description);
        }

        [Theory]
        [InlineData(105)]
        [InlineData(103)]
        [InlineData(102)]
        [InlineData(101)]
        [InlineData(200)]
        [InlineData(92)]
        [InlineData(87)]
        [InlineData(39)]
        public async Task ModesAreShownForVersionsWhoseLayoutIsExact(uint version)
        {
            steam.Lobbies = [Lobby(1, version)];

            var description = await Description();

            Assert.Contains("**Mode:** `Team Murder Match`", description);
        }

        // Their modes would be guesses
        [Theory]
        [InlineData(104)]
        [InlineData(100)]
        [InlineData(93)]
        [InlineData(91)]
        [InlineData(90)]
        [InlineData(89)]
        [InlineData(88)]
        [InlineData(80)]
        [InlineData(61)]
        [InlineData(53)]
        [InlineData(0)]
        [InlineData(106)]
        [InlineData(86)]
        [InlineData(10)]
        [InlineData(uint.MaxValue)]
        public async Task ModesAreUnknownForOtherVersions(uint version)
        {
            steam.Lobbies = [Lobby(1, version)];

            var description = await Description();

            Assert.Contains("**Mode:** `Modes Unknown`", description);
            Assert.DoesNotContain("Team Murder Match", description);
        }

        [Theory]
        [InlineData(105)]
        [InlineData(104)]
        public async Task LobbyWithRoomHasAJoinLink(uint version)
        {
            var lobby = Lobby(1, version);
            steam.Lobbies = [lobby];

            var description = await Description();

            Assert.EndsWith($"**Players:** `3/8` ​ [Join]({lobby.JoinURL})\n\n", description);
        }

        [Theory]
        [InlineData(105)]
        [InlineData(104)]
        public async Task FullLobbyHasNoJoinLink(uint version)
        {
            var lobby = Lobby(1, version);
            lobby.CurrentPlayers = lobby.MaxPlayers;
            steam.Lobbies = [lobby];

            var description = await Description();

            Assert.EndsWith("**Players:** `8/8` ​ \n\n", description);
            Assert.DoesNotContain("[Join]", description);
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
