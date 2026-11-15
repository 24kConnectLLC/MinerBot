using System.Text.Json;
using Discord.Net;
using MinerBot_2._0.Services;
using MinerBot_2._0.Status;

namespace MinerBot.Tests
{
    [Collection("Discord")]
    public class OwnerAlertTests
    {
        const string OpenStep = "open direct message";
        const string SendStep = "send message";
        const string Alert = "Steam needs attention @everyone";

        // Someone who is neither the application's owner nor the team's
        const ulong AlertUserId = 7001;

        static ulong Recipient(FakeDiscord discord) =>
            ulong.Parse(JsonDocument.Parse(discord.Single(OpenStep).Body).RootElement.GetProperty("recipient_id").GetString()!);

        [Fact]
        public async Task AlertGoesToTheUserTheSettingNames()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.TeamOwnerId = discord.OtherUserId;
            discord.Settings[OwnerAlertService.RecipientSetting] = AlertUserId.ToString();

            await discord.SendOwnerAlertAsync(Alert);

            var sent = discord.Single(SendStep);

            Assert.Equal(AlertUserId, Recipient(discord));
            Assert.Equal(Alert, sent.Content);
            Assert.True(sent.AllowsNoMentions);
        }

        [Theory]
        [InlineData("not an id")]
        [InlineData("0")]
        public async Task SettingThatIsNotAUserIdLeavesTheAlertWithTheTeamOwner(string setting)
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.TeamOwnerId = discord.OtherUserId;
            discord.Settings[OwnerAlertService.RecipientSetting] = setting;

            await discord.SendOwnerAlertAsync(Alert);

            Assert.Equal(discord.OtherUserId, Recipient(discord));
            Assert.Contains($"{OwnerAlertService.RecipientSetting} isn't a Discord user ID", discord.ConsoleText);
        }

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

        // Every alert goes into the bot's history too. Its text is the bot's own wording.
        [Fact]
        public async Task SentAlertIsRecorded()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendOwnerAlertAsync(Alert);

            Assert.Equal(Alert, Assert.Single(discord.Recorder.PeekEvents(100), row => row.kind == BotEvents.AlertSent).detail);
            Assert.DoesNotContain(discord.Recorder.PeekEvents(100), row => row.kind == BotEvents.AlertFailed);
        }

        // With the exception's type, never Discord's own text, and the start of the alert, an emoji kept whole
        [Fact]
        public async Task FailedAlertIsRecordedWithItsReason()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.FailStep = SendStep;
            var longAlert = new string('a', 199) + "\U0001F600" + new string('b', 100);

            await Assert.ThrowsAsync<HttpException>(() => discord.SendOwnerAlertAsync(Alert));
            await Assert.ThrowsAsync<HttpException>(() => discord.SendOwnerAlertAsync(longAlert));

            Assert.Equal([$"HttpException: {Alert}", "HttpException: " + new string('a', 199)],
                discord.Recorder.PeekEvents(100).Where(row => row.kind == BotEvents.AlertFailed).Select(row => row.detail));
            Assert.DoesNotContain(discord.Recorder.PeekEvents(100), row => row.kind == BotEvents.AlertSent);
        }
    }
}
