using System.Text.Json;
using Discord.Net;

namespace MinerBot.Tests
{
    [Collection("Discord")]
    public class OwnerAlertTests
    {
        const string OpenStep = "open direct message";
        const string SendStep = "send message";
        const string Alert = "Steam needs attention @everyone";

        static ulong Recipient(FakeDiscord discord) =>
            ulong.Parse(JsonDocument.Parse(discord.Single(OpenStep).Body).RootElement.GetProperty("recipient_id").GetString()!);

        [Fact]
        public async Task AlertGoesToTheApplicationOwnerAsADirectMessage()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendOwnerAlertAsync(Alert);

            var sent = discord.Single(SendStep);

            Assert.Equal(discord.UserId, Recipient(discord));
            Assert.Equal(Alert, sent.Content);
            Assert.True(sent.AllowsNoMentions);
        }

        [Fact]
        public async Task AlertForATeamApplicationGoesToTheTeamOwner()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.TeamOwnerId = discord.OtherUserId;

            await discord.SendOwnerAlertAsync(Alert);

            Assert.Equal(discord.OtherUserId, Recipient(discord));
            Assert.Equal(Alert, discord.Single(SendStep).Content);
        }

        [Fact]
        public async Task AlertWaitsUntilDiscordIsReady()
        {
            await using var discord = await FakeDiscord.StartAsync(holdReady: true);

            var alert = discord.SendOwnerAlertAsync(Alert);

            await Task.Delay(200, TestContext.Current.CancellationToken);

            Assert.DoesNotContain(OpenStep, discord.StepNames);

            await discord.ReleaseReadyAsync();
            await alert.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            Assert.Equal(Alert, discord.Single(SendStep).Content);
        }

        [Fact]
        public async Task ClosedDirectMessagesFailTheAlert()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.FailStep = SendStep;

            await Assert.ThrowsAsync<HttpException>(() => discord.SendOwnerAlertAsync(Alert));
        }
    }
}
