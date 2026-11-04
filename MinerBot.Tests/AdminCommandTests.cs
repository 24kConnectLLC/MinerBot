using DataAccessLibrary.Discord.Models;

namespace MinerBot.Tests
{
    // The bot has View Channel, Send Messages and Embed Links in the first guild, apart from the channels that take one away.
    // Every reply here is private, so only the admin sees it.
    [Collection("Discord")]
    public class AdminCommandTests
    {
        const string ArmorOption = "armor-channel", MapOption = "map-channel", LobbyOption = "lobby-channel", AnnouncementsOption = "announcements-channel";

        static string Message(string text) => $$"""{"name":"message","type":3,"value":"{{text}}"}""";

        static Guild WithAnnouncements(ulong channelId) => new() { guild_id = FakeDiscord.GuildId, announcements_channel_id = channelId };

        // set_channels

        [Fact]
        public async Task SetChannelsSavesEveryChannelTheBotCanPostIn()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSetChannelsAsync((ArmorOption, FakeDiscord.ChannelId), (MapOption, FakeDiscord.AnnouncementChannelId),
                (LobbyOption, FakeDiscord.ChannelId), (AnnouncementsOption, FakeDiscord.AnnouncementChannelId));

            Assert.Equal(["defer", "SaveGuild", "followup"], discord.StepNames);
            Assert.True(discord.Single("defer").IsEphemeral);

            var reply = discord.Single("followup");
            Assert.True(reply.IsEphemeral);
            Assert.Equal(
                "Saved. This is where I'll post in this server:\n" +
                $"- Armor styles: <#{FakeDiscord.ChannelId}>\n" +
                $"- Workshop maps: <#{FakeDiscord.AnnouncementChannelId}>\n" +
                $"- Lobbies: <#{FakeDiscord.ChannelId}>\n" +
                $"- Announcements: <#{FakeDiscord.AnnouncementChannelId}>\n" +
                "Any kind you leave out of /admin set_channels is turned off. See these again with /admin show_channels.",
                reply.Content);

            Assert.Equivalent(new Guild
            {
                guild_id = FakeDiscord.GuildId,
                armor_channel_id = FakeDiscord.ChannelId,
                map_channel_id = FakeDiscord.AnnouncementChannelId,
                lobby_channel_id = FakeDiscord.ChannelId,
                announcements_channel_id = FakeDiscord.AnnouncementChannelId
            }, discord.Guilds.Saved);
        }

        [Fact]
        public async Task SetChannelsTurnsOffEveryKindThatIsLeftOut()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSetChannelsAsync((LobbyOption, FakeDiscord.ChannelId));

            Assert.Equivalent(new Guild
            {
                guild_id = FakeDiscord.GuildId,
                armor_channel_id = Guild.ChannelOff,
                map_channel_id = Guild.ChannelOff,
                lobby_channel_id = FakeDiscord.ChannelId,
                announcements_channel_id = Guild.ChannelOff
            }, discord.Guilds.Saved);

