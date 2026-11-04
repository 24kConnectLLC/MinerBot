using System.Text.Json;
using System.Text.RegularExpressions;
using Discord;

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

        static JsonElement Subcommand(FakeDiscord discord, string group, string name) =>
            Registered(discord, group).GetProperty("options").EnumerateArray().Single(option => option.GetProperty("name").GetString() == name);

        static IEnumerable<int> Contexts(JsonElement command) => command.GetProperty("contexts").EnumerateArray().Select(context => context.GetInt32());

        // Discord hides the admin commands from members without Administrator. A server can show them to other roles, so they check too.
        [Fact]
        public async Task AdminCommandsAreRegisteredForAdministratorsInServersOnly()
        {
            await using var discord = await FakeDiscord.StartAsync();

            var admin = Registered(discord, "admin");

            // ToString reads the value whether it's written as a number or as a string
            Assert.Equal(((ulong)GuildPermission.Administrator).ToString(), admin.GetProperty("default_member_permissions").ToString());
            Assert.Equal([(int)InteractionContextType.Guild], Contexts(admin));
        }

        [Theory]
        [InlineData("pin")]
        [InlineData("greet")]
        public async Task ContextCommandIsOfferedInServersOnly(string name)
        {
            await using var discord = await FakeDiscord.StartAsync();

            Assert.Equal([(int)InteractionContextType.Guild], Contexts(Registered(discord, name)));
        }

        [Fact]
        public async Task SetChannelsOffersOnlyOptionalTextAndAnnouncementChannels()
        {
            await using var discord = await FakeDiscord.StartAsync();

            var options = Subcommand(discord, "admin", "set_channels").GetProperty("options").EnumerateArray().ToList();

            Assert.Equal(["armor-channel", "map-channel", "lobby-channel", "announcements-channel"], options.Select(option => option.GetProperty("name").GetString()));
            Assert.All(options, option =>
            {
                Assert.Equal(7, option.GetProperty("type").GetInt32());
                Assert.False(option.GetProperty("required").GetBoolean());
                Assert.Equal([(int)ChannelType.Text, (int)ChannelType.News], option.GetProperty("channel_types").EnumerateArray().Select(type => type.GetInt32()));
                Assert.Contains("Leave out to turn", option.GetProperty("description").GetString());
            });
        }

        [Fact]
        public async Task AnnounceMessageIsCappedAtOneDiscordMessage()
        {
            await using var discord = await FakeDiscord.StartAsync();

            var message = Assert.Single(Subcommand(discord, "admin", "announce").GetProperty("options").EnumerateArray());

            Assert.Equal("message", message.GetProperty("name").GetString());
            Assert.Equal(2000, message.GetProperty("max_length").GetInt32());
            Assert.True(message.GetProperty("required").GetBoolean());
            Assert.NotEqual("message", message.GetProperty("description").GetString());
        }

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
