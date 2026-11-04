using System.Text.RegularExpressions;
using Discord;
using MinerBot_2._0.Handlers;

namespace MinerBot.Tests
{
    // Joins and leaves right after a start, with no notification run before them. Without the presences intent Discord
    // lists only the bot and members in voice with a join, so these join events list the bot with its roles.
    [Collection("Discord")]
    public class GuildEventTests
    {
        const string SendStep = "send message";

        // Channels of the joined guild
        const ulong SystemChannelId = FakeDiscord.JoinedChannelId, PublicUpdatesChannelId = 3101;

        static string Bot(params ulong[] roles) => $"[{FakeDiscord.BotMemberJson(roles)}]";

        // Joins

        [Fact]
        public async Task JoiningAServerAsAdministratorSendsTheWelcomeMessage()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.JoinGuildAsync(Bot(FakeDiscord.BotRoleId));

            var welcome = discord.Single(SendStep);
            Assert.Equal(FakeDiscord.JoinedChannelId, welcome.SentTo);
            Assert.Equal(GuildEventHandler.WelcomeMessage, welcome.Content);
        }

        [Fact]
        public async Task JoiningAServerWithoutAdministratorStillSendsTheWelcomeMessage()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.JoinGuildAsync(Bot(FakeDiscord.JoinedPosterRoleId));

            Assert.Equal(FakeDiscord.JoinedChannelId, discord.Single(SendStep).SentTo);
        }

        [Fact]
        public async Task WelcomeMessageGoesToThePublicUpdatesChannelFirst()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.JoinGuildAsync(Bot(FakeDiscord.JoinedPosterRoleId),
                $"[{FakeDiscord.ChannelJson(SystemChannelId, 0)},{FakeDiscord.ChannelJson(PublicUpdatesChannelId, 1)}]",
                systemChannelId: SystemChannelId, publicUpdatesChannelId: PublicUpdatesChannelId);

            Assert.Equal(PublicUpdatesChannelId, discord.Single(SendStep).SentTo);
        }

        [Fact]
        public async Task WelcomeMessageSkipsAChannelTheBotCanNotPostIn()
        {
            await using var discord = await FakeDiscord.StartAsync();
            var noSend = FakeDiscord.DenyJson(FakeDiscord.JoinedPosterRoleId, ChannelPermission.SendMessages);

            await discord.JoinGuildAsync(Bot(FakeDiscord.JoinedPosterRoleId),
                $"[{FakeDiscord.ChannelJson(SystemChannelId, 0)},{FakeDiscord.ChannelJson(PublicUpdatesChannelId, 1, noSend)}]",
                systemChannelId: SystemChannelId, publicUpdatesChannelId: PublicUpdatesChannelId);

            Assert.Equal(SystemChannelId, discord.Single(SendStep).SentTo);
        }

        // Listed out of order. The top text channel is hidden from the bot, and the chat of a voice channel isn't a place for a welcome.
        [Fact]
        public async Task WelcomeMessageFallsBackToTheTopTextChannelTheBotCanPostIn()
        {
            await using var discord = await FakeDiscord.StartAsync();
            const ulong third = 3103, hidden = 3104, voice = 3105, second = 3106;
            var channels = string.Join(",",
                FakeDiscord.ChannelJson(third, 2),
                FakeDiscord.ChannelJson(hidden, 0, FakeDiscord.DenyJson(FakeDiscord.JoinedPosterRoleId, ChannelPermission.ViewChannel)),
                FakeDiscord.ChannelJson(voice, 0, type: 2),
                FakeDiscord.ChannelJson(second, 1));

            await discord.JoinGuildAsync(Bot(FakeDiscord.JoinedPosterRoleId), $"[{channels}]", systemChannelId: null);

            Assert.Equal(second, discord.Single(SendStep).SentTo);
        }

        // @everyone has no permissions in the joined guild
        [Fact]
        public async Task JoiningAServerWhereTheBotCanPostNowhereLogsOneLine()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.JoinGuildAsync(Bot());

            Assert.DoesNotContain(SendStep, discord.StepNames);
            Assert.Single(Regex.Matches(discord.ConsoleText, $"Couldn't welcome guild {FakeDiscord.JoinedGuildId}"));
        }

        [Fact]
        public async Task WelcomeMessageThatDiscordRefusesIsLoggedWithTheGuild()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.FailStep = SendStep;

            await discord.JoinGuildAsync(Bot(FakeDiscord.BotRoleId));

            Assert.Single(discord.Steps, step => step.Name == SendStep);
            Assert.Contains($"Couldn't send the welcome message to guild {FakeDiscord.JoinedGuildId}", discord.ConsoleText);
            Assert.Contains("Missing Permissions", discord.ConsoleText);
        }

        // Leaves

        [Fact]
        public async Task LeavingAServerRightAfterAStartForgetsIt()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Guilds.Guild = FakeGuildData.CreateGuild(FakeDiscord.ChannelId);

            await discord.LeaveGuildAsync();

            Assert.Equal([nameof(FakeGuildData.GetGuild), nameof(FakeGuildData.DeleteGuild)], discord.StepNames);
        }

        [Fact]
        public async Task GuildEventsAreHookedOnlyOnce()
        {
            await using var discord = await FakeDiscord.StartAsync();

            for (int run = 0; run < 3; run++)
                await discord.RunNotificationJobAsync();

            await discord.LeaveGuildAsync();

            Assert.Single(discord.Steps, step => step.Name == nameof(FakeGuildData.GetGuild));
        }

        // Outages

        // Discord.Net takes such a guild as a join, both for a server that has just added the bot and for one that already had it
        [Theory]
        [InlineData(FakeDiscord.JoinedGuildId)]
        [InlineData(FakeDiscord.GuildId)]
        public async Task GuildThatComesInUnavailableIsLoggedAsAnOutage(ulong guildId)
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendUnavailableGuildAsync(guildId);

            Assert.Empty(discord.Steps);
            Assert.Contains($"Discord sent guild {guildId} as unavailable, which happens during an outage", discord.ConsoleText);
            Assert.DoesNotContain("Couldn't welcome", discord.ConsoleText);
        }

        // Discord also sends every server again after each start, the way it does here once the outage is over
        [Fact]
        public async Task ServerThatIsUnavailableForAWhileIsNeitherForgottenNorWelcomedAgain()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.MakeGuildUnavailableAsync();
            await discord.MakeGuildAvailableAsync();

            Assert.Empty(discord.Steps);
        }
    }
}
