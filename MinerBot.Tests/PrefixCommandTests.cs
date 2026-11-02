using Discord;
using Discord.Commands;

namespace MinerBot.Tests
{
    // No real prefix command has a precondition right now. CommandHandlingService finds this module
    // because it looks in the entry assembly, which is this project when the tests run.
    public class LockedPrefixCommands : ModuleBase<SocketCommandContext>
    {
        [Command("locked")]
        [RequireUserPermission(GuildPermission.Administrator)]
        public Task Locked() => ReplyAsync("Unlocked");
    }

    [Collection("Discord")]
    public class PrefixCommandTests
    {
        const string FailedCommandReply = "Something went wrong with that command. Please try again later.";

        [Fact]
        public async Task FailedCommandPostsFixedTextAndLogsTheDetails()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Armor.Failure = new InvalidOperationException("private detail");

            await discord.SendMessageAsync("+ArmorList");

            var reply = discord.Single("send message");
            Assert.Equal(FailedCommandReply, reply.Content);
            Assert.DoesNotContain("private detail", reply.Body);
            Assert.True(reply.AllowsNoMentions);
            Assert.Contains("private detail", discord.ConsoleText);
        }

        [Fact]
        public async Task MissingInputPostsFixedTextAndLogsTheReason()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendMessageAsync("+Map");

            var reply = discord.Single("send message");
            Assert.Contains("+help", reply.Content);
            Assert.True(reply.AllowsNoMentions);
            Assert.Contains("[Command/Warning] Map failed", discord.ConsoleText);
            Assert.Contains("BadArgCount", discord.ConsoleText);
        }

        [Fact]
        public async Task RefusedCommandPostsFixedTextAndLogsTheReason()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendMessageAsync("+locked", discord.OtherUserId);

            var reply = discord.Single("send message");
            Assert.Equal("You can't use that command here.", reply.Content);
            Assert.True(reply.AllowsNoMentions);
            Assert.Contains("[Command/Warning] locked failed", discord.ConsoleText);
        }

        [Fact]
        public async Task UnknownCommandStaysSilent()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendMessageAsync("+NotACommand");

            Assert.Empty(discord.Steps);
        }

        [Theory]
        [InlineData("not-a-helmet")]
        [InlineData("1")]
        [InlineData("999")]
        [InlineData("-1")]
        [InlineData("Bug,Ninja")]
        public async Task UnknownHelmetGetsAFriendlyReply(string helmet)
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendMessageAsync($"+ArmorList {helmet}");

            var reply = discord.Single("send message");
            Assert.Contains("+helmets", reply.Content);
            Assert.DoesNotContain(helmet, reply.Body);
            Assert.True(reply.AllowsNoMentions);
            Assert.Equal(["send message"], discord.StepNames);
        }

        [Theory]
        [InlineData("bug", "Bug")]
        [InlineData("TRI-CLOPS", "Tri-Clops")]
        [InlineData("t_visor", "T-Visor")]
        public async Task HelmetNamesStillWork(string helmet, string expected)
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendMessageAsync($"+ArmorList {helmet}");

            Assert.Equal(expected, discord.Armor.RequestedHelmet);
            Assert.Equal([$"Armor List: {expected}"], discord.Single("send message").EmbedTitles);
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

            await discord.SendMessageAsync($"+Map {link}");

            Assert.StartsWith("Error: Please Provide a valid Steam Workshop Map", Assert.Single(discord.Single("send message").EmbedTitles));
            Assert.Null(discord.Steam.RequestedId);
        }

        [Fact]
        public async Task MapLinkStillWorks()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendMessageAsync("+Map https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890");

            Assert.Equal(1234567890UL, discord.Steam.RequestedId);
            Assert.Equal(["Map 1"], discord.Single("send message").EmbedTitles);
        }

        [Fact]
        public async Task MapThatSteamDoesNotReturnGetsAFriendlyReply()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.WorkshopItem = null!;

            await discord.SendMessageAsync("+Map https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890");

            Assert.StartsWith("Error: Please Provide a valid Steam Workshop Map", Assert.Single(discord.Single("send message").EmbedTitles));
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

            await discord.SendMessageAsync($"+Steamid {link}");

            Assert.StartsWith("Error: Please Provide a valid Steam User Profile", Assert.Single(discord.Single("send message").EmbedTitles));
            Assert.Null(discord.Steam.RequestedId);
        }

        [Theory]
        [InlineData("https://steamcommunity.com/profiles/76561197960265729")]
        [InlineData("https://steamcommunity.com/profiles/76561197960265729/")]
        public async Task ProfileLinkStillWorks(string link)
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendMessageAsync($"+Steamid {link}");

            Assert.Equal(76561197960265729UL, discord.Steam.RequestedId);
            Assert.Equal(["Steam IDs for Player:"], discord.Single("send message").EmbedTitles);
        }
    }
}
