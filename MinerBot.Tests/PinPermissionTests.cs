namespace MinerBot.Tests
{
    // The bot pins with its own Pin Messages permission, so it only pins for a member who could pin in that channel themselves.
    // The second user isn't the guild's owner, so only the roles a test gives it count.
    [Collection("Discord")]
    public class PinPermissionTests
    {
        [Fact]
        public async Task MemberWhoCanPinGetsTheMessagePinned()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.MemberRoles = [FakeDiscord.PinnerRoleId];

            await discord.SendMessageCommandAsync("pin", userId: discord.OtherUserId);

            Assert.Equal(["defer", "get pins", "pin", "followup"], discord.StepNames);
        }

        // Manage Channels was the old check, and doesn't let anyone pin. The other role can pin, except in this channel.
        [Theory]
        [InlineData(FakeDiscord.ChannelManagerRoleId)]
        [InlineData(FakeDiscord.DeniedPinnerRoleId)]
        public async Task MemberWhoCantPinInTheChannelIsRefused(ulong role)
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.MemberRoles = [role];

            await discord.SendMessageCommandAsync("pin", userId: discord.OtherUserId);

            Assert.Equal(["respond"], discord.StepNames);

            var reply = discord.Single("respond");
            Assert.True(reply.IsEphemeral);
            Assert.Equal("User requires channel permission PinMessages.", reply.Content);
        }

        [Fact]
        public async Task PinIsRefusedInADirectMessage()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendMessageCommandAsync("pin", direct: true);

            Assert.Equal(["respond"], discord.StepNames);

            var reply = discord.Single("respond");
            Assert.True(reply.IsEphemeral);
            Assert.Contains("accepted contexts: Guild", reply.Content);
        }
    }
}
