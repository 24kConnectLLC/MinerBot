using System.Text.RegularExpressions;

namespace MinerBot.Tests
{
    [Collection("Discord")]
    public class InteractionFailureTests
    {
        const string FailedCommandReply = "Something went wrong with that command. Please try again later.";

        [Fact]
        public async Task FailureBeforeAnyReplyGetsAnEphemeralResponse()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("test", "fail");

            var reply = discord.Single("respond");
            Assert.Equal(FailedCommandReply, reply.Content);
            Assert.True(reply.IsEphemeral);
            Assert.True(reply.AllowsNoMentions);
            Assert.DoesNotContain(TestCommands.PrivateDetail, reply.Body);
            Assert.Equal(["respond"], discord.StepNames);
        }

        [Fact]
        public async Task FailureAfterDeferGetsAFollowup()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("test", "defer-and-fail");

            Assert.Equal(["defer", "followup"], discord.StepNames);

            // This is what the bot asks for. Discord keeps the first followup public after a public defer.
            var reply = discord.Single("followup");
            Assert.Equal(FailedCommandReply, reply.Content);
            Assert.True(reply.IsEphemeral);
            Assert.True(reply.AllowsNoMentions);
            Assert.DoesNotContain(TestCommands.PrivateDetail, reply.Body);
        }

        [Fact]
        public async Task FailureIsLoggedWithItsDetails()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("test", "fail");

            Assert.Contains("[Interaction/Warning] fail failed for", discord.ConsoleText);
            Assert.Contains(TestCommands.PrivateDetail, discord.ConsoleText);
        }

        [Fact]
        public async Task SuccessGetsNoFailureReplyOrLogLine()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("test", "succeed");

            Assert.Equal("Done", discord.Single("respond").Content);
            Assert.Equal(["respond"], discord.StepNames);
            Assert.DoesNotContain("[Interaction/", discord.ConsoleText);
        }

        [Fact]
        public async Task CooldownRepliesWithItsTime()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("murder-miners", "lobbies");
            await discord.SendSlashCommandAsync("murder-miners", "lobbies");

            var reply = discord.Steps[^1];
            Assert.Equal("respond", reply.Name);
            Assert.StartsWith("This command is usable <t:", reply.Content);
            Assert.True(reply.IsEphemeral);
            Assert.Single(discord.Steps, step => step.Name == nameof(FakeGetData.GetServerList));
        }

        [Fact]
        public async Task CooldownShowsTheTimeOfTheRefusedCommand()
        {
            await using var discord = await FakeDiscord.StartAsync();

            // /armor list has a cooldown of 5 seconds and /more info one of 3 seconds
            var before = DateTimeOffset.UtcNow;
            await discord.SendSlashCommandAsync("armor", "list");
            var after = DateTimeOffset.UtcNow;

            await discord.SendSlashCommandAsync("more", "info");
            await discord.SendSlashCommandAsync("armor", "list");

            var usableAt = long.Parse(Regex.Match(discord.Steps[^1].Content!, @"<t:(\d+):R>").Groups[1].Value);
            Assert.InRange(usableAt, before.AddSeconds(5).ToUnixTimeSeconds(), after.AddSeconds(5).ToUnixTimeSeconds());
        }

        [Fact]
        public async Task MissingPermissionRepliesWithTheReason()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("admin", "announce", """{"name":"message","type":3,"value":"Hello"}""", userId: discord.OtherUserId);

            var reply = discord.Single("respond");
            Assert.Contains("Administrator", reply.Content);
            Assert.True(reply.IsEphemeral);
        }

        [Fact]
        public async Task ButtonOfAnotherUserIsRefused()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendButtonAsync($"maplist:{discord.UserId},0,next", userId: discord.OtherUserId);

            var reply = discord.Single("respond");
            Assert.Equal("Only the person who ran this command can use these buttons.", reply.Content);
            Assert.True(reply.IsEphemeral);
            Assert.Equal(["respond"], discord.StepNames);
        }

        [Fact]
        public async Task BadInputIsNotRepeatedBack()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("armor", "list", """{"name":"helmet-type","type":3,"value":"typed by the user"}""");

            var reply = discord.Single("respond");
            Assert.Equal("That input didn't work. Please check it and try again.", reply.Content);
            Assert.True(reply.IsEphemeral);
            Assert.DoesNotContain("typed by the user", reply.Body);
        }

        [Fact]
        public async Task UnknownButtonGetsAReplyAndALogLine()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendButtonAsync("not-a-button");

            var reply = discord.Single("respond");
            Assert.Equal("That command or button isn't available any more.", reply.Content);
            Assert.True(reply.IsEphemeral);
            Assert.Contains("[Interaction/Warning] Unknown command failed for", discord.ConsoleText);
        }

        [Fact]
        public async Task UnknownSlashCommandGetsAReply()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("not-a-group", "not-a-command");

            Assert.Equal("That command or button isn't available any more.", discord.Single("respond").Content);
        }

        [Fact]
        public async Task FailedAutocompleteIsOnlyLogged()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendUnknownAutocompleteAsync();

            Assert.Empty(discord.Steps);
            Assert.Contains("[Interaction/Warning] Unknown command failed for", discord.ConsoleText);
            Assert.DoesNotContain("[Interaction/Error]", discord.ConsoleText);
        }

        [Theory]
        [InlineData("fail", "respond")]
        [InlineData("defer-and-fail", "followup")]
        public async Task ReplyThatCanNotBeSentIsLoggedAndStopsThere(string command, string replyStep)
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.FailStep = replyStep;

            await discord.SendSlashCommandAsync("test", command);

            Assert.Equal(1, discord.ExecutedEvents);
            Assert.Single(discord.Steps, step => step.Name == replyStep);
            Assert.Contains("[Interaction/Error] Couldn't reply to a failed interaction", discord.ConsoleText);
        }

        [Fact]
        public async Task InteractionPastTheReplyLimitIsLoggedAndStopsThere()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("test", "fail", age: TimeSpan.FromSeconds(10));

            Assert.Empty(discord.Steps);
            Assert.Equal(1, discord.ExecutedEvents);
            Assert.Contains("[Interaction/Error] Couldn't reply to a failed interaction", discord.ConsoleText);
        }
    }
}
