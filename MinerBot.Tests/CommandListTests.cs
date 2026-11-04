using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinerBot.Tests
{
    [Collection("Discord")]
    public class CommandListTests
    {
        const string MapUrl = "https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890";

        static string Option(string name, string value) => $$"""{"name":"{{name}}","type":3,"value":"{{value}}"}""";

        [Fact]
        public async Task HelpHasNothingToWaitForSoItRespondsDirectly()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendTopLevelCommandAsync("help");

            Assert.Equal(["respond"], discord.StepNames);

            var reply = discord.Single("respond");
            Assert.False(reply.IsEphemeral);
            Assert.Equal(["Help:"], reply.EmbedTitles);
        }

        [Fact]
        public async Task HelpListsEverySlashCommandAndNoOthers()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendTopLevelCommandAsync("help");

            var listed = Regex.Matches(discord.Single("respond").Body, @"\*\*/([^*]+)\*\*").Select(match => match.Groups[1].Value);
            var loaded = discord.LoadedSlashCommands.Where(command => !command.StartsWith("test "));

            Assert.Equal(loaded.Order(), listed.Order());
        }

        [Fact]
        public async Task HelpHasACooldown()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendTopLevelCommandAsync("help");
            await discord.SendTopLevelCommandAsync("help");

            Assert.StartsWith("This command is usable <t:", discord.Steps[^1].Content);
        }

        // This shows what the bot asks for. Whether Discord offers the commands in a direct message can only be seen on live Discord.
        [Theory]
        [InlineData("help")]
        [InlineData("armor")]
        [InlineData("maps")]
        [InlineData("more")]
        [InlineData("murder-miners")]
        public async Task CommandIsRegisteredForEveryoneAndForDirectMessages(string name)
        {
            await using var discord = await FakeDiscord.StartAsync();

            var command = Registered(discord, name);

            Assert.True(command.GetProperty("dm_permission").GetBoolean());
            Assert.Equal(JsonValueKind.Null, command.GetProperty("default_member_permissions").ValueKind);
            Assert.False(command.TryGetProperty("contexts", out var contexts) && contexts.ValueKind != JsonValueKind.Null);
        }

        [Fact]
        public async Task HelmetOptionIsOptionalTextWithSuggestions()
        {
            await using var discord = await FakeDiscord.StartAsync();

            var list = Registered(discord, "armor").GetProperty("options").EnumerateArray().Single(option => option.GetProperty("name").GetString() == "list");
            var helmet = Assert.Single(list.GetProperty("options").EnumerateArray());

            Assert.Equal("helmet-type", helmet.GetProperty("name").GetString());
            Assert.Equal("Optional: Filter armor by helmet by adding the helmet name.", helmet.GetProperty("description").GetString());
            Assert.Equal(3, helmet.GetProperty("type").GetInt32());
            Assert.True(helmet.GetProperty("autocomplete").GetBoolean());
            Assert.False(helmet.GetProperty("required").GetBoolean());
            Assert.Equal(0, helmet.GetProperty("choices").GetArrayLength());
        }

        static JsonElement Registered(FakeDiscord discord, string name) =>
            JsonDocument.Parse(discord.RegisteredCommands!).RootElement.EnumerateArray().Single(command => command.GetProperty("name").GetString() == name);

        [Theory]
        [InlineData("armor", "list", "followup")]
        [InlineData("armor", "helmets", "respond")]
        [InlineData("maps", "latest", "followup")]
        [InlineData("more", "info", "respond")]
        [InlineData("murder-miners", "lobbies", "followup")]
        public async Task CommandWorksInADirectMessage(string group, string subcommand, string replyStep)
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync(group, subcommand, direct: true);

            Assert.False(discord.Single(replyStep).IsEphemeral);
        }

        [Fact]
        public async Task FilteredArmorListCanTurnItsPagesInADirectMessage()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Armor.ArmorNames = Enumerable.Range(1, 45).Select(number => $"Armor {number}");

            await discord.SendSlashCommandAsync("armor", "list", Option("helmet-type", "tri-clops"), direct: true);
            await discord.SendButtonAsync(discord.Single("followup").ButtonIds.Last(), direct: true);

            Assert.Equal(["Armor List: Tri-Clops"], discord.Single("edit original").EmbedTitles);
        }

        [Fact]
        public async Task UnknownHelmetGetsItsOwnReplyInADirectMessage()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("armor", "list", Option("helmet-type", "not-a-helmet"), direct: true);

            Assert.Contains("/armor helmets", discord.Single("respond").Content);
        }

        [Fact]
        public async Task AutocompleteWorksInADirectMessage()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendAutocompleteAsync("armor", "list", "helmet-type", "tri", direct: true);

            Assert.Equal([("Tri-Clops", "Tri-Clops")], discord.Single("autocomplete").Choices);
        }

        [Fact]
        public async Task ArmorLookupOfUnknownArmorWorksInADirectMessage()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("armor", "lookup", Option("armor-name", "Unknown"), direct: true);

            Assert.Equal(["hmmm..."], discord.Single("followup").EmbedTitles);
        }

        [Fact]
        public async Task MapLookupWorksInADirectMessage()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("maps", "lookup", Option("map-url", MapUrl), direct: true);

            Assert.Equal(["Map 1"], discord.Single("followup").EmbedTitles);
        }

        [Fact]
        public async Task SteamIdWorksInADirectMessage()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("more", "steam_id", Option("profile-url", "https://steamcommunity.com/profiles/76561197960265729"), direct: true);

            Assert.Equal(["Steam IDs for Player:"], discord.Single("followup").EmbedTitles);
        }

        [Fact]
        public async Task HelpWorksInADirectMessage()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendTopLevelCommandAsync("help", direct: true);

            Assert.Equal(["Help:"], discord.Single("respond").EmbedTitles);
        }

        [Fact]
        public async Task MapPageButtonWorksInADirectMessage()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("maps", "latest", direct: true);
            await discord.SendButtonAsync($"maplist:{discord.UserId},0,next", direct: true);

            Assert.Equal(["Map 2"], discord.Single("edit original").EmbedTitles);
        }

        [Fact]
        public async Task AdminCommandsAreRefusedInADirectMessage()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("admin", "announce", Option("message", "Hello"), direct: true);

            var reply = discord.Single("respond");
            Assert.Contains("accepted contexts: Guild", reply.Content);
            Assert.True(reply.IsEphemeral);
            Assert.Equal(["respond"], discord.StepNames);
        }
    }
}
