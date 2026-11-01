namespace MinerBot.Tests
{
    [Collection("Discord")]
    public class LobbyListCommandTests
    {
        const string Unreachable = "Steam can't be reached right now";
        const string NoLobbies = "No lobbies available!";

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
