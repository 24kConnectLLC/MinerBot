using System.Text.Json;
using MinerBot_2._0;
using MinerBot_2._0.Extensions;
using MinerBot_2._0.Services;

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

        [Theory]
        [InlineData("not a link")]
        [InlineData("1234567890")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=abc")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=-1")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=99999999999999999999999")]
        public async Task BadMapLinkGetsAFriendlyReply(string link)
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("maps", "lookup", Option("map-url", link));

            Assert.StartsWith("Error: Please Provide a valid Steam Workshop Map", Assert.Single(discord.Single("followup").EmbedTitles));
            Assert.Null(discord.Steam.RequestedId);
        }

        [Fact]
        public async Task MapLinkWorks()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("maps", "lookup", Option("map-url", MapUrl));

            Assert.Equal(1234567890UL, discord.Steam.RequestedId);
            Assert.Equal(["Map 1"], discord.Single("followup").EmbedTitles);
        }

        [Fact]
        public async Task MapThatSteamDoesNotReturnGetsAFriendlyReply()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.WorkshopItem = null!;

            await discord.SendSlashCommandAsync("maps", "lookup", Option("map-url", MapUrl));

            Assert.StartsWith("Error: Please Provide a valid Steam Workshop Map", Assert.Single(discord.Single("followup").EmbedTitles));
        }

        [Theory]
        [InlineData("https://steamcommunity.com/profiles/abc")]
        [InlineData("https://steamcommunity.com/profiles/")]
        [InlineData("https://steamcommunity.com/profiles/99999999999999999999999")]
        [InlineData("https://steamcommunity.com/profiles/76561197960265729/games")]
        [InlineData("not a link")]
        public async Task BadProfileLinkGetsAFriendlyReply(string link)
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("more", "steam_id", Option("profile-url", link));

            Assert.StartsWith("Error: Please Provide a valid Steam User Profile", Assert.Single(discord.Single("followup").EmbedTitles));
            Assert.Null(discord.Steam.RequestedId);
        }

        [Theory]
        [InlineData("https://steamcommunity.com/profiles/76561197960265729")]
        [InlineData("https://steamcommunity.com/profiles/76561197960265729/")]
        public async Task ProfileLinkWorks(string link)
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("more", "steam_id", Option("profile-url", link));

            Assert.Equal(76561197960265729UL, discord.Steam.RequestedId);
            Assert.Equal(["Steam IDs for Player:"], discord.Single("followup").EmbedTitles);
        }
    }
}
