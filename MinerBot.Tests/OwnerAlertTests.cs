using System.Text.Json;
using Discord.Net;
using Microsoft.Extensions.DependencyInjection;
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

        // A message about a dashboard request holds a name and someone's words, so the bot's history keeps the bot's own words
        // in its place, sent or failed, because Admins read it
        [Fact]
        public async Task RecordedTextReplacesTheMessage()
        {
            await using var discord = await FakeDiscord.StartAsync();
            var alerts = discord.Services.GetRequiredService<OwnerAlertService>();

            await alerts.SendAsync("Pause posts, asked for by `Jane`", "request 1: Pause posts, done");

            Assert.Equal("Pause posts, asked for by `Jane`", discord.Single(SendStep).Content);
            Assert.Equal("request 1: Pause posts, done", Assert.Single(discord.Recorder.PeekEvents(100), row => row.kind == BotEvents.AlertSent).detail);

            discord.FailStep = SendStep;
            await Assert.ThrowsAsync<HttpException>(() => alerts.SendAsync("Resume posts, asked for by `Jane`", "request 2: Resume posts, done"));

            Assert.Equal("HttpException: request 2: Resume posts, done", Assert.Single(discord.Recorder.PeekEvents(100), row => row.kind == BotEvents.AlertFailed).detail);
            Assert.DoesNotContain(discord.Recorder.PeekEvents(100), row => row.detail != null && row.detail.Contains("Jane"));
        }

        // Steam's AlertOwner is the one-argument method, whose text is the bot's own, recorded as it is
        [Fact]
        public async Task OneArgumentSendAsyncStillRecordsTheMessage()
        {
            await using var discord = await FakeDiscord.StartAsync();
            Func<string, Task> alertOwner = discord.Services.GetRequiredService<OwnerAlertService>().SendAsync;

            await alertOwner(Alert);

            Assert.Equal(Alert, discord.Single(SendStep).Content);
            Assert.Equal(Alert, Assert.Single(discord.Recorder.PeekEvents(100), row => row.kind == BotEvents.AlertSent).detail);
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
