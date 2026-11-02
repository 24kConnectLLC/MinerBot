using System.Net;
using System.Net.Sockets;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Discord.Models;

namespace MinerBot.Tests
{
    // Every step list here shows the order of the data calls and the Discord requests.
    // Discord gives an interaction 3 seconds, so the defer has to come before any data call.
    [Collection("Discord")]
    public class SlashCommandFlowTests
    {
        const string ProfileUrl = "https://steamcommunity.com/profiles/76561197960265729";
        const string MapUrl = "https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890";

        static string Option(string name, string value) => $$"""{"name":"{{name}}","type":3,"value":"{{value}}"}""";

        [Fact]
        public async Task LobbiesDefersBeforeAskingSteam()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("murder-miners", "lobbies");

            Assert.Equal(["defer", "GetServerList", "followup"], discord.StepNames);
            Assert.Contains("Murder Miners Public Lobbies", Assert.Single(discord.Single("followup").EmbedTitles));
        }

        [Fact]
        public async Task ArmorListDefersBeforeAskingTheDatabase()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("armor", "list");

            Assert.Equal(["defer", "GetAllArmorNamesCache", "GetAllArmorNamesCache", "followup"], discord.StepNames);

            var reply = discord.Single("followup");
            Assert.Equal(["Armor List: All"], reply.EmbedTitles);
            Assert.Empty(reply.ButtonIds);
        }

        [Fact]
        public async Task LongArmorListKeepsItsButtons()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Armor.ArmorNames = Enumerable.Range(1, 25).Select(number => $"Armor {number}");

            await discord.SendSlashCommandAsync("armor", "list");

            Assert.Equal(
                [$"armorlist:{discord.UserId},0,previous,All", $"armorlist:{discord.UserId},0,next,All"],
                discord.Single("followup").ButtonIds);
        }

        [Fact]
        public async Task ArmorLookupOfUnknownArmorDefersBeforeAskingTheDatabase()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("armor", "lookup", Option("armor-name", "Unknown"));

            Assert.Equal(["defer", "GetArmorStyle", "followup"], discord.StepNames);
            Assert.Equal(["hmmm..."], discord.Single("followup").EmbedTitles);
        }

        [Fact]
        public async Task ArmorLookupSendsItsImageAsAFollowup()
        {
            await using var discord = await FakeDiscord.StartAsync();
            using var imageServer = new TcpListener(IPAddress.Loopback, 0);
            imageServer.Start();
            var serving = ServeImageAsync(imageServer);
            discord.Armor.Armor = new ArmorStyle
            {
                ArmorName = "Armor One",
                ArmorDescription = "An armor",
                Author = "Author",
                ArmorChoice = "Bug",
                ArmorScreenshot = $"http://127.0.0.1:{((IPEndPoint)imageServer.LocalEndpoint).Port}/armor.png"
            };

            await discord.SendSlashCommandAsync("armor", "lookup", Option("armor-name", "Armor One"));
            await serving;

            Assert.Equal(["defer", "GetArmorStyle", "followup"], discord.StepNames);

            var reply = discord.Single("followup");
            Assert.Equal(["Armor One"], reply.EmbedTitles);
            Assert.Equal("image", reply.File);
        }

        static async Task ServeImageAsync(TcpListener server)
        {
            using var client = await server.AcceptTcpClientAsync();
            using var reader = new StreamReader(client.GetStream());
            using var writer = new StreamWriter(client.GetStream());

            // The request ends with an empty line
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
            {
            }

            await writer.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nimage");
        }

        [Fact]
        public async Task LatestMapsDefersBeforeAskingSteam()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("maps", "latest");

            Assert.Equal(["defer", "GetLatestWorkshopItems", "followup"], discord.StepNames);

            var reply = discord.Single("followup");
            Assert.Equal("__**Latest Maps: (1/5)**__", reply.Content);
            Assert.Equal(["Map 1"], reply.EmbedTitles);
            Assert.Equal([$"maplist:{discord.UserId},0,previous", $"maplist:{discord.UserId},0,next"], reply.ButtonIds);
        }

        [Fact]
        public async Task LatestMapsWithNoResultGetsAFailureReply()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.LatestItems = new();

            await discord.SendSlashCommandAsync("maps", "latest");

            Assert.Equal(["defer", "GetLatestWorkshopItems", "followup"], discord.StepNames);
            Assert.Equal("Something went wrong with that command. Please try again later.", discord.Single("followup").Content);
        }

        [Fact]
        public async Task MapLookupDefersBeforeAskingSteam()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("maps", "lookup", Option("map-url", MapUrl));

            Assert.Equal(["defer", "GetWorkshopItem", "followup"], discord.StepNames);
            Assert.Equal(["Map 1"], discord.Single("followup").EmbedTitles);
        }

        [Fact]
        public async Task BadMapLinkGetsAFriendlyFollowup()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("maps", "lookup", Option("map-url", "not a link"));

            Assert.Equal(["defer", "followup"], discord.StepNames);
            Assert.StartsWith("Error: Please Provide a valid Steam Workshop Map", Assert.Single(discord.Single("followup").EmbedTitles));
        }

        [Fact]
        public async Task SteamIdDefersBeforeAskingSteam()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("more", "steam_id", Option("profile-url", ProfileUrl));

            Assert.Equal(["defer", "GetPlayerSummaries", "followup"], discord.StepNames);
            Assert.Equal(["Steam IDs for Player:"], discord.Single("followup").EmbedTitles);
        }

        [Fact]
        public async Task InfoHasNothingToWaitForSoItRespondsDirectly()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("more", "info");

            Assert.Equal(["respond"], discord.StepNames);
        }

        [Fact]
        public async Task SetChannelsDefersBeforeAskingTheDatabase()
        {
            await using var discord = await FakeDiscord.StartAsync();
            var channel = FakeDiscord.ChannelId;
            var options = string.Join(",", new[] { "armor-channel", "map-channel", "lobby-channel", "announcements-channel" }
                .Select(name => $$"""{"name":"{{name}}","type":7,"value":"{{channel}}"}"""));
            var resolved = $$"""{"channels":{"{{channel}}":{"id":"{{channel}}","type":0,"name":"general","permissions":"0"} } }""";

            await discord.SendSlashCommandAsync("admin", "set_channels", options, resolved);

            Assert.Equal(["defer", "GetGuild", "InsertGuild", "followup"], discord.StepNames);
            Assert.Equal("Thanks for setting the channels!", discord.Single("followup").Content);
        }

        [Fact]
        public async Task AnnounceDefersBeforeAskingTheDatabase()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Guilds.Guild = new Guild { announcements_channel_id = FakeDiscord.ChannelId };

            await discord.SendSlashCommandAsync("admin", "announce", Option("message", "Hello"));

            Assert.Equal(["defer", "GetGuild", "send message", "followup"], discord.StepNames);
            Assert.Equal("Hello", discord.Single("send message").Content);
            Assert.Equal("Announcement Sent!", discord.Single("followup").Content);
        }

        [Fact]
        public async Task PinDefersBeforeAskingDiscord()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendMessageCommandAsync("pin");

            Assert.Equal(["defer", "get pins", "pin", "followup"], discord.StepNames);
            Assert.Equal(":white_check_mark: Successfully pinned message!", discord.Single("followup").Content);
        }

        [Fact]
        public async Task PinOfASystemMessageIsRefusedWithAFollowup()
        {
            await using var discord = await FakeDiscord.StartAsync();

            const int userJoinedMessage = 7;
            await discord.SendMessageCommandAsync("pin", userJoinedMessage);

            Assert.Equal(["defer", "followup"], discord.StepNames);
            Assert.Equal(":x: You cant pin system messages!", discord.Single("followup").Content);
        }

        [Theory]
        [InlineData(0, "next", 2, "Map 2")]
        [InlineData(2, "next", 4, "Map 4")]
        [InlineData(2, "previous", 2, "Map 2")]
        public async Task MapPageButtonDefersAndEditsTheMessage(int page, string direction, int shownPage, string shownMap)
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendButtonAsync($"maplist:{discord.UserId},{page},{direction}");

            Assert.Equal(["defer update", "GetLatestWorkshopItems", "edit original"], discord.StepNames);

            var edit = discord.Single("edit original");
            Assert.Equal($"__**Latest Maps: ({shownPage}/5)**__", edit.Content);
            Assert.Equal([shownMap], edit.EmbedTitles);
            Assert.Equal([$"maplist:{discord.UserId},{shownPage - 1},previous", $"maplist:{discord.UserId},{shownPage - 1},next"], edit.ButtonIds);
        }

        [Fact]
        public async Task ArmorPageButtonDefersAndEditsTheMessage()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Armor.ArmorNames = Enumerable.Range(1, 45).Select(number => $"Armor {number}");

            await discord.SendButtonAsync($"armorlist:{discord.UserId},0,next,Bug");

            Assert.Equal(["defer update", "GetArmorNamesByHelmet", "edit original"], discord.StepNames);

            var edit = discord.Single("edit original");
            Assert.Equal(["Armor List: Bug"], edit.EmbedTitles);
            Assert.Equal([$"armorlist:{discord.UserId},1,previous,Bug", $"armorlist:{discord.UserId},1,next,Bug"], edit.ButtonIds);
        }

        [Fact]
        public async Task PageButtonFailureLeavesTheMessageAndRepliesPrivately()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.LatestItems = new();

            await discord.SendButtonAsync($"maplist:{discord.UserId},0,next");

            Assert.Equal(["defer update", "GetLatestWorkshopItems", "followup"], discord.StepNames);

            var reply = discord.Single("followup");
            Assert.True(reply.IsEphemeral);
            Assert.Equal("Something went wrong with that command. Please try again later.", reply.Content);
        }
    }
}
