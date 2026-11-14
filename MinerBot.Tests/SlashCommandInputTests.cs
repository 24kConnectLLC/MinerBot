using System.Text.Json;
using MinerBot_2._0;
using MinerBot_2._0.Extensions;
using MinerBot_2._0.Services;
using Steam.Models;

namespace MinerBot.Tests
{
    // Discord doesn't check the text of these options, so anything a user can type has to get a sensible reply
    [Collection("Discord")]
    public class SlashCommandInputTests
    {
        const string MapUrl = "https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890";

        static string Option(string name, string value) => $$"""{"name":"{{name}}","type":3,"value":{{JsonSerializer.Serialize(value)}}}""";

        [Theory]
        [InlineData("not-a-helmet")]
        [InlineData("1")]
        [InlineData("999")]
        [InlineData("-1")]
        [InlineData("Bug,Ninja")]
        [InlineData("___")]
        public async Task UnknownHelmetGetsAPrivateReply(string helmet)
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("armor", "list", Option("helmet-type", helmet));

            var reply = discord.Single("respond");
            Assert.Contains("/armor helmets", reply.Content);
            Assert.DoesNotContain(helmet, reply.Body);
            Assert.True(reply.IsEphemeral);
            Assert.True(reply.AllowsNoMentions);
            Assert.Equal(["respond"], discord.StepNames);
        }

        [Theory]
        [InlineData("bug", "Bug")]
        [InlineData("TRI-CLOPS", "Tri-Clops")]
        [InlineData("t_visor", "T-Visor")]
        [InlineData(" Bug ", "Bug")]
        public async Task HelmetCanBeTypedInAnyForm(string helmet, string expected)
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("armor", "list", Option("helmet-type", helmet));