            Assert.StartsWith(
                $"Saved. This is where I'll post in this server:\n- Armor styles: off\n- Workshop maps: off\n- Lobbies: <#{FakeDiscord.ChannelId}>\n- Announcements: off\n",
                discord.Single("followup").Content);
        }

        [Fact]
        public async Task SetChannelsWithNoChannelsTurnsEverythingOff()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSetChannelsAsync();

            Assert.Equal(["defer", "SaveGuild", "followup"], discord.StepNames);
            Assert.Equivalent(new Guild
            {
                guild_id = FakeDiscord.GuildId,
                armor_channel_id = Guild.ChannelOff,
                map_channel_id = Guild.ChannelOff,
                lobby_channel_id = Guild.ChannelOff,
                announcements_channel_id = Guild.ChannelOff
            }, discord.Guilds.Saved);

            Assert.StartsWith(
                "Saved. This is where I'll post in this server:\n- Armor styles: off\n- Workshop maps: off\n- Lobbies: off\n- Announcements: off\n",
                discord.Single("followup").Content);
        }

        // Without Send Messages Discord takes Embed Links away too, and without View Channel everything.
        // The channel of both the armor and the announcements is listed once.
        [Fact]
        public async Task SetChannelsNamesEachChannelTheBotCanNotPostInAndSavesNothing()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSetChannelsAsync((ArmorOption, FakeDiscord.NoSendChannelId), (MapOption, FakeDiscord.HiddenChannelId),
                (LobbyOption, FakeDiscord.NoEmbedChannelId), (AnnouncementsOption, FakeDiscord.NoSendChannelId));

            Assert.Equal(["defer", "followup"], discord.StepNames);
            Assert.True(discord.Single("defer").IsEphemeral);

            var reply = discord.Single("followup");
            Assert.True(reply.IsEphemeral);
            Assert.Equal(
                "Nothing was saved, because I'm missing permissions in these channels:\n" +
                $"- <#{FakeDiscord.NoSendChannelId}>: Send Messages, Embed Links\n" +
                $"- <#{FakeDiscord.HiddenChannelId}>: View Channel, Send Messages, Embed Links\n" +
                $"- <#{FakeDiscord.NoEmbedChannelId}>: Embed Links\n" +
                "Give me these permissions there (or pick other channels), then run /admin set_channels again.",
                reply.Content);

            Assert.Null(discord.Guilds.Saved);
        }

        // show_channels

        [Fact]
        public async Task ShowChannelsShowsEachKindWithItsChannelOrOff()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Guilds.Guild = new Guild { guild_id = FakeDiscord.GuildId, armor_channel_id = FakeDiscord.ChannelId, lobby_channel_id = FakeDiscord.AnnouncementChannelId };

            await discord.SendSlashCommandAsync("admin", "show_channels");

            Assert.Equal(["defer", "GetGuild", "followup"], discord.StepNames);
            Assert.True(discord.Single("defer").IsEphemeral);

            var reply = discord.Single("followup");
            Assert.True(reply.IsEphemeral);
            Assert.Equal(
                "This is where I post in this server:\n" +
                $"- Armor styles: <#{FakeDiscord.ChannelId}>\n" +
                "- Workshop maps: off\n" +
                $"- Lobbies: <#{FakeDiscord.AnnouncementChannelId}>\n" +
                "- Announcements: off\n" +
                "Change these with /admin set_channels. Any kind you leave out there is turned off.",
                reply.Content);
        }

        [Fact]
        public async Task ShowChannelsOfAServerThatSetNothingShowsEveryKindOff()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("admin", "show_channels");

            Assert.StartsWith(
                "This is where I post in this server:\n- Armor styles: off\n- Workshop maps: off\n- Lobbies: off\n- Announcements: off\n",
                discord.Single("followup").Content);
        }

        [Fact]
        public async Task ShowChannelsIsRefusedForAMemberWithoutAdministrator()
        {
            await using var discord = await FakeDiscord.StartAsync();

            await discord.SendSlashCommandAsync("admin", "show_channels", userId: discord.OtherUserId);

            Assert.Equal(["respond"], discord.StepNames);

            var reply = discord.Single("respond");
            Assert.True(reply.IsEphemeral);
            Assert.Equal("User requires guild permission Administrator.", reply.Content);
        }

        // announce

        // The announcement is plain text, so it needs no Embed Links
        [Theory]
        [InlineData(FakeDiscord.AnnouncementChannelId)]
        [InlineData(FakeDiscord.NoEmbedChannelId)]
        public async Task AnnounceGoesToTheAnnouncementsChannel(ulong channel)
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Guilds.Guild = WithAnnouncements(channel);

            await discord.SendSlashCommandAsync("admin", "announce", Message("Hello"));

            Assert.Equal(["defer", "GetGuild", "send message", "followup"], discord.StepNames);

            // The defer decides who sees the reply, as Discord keeps the first followup after it the same whatever that asks for
            Assert.True(discord.Single("defer").IsEphemeral);

            var sent = discord.Single("send message");
            Assert.Equal(channel, sent.SentTo);
            Assert.Equal("Hello", sent.Content);

            var reply = discord.Single("followup");
            Assert.True(reply.IsEphemeral);
            Assert.Equal($"Sent your announcement to <#{channel}>.", reply.Content);
        }

        // A server that set its channels without an announcements one has a row with 0 there
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AnnounceWithoutAnAnnouncementsChannelSendsNothingAndSaysSo(bool serverHasARow)
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Guilds.Guild = serverHasARow ? FakeGuildData.CreateGuild(FakeDiscord.ChannelId) : null;

            await discord.SendSlashCommandAsync("admin", "announce", Message("Hello"));

            Assert.Equal(["defer", "GetGuild", "followup"], discord.StepNames);
            Assert.Equal(
                "Nothing was sent, because this server has no announcements channel. Choose one with /admin set_channels, and include the other channels you want to keep, as any you leave out are turned off.",
                discord.Single("followup").Content);
        }

        // A deleted channel leaves the cache, so there's no "get channel" to ask Discord
        [Fact]
        public async Task AnnounceToADeletedChannelSaysItNoLongerExists()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Guilds.Guild = WithAnnouncements(FakeDiscord.DeletedChannelId);

            await discord.SendSlashCommandAsync("admin", "announce", Message("Hello"));

            Assert.Equal(["defer", "GetGuild", "followup"], discord.StepNames);
            Assert.Equal(
                "Nothing was sent, because the announcements channel set for this server no longer exists. Choose a new one with /admin set_channels, and include the other channels you want to keep, as any you leave out are turned off.",
                discord.Single("followup").Content);
        }

        [Theory]
        [InlineData(FakeDiscord.NoSendChannelId, "Send Messages")]
        [InlineData(FakeDiscord.HiddenChannelId, "View Channel, Send Messages")]
        public async Task AnnounceToAChannelTheBotCanNotPostInNamesWhatIsMissing(ulong channel, string missing)
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Guilds.Guild = WithAnnouncements(channel);

            await discord.SendSlashCommandAsync("admin", "announce", Message("Hello"));

            Assert.Equal(["defer", "GetGuild", "followup"], discord.StepNames);
            Assert.Equal(
                $"Nothing was sent, because I'm missing permissions in <#{channel}>: {missing}. Give me View Channel and Send Messages there, then try again.",
                discord.Single("followup").Content);
        }

        // The cache can say the bot may post when Discord says it may not, such as for a bot that's timed out
        [Fact]
        public async Task AnnounceThatDiscordRefusesSaysSo()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Guilds.Guild = WithAnnouncements(FakeDiscord.ChannelId);
            discord.FailStep = "send message";

            await discord.SendSlashCommandAsync("admin", "announce", Message("Hello"));

            Assert.Equal(["defer", "GetGuild", "send message", "followup"], discord.StepNames);
            Assert.Equal(
                $"Nothing was sent, because Discord didn't let me post in <#{FakeDiscord.ChannelId}>. Check that I have View Channel and Send Messages there, then try again.",
                discord.Single("followup").Content);
            Assert.Contains($"[Interaction/Warning] Discord refused an announcement in channel {FakeDiscord.ChannelId} of guild {FakeDiscord.GuildId}", discord.ConsoleText);
        }
    }
}