            Assert.Equal(expected, discord.Armor.RequestedHelmet);
            Assert.Equal([$"Armor List: {expected}"], discord.Single("followup").EmbedTitles);
        }

        // The page buttons read the enum name back, so the text as typed would fail on the next page
        [Theory]
        [InlineData("TRI-CLOPS", "Tri_Clops", "Tri-Clops")]
        [InlineData("t_visor", "T_Visor", "T-Visor")]
        [InlineData("bug", "Bug", "Bug")]
        public async Task FilteredListCanTurnItsPages(string typed, string enumName, string shown)
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Armor.ArmorNames = Enumerable.Range(1, 45).Select(number => $"Armor {number}");

            await discord.SendSlashCommandAsync("armor", "list", Option("helmet-type", typed));

            var reply = discord.Single("followup");
            Assert.Equal([$"Armor List: {shown}"], reply.EmbedTitles);

            var buttons = reply.ButtonIds.ToList();
            Assert.Equal([$"armorlist:{discord.UserId},0,previous,{enumName}", $"armorlist:{discord.UserId},0,next,{enumName}"], buttons);

            await discord.SendButtonAsync(buttons[1]);

            Assert.Equal([$"Armor List: {shown}"], discord.Single("edit original").EmbedTitles);
        }

        [Fact]
        public void EveryHelmetNameParsesToItsHelmet()
        {
            foreach (var helmet in Enum.GetValues<HelmentType>())
            {
                Assert.True(ArmorService.TryParseHelmet(helmet.ToName(), out var parsed), helmet.ToName());
                Assert.Equal(helmet, parsed);
            }
        }

        [Fact]
        public async Task HelmetsListsEveryHelmetUnderTheNameToType()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("armor", "helmets");

            Assert.Equal(["respond"], discord.StepNames);

            var reply = discord.Single("respond");
            Assert.False(reply.IsEphemeral);
            Assert.Equal(["Helmets:"], reply.EmbedTitles);
            Assert.Equal(
                Enum.GetValues<HelmentType>().Select(helmet => helmet.ToName()),
                ArmorEmbedTests.NamesIn(Assert.Single(reply.EmbedDescriptions)));
        }

        [Theory]
        [InlineData("tri", "Tri-Clops")]
        [InlineData("TRI", "Tri-Clops")]
        [InlineData("clops", "Tri-Clops")]
        [InlineData("tri-c", "Tri-Clops")]
        [InlineData("zom", "Zombie")]
        public async Task AutocompleteFindsAHelmetByPartOfItsName(string typed, string expected)
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendAutocompleteAsync("armor", "list", "helmet-type", typed);

            Assert.Equal([(expected, expected)], discord.Single("autocomplete").Choices);
        }

        [Fact]
        public async Task AutocompleteStopsAtTheLimitOfDiscord()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendAutocompleteAsync("armor", "list", "helmet-type", "");

            Assert.Equal(25, discord.Single("autocomplete").Choices.Count());
        }

        [Fact]
        public async Task AutocompleteDoesNotUseUpTheCooldown()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendAutocompleteAsync("armor", "list", "helmet-type", "tri");
            await discord.SendSlashCommandAsync("armor", "list");

            Assert.Equal(["Armor List: All"], discord.Single("followup").EmbedTitles);
        }

        const string BadMapTitle = "Error: Please Provide a valid Steam Workshop Map URL/Link...";
        const string NoPublicMapTitle = "Error: Steam has no public map with that ID...";
        const string OtherGameTitle = "Error: That isn't a Murder Miners map...";
        const string UnreachableTitle = "Error: Steam can't be reached right now...";
        const string BadProfileTitle = "Error: Please Provide a valid Steam User Profile URL/Link...";
        const string NoProfileTitle = "Error: Steam has no profile for that link or ID...";

        // Steam isn't asked about anything but a Workshop link or a map's ID
        [Theory]
        [InlineData("not a link")]
        [InlineData("0")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=abc")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=-1")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=99999999999999999999999")]
        [InlineData("https://steamcommunity.com.example.com/sharedfiles/filedetails/?id=1234567890")]
        [InlineData("https://example.com/steamcommunity.com/sharedfiles/filedetails/?id=1234567890")]
        public async Task BadMapLinkGetsAFriendlyReply(string link)
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("maps", "lookup", Option("map-url", link));

            Assert.Equal(["defer", "followup"], discord.StepNames);
            var reply = discord.Single("followup");
            Assert.Equal([BadMapTitle], reply.EmbedTitles);
            Assert.Contains("only the map's ID", Assert.Single(reply.EmbedDescriptions));
            Assert.Null(discord.Steam.RequestedId);
        }

        [Theory]
        [InlineData(MapUrl)]
        [InlineData("1234567890")]
        [InlineData("<https://steamcommunity.com/workshop/filedetails/?id=1234567890>")]
        [InlineData("http://www.steamcommunity.com/sharedfiles/filedetails?id=1234567890&searchtext=")]
        public async Task MapLinkWorks(string link)
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("maps", "lookup", Option("map-url", link));

            Assert.Equal(1234567890UL, discord.Steam.RequestedId);
            Assert.Equal(["Map 1"], discord.Single("followup").EmbedTitles);
        }

        // An unlisted map is meant to be shared by its link
        [Fact]
        public async Task UnlistedMapIsShown()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.WorkshopItem!.Visibility = PublishedFileVisibility.Unknown;

            await discord.SendSlashCommandAsync("maps", "lookup", Option("map-url", MapUrl));

            Assert.Equal(["Map 1"], discord.Single("followup").EmbedTitles);
        }

        // The same reply whatever the reason, so the bot never confirms that a private map exists
        [Theory]
        [InlineData("no details")]
        [InlineData("removed")]
        [InlineData("banned")]
        [InlineData("friends-only")]
        [InlineData("private")]
        [InlineData("another game's private item")]
        public async Task MapThatSteamDoesNotShowGetsAFriendlyReply(string reason)
        {
            await using var discord = await FakeDiscord.StartAsync();
            var map = discord.Steam.WorkshopItem!;

            switch (reason)
            {
                case "no details": discord.Steam.WorkshopItem = null; break;
                case "removed": discord.Steam.WorkshopItem = new PublishedFileDetailsModel { PublishedFileId = 1234567890, Result = 9, Visibility = PublishedFileVisibility.Unknown }; break;
                case "banned": map.Banned = true; break;
                case "friends-only": map.Visibility = PublishedFileVisibility.FriendsOnly; break;
                case "private": map.Visibility = PublishedFileVisibility.Private; break;
                case "another game's private item": map.Visibility = PublishedFileVisibility.Private; map.ConsumerAppId = 4000; break;
            }

            await discord.SendSlashCommandAsync("maps", "lookup", Option("map-url", MapUrl));

            Assert.Equal(["defer", "GetWorkshopItem", "followup"], discord.StepNames);
            var reply = discord.Single("followup");
            Assert.Equal([NoPublicMapTitle], reply.EmbedTitles);
            Assert.Equal(["It may have been removed or hidden."], reply.EmbedDescriptions);
        }

        [Fact]
        public async Task MapOfAnotherGameGetsItsOwnReply()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.WorkshopItem!.ConsumerAppId = 4000;

            await discord.SendSlashCommandAsync("maps", "lookup", Option("map-url", MapUrl));

            Assert.Equal([OtherGameTitle], discord.Single("followup").EmbedTitles);
        }

        [Fact]
        public async Task MapLookupWhileSteamCanNotBeReachedSaysSo()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.Failure = new HttpRequestException("Steam didn't answer the Workshop details request");

            await discord.SendSlashCommandAsync("maps", "lookup", Option("map-url", MapUrl));

            Assert.Equal(["defer", "GetWorkshopItem", "followup"], discord.StepNames);
            var reply = discord.Single("followup");
            Assert.Equal([UnreachableTitle], reply.EmbedTitles);
            Assert.Equal(["Please try again later."], reply.EmbedDescriptions);
            Assert.Contains("Couldn't look up a map on Steam: HttpRequestException: Steam didn't answer the Workshop details request", discord.ConsoleText);
        }

        // Steam isn't asked about anything but a profile link or a SteamID64
        [Theory]
        [InlineData("https://steamcommunity.com/profiles/abc")]
        [InlineData("https://steamcommunity.com/profiles/")]
        [InlineData("https://steamcommunity.com/profiles/99999999999999999999999")]
        [InlineData("https://steamcommunity.com/profiles/1234567890")]
        [InlineData("https://steamcommunity.com/id/")]
        [InlineData("https://steamcommunity.com/id/foo.bar")]
        [InlineData("https://steamcommunity.com.example.com/id/foo")]
        [InlineData("https://example.com/steamcommunity.com/id/foo")]
        [InlineData("42")]
        [InlineData("not a link")]
        public async Task BadProfileLinkGetsAFriendlyReply(string link)
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("more", "steam_id", Option("profile-url", link));

            Assert.Equal(["defer", "followup"], discord.StepNames);
            Assert.Equal([BadProfileTitle], discord.Single("followup").EmbedTitles);
            Assert.Null(discord.Steam.RequestedId);
            Assert.Null(discord.Steam.RequestedName);
        }

        [Theory]
        [InlineData("https://steamcommunity.com/profiles/76561197960265729")]
        [InlineData("https://steamcommunity.com/profiles/76561197960265729/")]
        [InlineData("https://steamcommunity.com/profiles/76561197960265729/games")]
        [InlineData("<http://www.steamcommunity.com/profiles/76561197960265729>")]
        [InlineData("76561197960265729")]
        public async Task ProfileLinkWorks(string link)
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("more", "steam_id", Option("profile-url", link));

            Assert.Equal(["defer", "GetPlayerSummaries", "followup"], discord.StepNames);
            Assert.Equal(76561197960265729UL, discord.Steam.RequestedId);
            Assert.Equal(["Steam IDs for Player:"], discord.Single("followup").EmbedTitles);
        }

        // .../id/foo/games was once looked up as foogames, which can be someone else's name
        [Fact]
        public async Task CustomUrlIsLookedUpByItsName()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("more", "steam_id", Option("profile-url", "https://steamcommunity.com/id/foo/games"));

            Assert.Equal(["defer", "ResolveVanityURL", "GetPlayerSummaries", "followup"], discord.StepNames);
            Assert.Equal("foo", discord.Steam.RequestedName);
            Assert.Equal(FakeGetData.VanitySteamId, discord.Steam.RequestedId);
            Assert.Equal(["Steam IDs for Player:"], discord.Single("followup").EmbedTitles);
            Assert.Contains($"SteamID 64: `{FakeGetData.VanitySteamId}`", Assert.Single(discord.Single("followup").EmbedDescriptions));
        }

        // A name nobody has once became SteamID 42, and the friendly reply never showed
        [Fact]
        public async Task CustomUrlThatNobodyHasGetsAFriendlyReply()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.VanityId = null;

            await discord.SendSlashCommandAsync("more", "steam_id", Option("profile-url", "https://steamcommunity.com/id/nobody"));

            Assert.Equal(["defer", "ResolveVanityURL", "followup"], discord.StepNames);
            Assert.Equal([NoProfileTitle], discord.Single("followup").EmbedTitles);
        }

        // A SteamID64 on its own is read too, so the reply speaks of a link or an ID
        [Theory]
        [InlineData("https://steamcommunity.com/profiles/76561197960265729")]
        [InlineData("76561197960265729")]
        public async Task ProfileThatSteamDoesNotHaveGetsAFriendlyReply(string link)
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.PlayerSummary = null;

            await discord.SendSlashCommandAsync("more", "steam_id", Option("profile-url", link));

            Assert.Equal(["defer", "GetPlayerSummaries", "followup"], discord.StepNames);
            Assert.Equal(76561197960265729UL, discord.Steam.RequestedId);
            Assert.Equal([NoProfileTitle], discord.Single("followup").EmbedTitles);
        }

        [Theory]
        [InlineData("https://steamcommunity.com/id/foo", "ResolveVanityURL")]
        [InlineData("https://steamcommunity.com/profiles/76561197960265729", "GetPlayerSummaries")]
        public async Task ProfileLookupWhileSteamCanNotBeReachedSaysSo(string link, string failedCall)
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.Failure = new TimeoutException("No answer from api.steampowered.com within 10 seconds");

            await discord.SendSlashCommandAsync("more", "steam_id", Option("profile-url", link));

            Assert.Equal(["defer", failedCall, "followup"], discord.StepNames);
            var reply = discord.Single("followup");
            Assert.Equal([UnreachableTitle], reply.EmbedTitles);
            Assert.Equal(["Please try again later."], reply.EmbedDescriptions);
            Assert.Contains("Couldn't look up a Steam profile: TimeoutException: No answer from api.steampowered.com within 10 seconds", discord.ConsoleText);
        }
    }
}
