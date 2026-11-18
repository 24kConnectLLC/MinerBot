using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Guilds.Models;
using Discord;
using Microsoft.Extensions.Logging;
using MinerBot_2._0.Status;
using MySql.Data.MySqlClient;

namespace MinerBot.Tests
{
    // The bot's copy of its servers, written from Discord.Net's cache as the gateway's events reach it through FakeDiscord,
    // with FakeBotGuildData standing in for MinerBot's database. The ticks are called one by one here, on a clock the test
    // moves, except where the loop itself is what's tested.
    [Collection("Discord")]
    public class GuildWriterTests
    {
        // Made up, and never connected to
        const string Connection = "Server=127.0.0.1;Database=bot_db;Uid=minerbot-bot;Pwd=password";

        static readonly DateTimeOffset Noon = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

        // What the bot's role in the first guild gives it: View Channel, Send Messages and Embed Links
        const ulong PosterPermissions = 1024 | 2048 | 16384;

        readonly FakeBotGuildData data = new();
        readonly RecordingLogger<GuildWriter> logger = new();

        GuildWriter Writer(FakeDiscord discord, GuildWatcher? watcher = null, string? connection = Connection, TimeProvider? clock = null) =>
            new(discord.Client, watcher ?? discord.Watcher, data, discord.Recorder, BotRecorderTests.Settings(botConnection: connection), clock ?? discord.Clock, logger)
            {
                Interval = TimeSpan.FromMilliseconds(20)
            };

        static async Task<FakeDiscord> StartAsync(ulong[]? unavailableGuilds = null)
        {
            var discord = await FakeDiscord.StartAsync(unavailableGuilds: unavailableGuilds);
            discord.Clock.UtcNow = Noon;
            return discord;
        }

        static Task TickAsync(GuildWriter writer) => writer.TickAsync(TestContext.Current.CancellationToken);

        static BotGuildChannel Channel(ulong id, string name, int position, bool view = true, bool send = true, bool embed = true, string type = GuildChannelType.Text,
            string? category = null, ulong? categoryId = null, int? categoryPosition = null) => new()
        {
            channel_id = id,
            name = name,
            type = type,
            category = category,
            category_id = categoryId,
            category_position = categoryPosition,
            position = position,
            can_view = view,
            can_send = send,
            can_embed = embed
        };

        // The first guild as FakeDiscord sends it, where every channel but general and the announcements takes something away
        // from the bot's role
        static readonly BotGuild FirstGuild = new()
        {
            guild_id = FakeDiscord.GuildId,
            name = "Guild",
            member_count = 2,
            bot_joined_at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            available = true,
            bot_permissions = PosterPermissions
        };

        static readonly BotGuildChannel[] FirstGuildChannels =
        [
            Channel(FakeDiscord.ChannelId, "general", 0),
            Channel(FakeDiscord.NoSendChannelId, "channel-3003", 1, send: false, embed: false),
            Channel(FakeDiscord.HiddenChannelId, "___hidden___", 2, view: false, send: false, embed: false),
            Channel(FakeDiscord.NoEmbedChannelId, "channel-3005", 3, embed: false),
            Channel(FakeDiscord.AnnouncementChannelId, "channel-3006", 4, type: GuildChannelType.Announcement)
        ];

        // MySql.Data's messages can name the login, as this one does
        static MySqlException Refused(int number) =>
            (MySqlException)typeof(MySqlException)
                .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string), typeof(int), typeof(Exception)])!
                .Invoke([$"Access denied for user 'minerbot-bot'@'localhost'", number, new SocketException()]);

        static async Task WaitUntilAsync(Func<bool> condition, string failure)
        {
            var clock = Stopwatch.StartNew();

            while (!condition())
            {
                Assert.True(clock.Elapsed < WaitLimit, failure);
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        // The session's Ready notes every server, and the table, new here, gets each one with its channels, as joined
        [Fact]
        public async Task FirstSessionWritesEveryServerWithItsChannels()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);

            await TickAsync(writer);

            var saved = Assert.Single(data.Saved);
            Assert.Equal(FirstGuild, saved.Guild);
            Assert.Equal(FirstGuildChannels, saved.Channels);
            Assert.Equal(Noon.UtcDateTime, saved.At);
            Assert.Equal([(1u, FakeDiscord.GuildId, GuildEventKind.Joined)], data.Events);
            Assert.Empty(data.Leaves);

            // No Discord request and no call to the website's database
            Assert.Empty(discord.Steps);
        }

        // A new channel in a category, one renamed and moved, and one deleted, all in one server: one write at the next tick
        [Fact]
        public async Task ChannelsThatChangeAreWrittenTogetherAtTheNextTick()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);
            await TickAsync(writer);

            await discord.DeliverAsync("CHANNEL_CREATE", FakeDiscord.ChannelEventJson(FakeDiscord.GuildId, 3060, 5, type: 4, name: "Topics"));
            await discord.DeliverAsync("CHANNEL_CREATE", FakeDiscord.ChannelEventJson(FakeDiscord.GuildId, 3050, 9, name: "new-channel", parentId: 3060));
            await discord.DeliverAsync("CHANNEL_UPDATE", FakeDiscord.ChannelEventJson(FakeDiscord.GuildId, FakeDiscord.ChannelId, 7, name: "renamed"));
            await discord.DeliverAsync("CHANNEL_DELETE", FakeDiscord.ChannelEventJson(FakeDiscord.GuildId, FakeDiscord.NoEmbedChannelId, 3));

            Assert.Single(data.Saved);
            await TickAsync(writer);

            Assert.Equal(2, data.Saved.Count);
            Assert.Equal(
            [
                Channel(FakeDiscord.ChannelId, "renamed", 7),
                FirstGuildChannels[1],
                FirstGuildChannels[2],
                FirstGuildChannels[4],
                Channel(3050, "new-channel", 9, category: "Topics", categoryId: 3060, categoryPosition: 5)
            ], data.Saved[^1].Channels);
            Assert.Single(data.Events);
        }

        // Discord lists a server's channels by category, in the categories' order, so a category that moves changes the rows of
        // its channels, though nothing of the channels did, and two categories of the same name stay apart
        [Fact]
        public async Task CategoryThatMovesIsWrittenWithItsChannels()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);
            await TickAsync(writer);

            await discord.DeliverAsync("CHANNEL_CREATE", FakeDiscord.ChannelEventJson(FakeDiscord.GuildId, 3060, 5, type: 4, name: "Topics"));
            await discord.DeliverAsync("CHANNEL_CREATE", FakeDiscord.ChannelEventJson(FakeDiscord.GuildId, 3061, 6, type: 4, name: "Topics"));
            await discord.DeliverAsync("CHANNEL_CREATE", FakeDiscord.ChannelEventJson(FakeDiscord.GuildId, 3050, 0, name: "first", parentId: 3060));
            await discord.DeliverAsync("CHANNEL_CREATE", FakeDiscord.ChannelEventJson(FakeDiscord.GuildId, 3051, 0, name: "second", parentId: 3061));
            discord.Clock.UtcNow = Noon.AddSeconds(5);
            await TickAsync(writer);

            Assert.Equal(
            [
                Channel(3050, "first", 0, category: "Topics", categoryId: 3060, categoryPosition: 5),
                Channel(3051, "second", 0, category: "Topics", categoryId: 3061, categoryPosition: 6)
            ], data.Saved[^1].Channels.Where(channel => channel.channel_id >= 3050));

            // The second category moves above the first
            var saves = data.Saved.Count;
            await discord.DeliverAsync("CHANNEL_UPDATE", FakeDiscord.ChannelEventJson(FakeDiscord.GuildId, 3061, 1, type: 4, name: "Topics"));
            discord.Clock.UtcNow = Noon.AddSeconds(10);
            await TickAsync(writer);

            Assert.Equal(saves + 1, data.Saved.Count);
            Assert.Equal(Channel(3051, "second", 0, category: "Topics", categoryId: 3061, categoryPosition: 1), data.Saved[^1].Channels[^1]);
        }

        // The bot's permissions come from its roles and each channel's overwrites, as DiscordService.MissingPermissions works
        // them out
        [Fact]
        public async Task PermissionsThatChangeThroughAnOverwriteOrARoleAreWritten()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);
            await TickAsync(writer);

            // An overwrite in general takes Send Messages from the bot's role, and with it Embed Links
            await discord.DeliverAsync("CHANNEL_UPDATE", FakeDiscord.ChannelEventJson(FakeDiscord.GuildId, FakeDiscord.ChannelId, 0,
                FakeDiscord.DenyJson(FakeDiscord.PosterRoleId, ChannelPermission.SendMessages), name: "general"));
            await TickAsync(writer);

            Assert.Equal(Channel(FakeDiscord.ChannelId, "general", 0, send: false, embed: false), data.Saved[^1].Channels[0]);

            // The bot's role loses Embed Links everywhere
            await discord.DeliverAsync("GUILD_ROLE_UPDATE", FakeDiscord.RoleEventJson(FakeDiscord.GuildId, FakeDiscord.PosterRoleId, "MinerBot",
                GuildPermission.ViewChannel | GuildPermission.SendMessages));
            await TickAsync(writer);

            var saved = data.Saved[^1];
            Assert.Equal(3, data.Saved.Count);
            Assert.Equal(1024UL | 2048, saved.Guild.bot_permissions);
            Assert.All(saved.Channels, channel => Assert.False(channel.can_embed));
            Assert.Equal(Channel(FakeDiscord.AnnouncementChannelId, "channel-3006", 4, embed: false, type: GuildChannelType.Announcement), saved.Channels[^1]);
        }

        // Discord sends the bot's own member update without the members intent, such as when it's given a role. A new role
        // it doesn't have changes nothing the copy holds, so nothing is written for it.
        [Fact]
        public async Task BotGivenARoleIsWritten()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);
            const ulong adminRoleId = 2005;
            await TickAsync(writer);

            await discord.DeliverAsync("GUILD_ROLE_CREATE", FakeDiscord.RoleEventJson(FakeDiscord.GuildId, adminRoleId, "Admin", GuildPermission.Administrator));
            await TickAsync(writer);

            Assert.Single(data.Saved);

            await discord.DeliverAsync("GUILD_MEMBER_UPDATE", FakeDiscord.BotMemberUpdateJson(FakeDiscord.GuildId, FakeDiscord.PosterRoleId, adminRoleId));
            await TickAsync(writer);

            var saved = data.Saved[^1];
            Assert.Equal((true, PosterPermissions | 8), (saved.Guild.bot_administrator, saved.Guild.bot_permissions));
            Assert.All(saved.Channels, channel => Assert.True(channel.can_view && channel.can_send && channel.can_embed));
        }

        // Another member's update, which Discord would only send with the members intent, notes nothing
        [Fact]
        public async Task OnlyTheBotsOwnMemberUpdateIsNoted()
        {
            await using var discord = await StartAsync();
            await TickAsync(Writer(discord));

            Assert.Empty(discord.Watcher.PeekNotes());

            await discord.DeliverAsync("GUILD_MEMBER_UPDATE", FakeDiscord.MemberUpdateJson(FakeDiscord.GuildId, discord.OtherUserId));
            Assert.Empty(discord.Watcher.PeekNotes());

            await discord.DeliverAsync("GUILD_MEMBER_UPDATE", FakeDiscord.BotMemberUpdateJson(FakeDiscord.GuildId, FakeDiscord.PosterRoleId));
            Assert.Equal([FakeDiscord.GuildId], discord.Watcher.PeekNotes().Select(note => note.GuildId));
        }

        [Fact]
        public async Task JoinsAndLeavesAreWritten()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);
            await TickAsync(writer);

            await discord.JoinGuildAsync($"[{FakeDiscord.BotMemberJson(FakeDiscord.BotRoleId)}]");
            await TickAsync(writer);

            var joined = data.Saved[^1];
            Assert.Equal((FakeDiscord.JoinedGuildId, "New guild", true), (joined.Guild.guild_id, joined.Guild.name, joined.Guild.bot_administrator));
            Assert.Equal([Channel(FakeDiscord.JoinedChannelId, "general", 0)], joined.Channels);

            discord.Clock.UtcNow = Noon.AddMinutes(1);
            await discord.LeaveGuildAsync();
            await TickAsync(writer);

            Assert.Equal([(FakeDiscord.GuildId, Noon.AddMinutes(1).UtcDateTime, (string?)null)], data.Leaves);
            Assert.Equal(
            [
                (1u, FakeDiscord.GuildId, GuildEventKind.Joined),
                (2u, FakeDiscord.JoinedGuildId, GuildEventKind.Joined),
                (3u, FakeDiscord.GuildId, GuildEventKind.Left)
            ], data.Events);
            Assert.Equal(2, data.Saved.Count);
        }

        // Discord.Net marks a server it has just joined connected, and raises GuildAvailable, only once every JoinedGuild handler
        // has returned, GuildEventHandler's welcome message included. Until then the cache has it whole but not connected,
        // which isn't an outage, so it waits, noted, and is written once, as available.
        [Fact]
        public async Task ServerJoinedIsWrittenAsAvailableOnceItsWelcomeIsSent()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);
            await TickAsync(writer);

            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            discord.SendHold = sent.Task;
            var joining = discord.JoinGuildAsync($"[{FakeDiscord.BotMemberJson(FakeDiscord.BotRoleId)}]");
            await WaitUntilAsync(() => discord.StepNames.Contains("send message"), "The welcome message was never sent");

            Assert.False(discord.Client.GetGuild(FakeDiscord.JoinedGuildId).IsConnected);
            discord.Clock.UtcNow = Noon.AddSeconds(5);
            await TickAsync(writer);

            Assert.DoesNotContain(data.Saved, saved => saved.Guild.guild_id == FakeDiscord.JoinedGuildId);
            Assert.Contains(FakeDiscord.JoinedGuildId, discord.Watcher.PeekNotes().Select(note => note.GuildId));

            sent.SetResult();
            await joining.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            discord.Clock.UtcNow = Noon.AddSeconds(10);
            await TickAsync(writer);

            Assert.True(Assert.Single(data.Saved, saved => saved.Guild.guild_id == FakeDiscord.JoinedGuildId).Guild.available);
            Assert.Equal((2u, FakeDiscord.JoinedGuildId, GuildEventKind.Joined), data.Events[^1]);
            Assert.Empty(discord.Watcher.PeekNotes());
        }

        // Once Discord.Net has marked a server just joined connected, it's no longer taken for one joining, so an outage there
        // later is written as one
        [Fact]
        public async Task ServerJoinedAndThenInAnOutageIsWrittenAsUnavailable()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);
            await TickAsync(writer);

            await discord.JoinGuildAsync($"[{FakeDiscord.BotMemberJson(FakeDiscord.BotRoleId)}]");
            discord.Clock.UtcNow = Noon.AddSeconds(5);
            await TickAsync(writer);

            await discord.DeliverAsync("GUILD_DELETE", $$"""{"id":"{{FakeDiscord.JoinedGuildId}}","unavailable":true}""");
            discord.Clock.UtcNow = Noon.AddSeconds(10);
            await TickAsync(writer);

            Assert.Equal([true, false], data.Saved.Where(saved => saved.Guild.guild_id == FakeDiscord.JoinedGuildId).Select(saved => saved.Guild.available));
            Assert.Empty(discord.Watcher.PeekNotes());
        }

        // A join or a leave whose commit landed but whose answer never came, as at the call's limit, recorded its event all the
        // same, and the try after it records nothing. Its number isn't given again, because the table would keep out the next
        // event that took it, though its call says it was recorded.
        [Fact]
        public async Task EventAfterAWriteWhoseAnswerNeverCameGetsANumberOfItsOwn()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);
            var timedOut = new TimeoutException("The database didn't answer within 15 seconds");

            data.FailureAfterLanding = timedOut;
            await TickAsync(writer);
            discord.Clock.UtcNow = Noon.AddSeconds(30);
            await TickAsync(writer);

            await discord.JoinGuildAsync($"[{FakeDiscord.BotMemberJson(FakeDiscord.BotRoleId)}]");
            discord.Clock.UtcNow = Noon.AddSeconds(35);
            await TickAsync(writer);

            data.FailureAfterLanding = timedOut;
            discord.Clock.UtcNow = Noon.AddSeconds(40);
            await discord.LeaveGuildAsync();
            await TickAsync(writer);
            discord.Clock.UtcNow = Noon.AddSeconds(70);
            await TickAsync(writer);

            await discord.DeliverAsync("GUILD_DELETE", $$"""{"id":"{{FakeDiscord.JoinedGuildId}}"}""");
            discord.Clock.UtcNow = Noon.AddSeconds(75);
            await TickAsync(writer);

            Assert.Equal(
            [
                (1u, FakeDiscord.GuildId, GuildEventKind.Joined),
                (2u, FakeDiscord.JoinedGuildId, GuildEventKind.Joined),
                (3u, FakeDiscord.GuildId, GuildEventKind.Left),
                (4u, FakeDiscord.JoinedGuildId, GuildEventKind.Left)
            ], data.Events);
        }

        // Unavailable while Discord says so, with its channels as they were, and neither left nor joined again
        [Fact]
        public async Task ServerBackFromAnOutageIsNeitherLeftNorJoinedAgain()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);
            await TickAsync(writer);

            await discord.MakeGuildUnavailableAsync();
            await TickAsync(writer);

            Assert.Equal(FirstGuild with { available = false }, data.Saved[^1].Guild);
            Assert.Equal(FirstGuildChannels, data.Saved[^1].Channels);

            await discord.MakeGuildAvailableAsync();
            await TickAsync(writer);

            Assert.Equal(3, data.Saved.Count);
            Assert.Equal(FirstGuild, data.Saved[^1].Guild);
            Assert.Empty(data.Leaves);
            Assert.Single(data.Events);
        }

        // A server in an outage that the writer hasn't written yet in this run, here because MinerBot's database was down, is
        // still written as unavailable: only a server just joined waits for Discord.Net to mark it connected
        [Fact]
        public async Task ServerInAnOutageBeforeItsFirstWriteIsWrittenAsUnavailable()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);

            data.Failure = Refused(1042);
            await TickAsync(writer);
            await discord.MakeGuildUnavailableAsync();

            data.Failure = null;
            discord.Clock.UtcNow = Noon.AddSeconds(30);
            await TickAsync(writer);

            Assert.Equal(FirstGuild with { available = false }, Assert.Single(data.Saved).Guild);
            Assert.Equal(FirstGuildChannels, data.Saved[0].Channels);
        }

        // Discord can send a server it has in an outage with nothing but its ID, which Discord.Net takes as a join and as an
        // empty server, and raises no event once the rest arrives. Its note stays until then, so the copy isn't emptied and
        // still catches up.
        [Fact]
        public async Task ServerSentWithNothingButItsIdIsWrittenOnceTheRestArrives()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);
            await TickAsync(writer);

            await discord.SendUnavailableGuildAsync(FakeDiscord.GuildId);
            await TickAsync(writer);
            await TickAsync(writer);

            await discord.MakeGuildAvailableAsync("Renamed during the outage");
            Assert.Single(data.Saved);

            await TickAsync(writer);

            Assert.Equal(FirstGuild with { name = "Renamed during the outage" }, data.Saved[^1].Guild);
            Assert.Equal(FirstGuildChannels, data.Saved[^1].Channels);
            Assert.Empty(data.Leaves);
            Assert.Single(data.Events);
        }

        // Discord.Net raises no LeftGuild for a server that went while the bot was offline, so the new session finds it. One
        // Discord still lists as unavailable at Ready is in an outage, not gone: only its row is marked unavailable.
        [Fact]
        public async Task NewSessionLeavesTheServersGoneWhileTheBotWasOfflineButNotOnesInAnOutage()
        {
            const ulong goneId = 2200, outageId = 2300;
            data.Rows[FakeDiscord.GuildId] = null;
            data.Rows[goneId] = null;
            data.Rows[outageId] = null;

            await using var discord = await StartAsync(unavailableGuilds: [outageId]);
            var writer = Writer(discord);

            await TickAsync(writer);

            Assert.Equal([(goneId, Noon.UtcDateTime, (string?)GuildWriter.GoneDetail)], data.Leaves);
            Assert.Equal([[outageId]], data.MarkedUnavailable);
            Assert.Equal([FakeDiscord.GuildId], data.Saved.Select(saved => saved.Guild.guild_id));
            Assert.Equal([(1u, goneId, GuildEventKind.Left)], data.Events);

            // Noted still, for when it's back
            Assert.Contains(outageId, discord.Watcher.PeekNotes().Select(note => note.GuildId));
        }

        // While the session starts, Discord can also send a server in an outage with nothing but its ID, before Ready or after
        // it, which Discord.Net takes as connected. It's in an outage all the same, so its row is marked unavailable.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ServerSentAsUnavailableAsTheSessionStartsIsMarkedUnavailable(bool beforeReady)
        {
            const ulong outageId = 2300;
            data.Rows[FakeDiscord.GuildId] = null;
            data.Rows[outageId] = null;

            await using var discord = await FakeDiscord.StartAsync(holdReady: beforeReady, unavailableGuilds: [outageId]);
            discord.Clock.UtcNow = Noon;

            if (beforeReady)
            {
                // Discord.Net raises Ready a second after the last server it was sent
                var ready = discord.ReleaseReadyAsync();
                await WaitUntilAsync(() => discord.Client.GetGuild(outageId) != null, "READY never came");
                await discord.SendUnavailableGuildAsync(outageId);
                await ready;
            }
            else
            {
                await discord.SendUnavailableGuildAsync(outageId);
            }

            Assert.True(discord.Client.GetGuild(outageId).IsConnected);
            await TickAsync(Writer(discord));

            Assert.Equal([[outageId]], data.MarkedUnavailable);
            Assert.Empty(data.Leaves);
            Assert.Contains(outageId, discord.Watcher.PeekNotes().Select(note => note.GuildId));
        }

        // A server the bot saw leave before the session's check ran, here while MinerBot's database was down, left when it
        // did, not as one found gone when the bot connected
        [Fact]
        public async Task LeaveSeenBeforeTheSessionsCheckKeepsItsTime()
        {
            data.Rows[FakeDiscord.GuildId] = null;
            await using var discord = await StartAsync();
            var writer = Writer(discord);

            data.Failure = Refused(1042);
            await TickAsync(writer);
            await discord.LeaveGuildAsync();

            data.Failure = null;
            discord.Clock.UtcNow = Noon.AddMinutes(1);
            await TickAsync(writer);

            Assert.Equal([(FakeDiscord.GuildId, Noon.UtcDateTime, (string?)null)], data.Leaves);
            Assert.Equal([(1u, FakeDiscord.GuildId, GuildEventKind.Left)], data.Events);
        }

        // While the gateway is down Discord.Net shows every server as unavailable, which isn't Discord's outage, so nothing is
        // written until it's back
        [Fact]
        public async Task NothingIsWrittenWhileTheGatewayIsDown()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);
            await TickAsync(writer);

            // Discord.Net's stop only starts the disconnect, which raises GuildUnavailable for every server before it ends
            await discord.Client.StopAsync();
            await WaitUntilAsync(() => discord.Client.ConnectionState == ConnectionState.Disconnected, "The gateway never disconnected");
            Assert.NotEmpty(discord.Watcher.PeekNotes());

            discord.Clock.UtcNow = Noon.AddSeconds(5);
            await TickAsync(writer);

            Assert.Single(data.Saved);
            Assert.NotEmpty(discord.Watcher.PeekNotes());
        }

        // Nor by a tick that was under way when the gateway dropped: what it hadn't written yet waits, noted
        [Fact]
        public async Task TickUnderWayWhenTheGatewayDropsWritesNoMore()
        {
            await using var discord = await StartAsync();
            await discord.JoinGuildAsync($"[{FakeDiscord.BotMemberJson(FakeDiscord.BotRoleId)}]");
            var writer = Writer(discord);
            await TickAsync(writer);

            await discord.UpdateGuildAsync("Renamed");
            await discord.DeliverAsync("CHANNEL_CREATE", FakeDiscord.ChannelEventJson(FakeDiscord.JoinedGuildId, 3150, 1, name: "new-channel"));

            var held = new TaskCompletionSource();
            data.Hold = held.Task;
            var calls = data.Calls;
            discord.Clock.UtcNow = Noon.AddSeconds(5);
            var tick = TickAsync(writer);
            await WaitUntilAsync(() => data.Calls > calls, "The first write never started");

            await discord.Client.StopAsync();
            await WaitUntilAsync(() => discord.Client.ConnectionState == ConnectionState.Disconnected, "The gateway never disconnected");
            held.SetResult();
            await tick.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.Equal(3, data.Saved.Count);
            Assert.Equal(("Renamed", true), (data.Saved[^1].Guild.name, data.Saved[^1].Guild.available));
            Assert.Contains(FakeDiscord.JoinedGuildId, discord.Watcher.PeekNotes().Select(note => note.GuildId));
        }

        // A tick with nothing new, and an event that changes nothing the copy holds, cost no call at all
        [Fact]
        public async Task UnchangedServersAreNotWrittenAgain()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);
            await TickAsync(writer);
            var calls = data.Calls;

            discord.Clock.UtcNow = Noon.AddSeconds(5);
            await TickAsync(writer);

            await discord.UpdateGuildAsync("Guild");
            discord.Clock.UtcNow = Noon.AddSeconds(10);
            await TickAsync(writer);

            Assert.Equal(calls, data.Calls);
            Assert.Single(data.Saved);
        }

        // In case an event never came: every server is looked at again every 15 minutes
        [Fact]
        public async Task EveryServerIsLookedAtAgainEvery15Minutes()
        {
            await using var discord = await StartAsync();

            // A watcher that hears no event
            var writer = Writer(discord, new GuildWatcher(discord.Client, discord.Clock));
            await TickAsync(writer);

            Assert.Single(data.Saved);

            await discord.DeliverAsync("CHANNEL_CREATE", FakeDiscord.ChannelEventJson(FakeDiscord.GuildId, 3050, 9, name: "new-channel"));
            discord.Clock.UtcNow = Noon.AddMinutes(15).AddSeconds(-1);
            await TickAsync(writer);

            Assert.Single(data.Saved);

            discord.Clock.UtcNow = Noon.AddMinutes(15);
            await TickAsync(writer);

            Assert.Equal(2, data.Saved.Count);
            Assert.Contains(data.Saved[^1].Channels, channel => channel.channel_id == 3050);
        }

        // 30 seconds, then twice as long each time, up to 5 minutes, and never sooner. The notes stay meanwhile, and the
        // whole outage gets one warning, with the error's number and nothing of its message.
        [Fact]
        public async Task FailedWriteKeepsTheNotesAndWaitsLongerEachTime()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);
            data.Failure = Refused(1045);

            await TickAsync(writer);

            var attempts = new List<int>();
            var last = data.Calls;

            for (var second = 1; second <= 1200; second++)
            {
                discord.Clock.UtcNow = Noon.AddSeconds(second);
                await TickAsync(writer);

                if (data.Calls != last)
                {
                    attempts.Add(second);
                    last = data.Calls;
                }
            }

            Assert.Equal([30, 90, 210, 450, 750, 1050], attempts);
            Assert.Empty(data.Saved);

            var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.Contains("(MySqlException, MySQL error 1045)", warning.Message);
            Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("minerbot-bot") || entry.Exception != null);

            data.Failure = null;
            discord.Clock.UtcNow = Noon.AddSeconds(1350);
            await TickAsync(writer);

            Assert.Equal(FirstGuild, Assert.Single(data.Saved).Guild);
            Assert.True(logger.Logged(LogLevel.Information, "MinerBot's database takes the bot's servers and channels again"));
        }

        // A server's write that fails keeps the server noted, so what changed reaches the table as soon as the database takes
        // it again, not at the next 15-minute pass
        [Fact]
        public async Task FailedWriteKeepsItsServerNotedUntilItGoesThrough()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);
            await TickAsync(writer);

            data.Failure = Refused(1042);
            await discord.DeliverAsync("CHANNEL_CREATE", FakeDiscord.ChannelEventJson(FakeDiscord.GuildId, 3050, 9, name: "new-channel"));
            discord.Clock.UtcNow = Noon.AddSeconds(5);
            await TickAsync(writer);

            Assert.Single(data.Saved);
            Assert.Equal([FakeDiscord.GuildId], discord.Watcher.PeekNotes().Select(note => note.GuildId));

            data.Failure = null;
            discord.Clock.UtcNow = Noon.AddSeconds(35);
            await TickAsync(writer);

            Assert.Equal(2, data.Saved.Count);
            Assert.Contains(data.Saved[^1].Channels, channel => channel.channel_id == 3050);
        }

        // A leave whose write fails keeps its time, and is recorded once the database takes it, under a number of its own:
        // the failed try's number isn't given again, as a write that failed may have landed
        [Fact]
        public async Task FailedLeaveIsRecordedOnceTheDatabaseTakesIt()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);
            await TickAsync(writer);

            data.Failure = Refused(1042);
            discord.Clock.UtcNow = Noon.AddSeconds(5);
            await discord.LeaveGuildAsync();
            await TickAsync(writer);

            Assert.Empty(data.Leaves);

            data.Failure = null;
            discord.Clock.UtcNow = Noon.AddSeconds(35);
            await TickAsync(writer);

            Assert.Equal([(FakeDiscord.GuildId, Noon.AddSeconds(5).UtcDateTime, (string?)null)], data.Leaves);
            Assert.Equal((3u, FakeDiscord.GuildId, GuildEventKind.Left), data.Events[^1]);
        }

        // A server noted again while its write is under way stays noted, so the change that came meanwhile is written next
        [Fact]
        public async Task ServerNotedAgainDuringItsWriteIsWrittenAgain()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);
            await TickAsync(writer);

            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            data.SaveHold = hold.Task;
            var calls = data.Calls;

            await discord.DeliverAsync("CHANNEL_CREATE", FakeDiscord.ChannelEventJson(FakeDiscord.GuildId, 3050, 9, name: "first"));
            discord.Clock.UtcNow = Noon.AddSeconds(5);
            var tick = TickAsync(writer);
            await WaitUntilAsync(() => data.Calls > calls, "The writer never started the write");

            await discord.DeliverAsync("CHANNEL_CREATE", FakeDiscord.ChannelEventJson(FakeDiscord.GuildId, 3051, 10, name: "second"));
            data.SaveHold = null;
            hold.SetResult();
            await tick.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.Equal([3050UL], data.Saved[^1].Channels.Select(channel => channel.channel_id).Where(id => id >= 3050));

            discord.Clock.UtcNow = Noon.AddSeconds(10);
            await TickAsync(writer);

            Assert.Equal(3, data.Saved.Count);
            Assert.Equal([3050UL, 3051UL], data.Saved[^1].Channels.Select(channel => channel.channel_id).Where(id => id >= 3050));
        }

        // A server already written that a new session finds in an outage has its row marked unavailable, so it's written
        // again once it's back, though it's back as it was
        [Fact]
        public async Task ServerInAnOutageAtANewSessionIsWrittenAgainOnceItsBack()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);
            await TickAsync(writer);

            await discord.StartNewSessionAsync(FakeDiscord.GuildId);
            discord.Clock.UtcNow = Noon.AddSeconds(5);
            await TickAsync(writer);

            Assert.Equal([[FakeDiscord.GuildId]], data.MarkedUnavailable);
            Assert.Single(data.Saved);

            await discord.MakeGuildAvailableAsync();
            discord.Clock.UtcNow = Noon.AddSeconds(10);
            await TickAsync(writer);

            Assert.Equal(2, data.Saved.Count);
            Assert.Equal(FirstGuild, data.Saved[^1].Guild);
            Assert.Empty(data.Leaves);
        }

        // A server the database refuses before any SQL is the bot's own mistake: it goes, rather than failing every tick
        [Fact]
        public async Task ServerTheDatabaseRefusesIsDropped()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);
            data.RefuseGuilds = true;

            await TickAsync(writer);

            Assert.Empty(data.Saved);
            Assert.Empty(discord.Watcher.PeekNotes());
            Assert.True(logger.Logged(LogLevel.Error, "MinerBot's database refused a server the bot read, so it wasn't written (ArgumentException)"));
            Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);

            data.RefuseGuilds = false;
            await discord.UpdateGuildAsync("Renamed");
            await TickAsync(writer);

            Assert.Equal("Renamed", Assert.Single(data.Saved).Guild.name);
        }

        // A write that hangs, on a database that stopped answering, is given up when the bot stops, and the next start's
        // session notes every server again. The session's read goes through, so it's the server's write that hangs.
        [Fact]
        public async Task StoppingTheBotGivesUpAWriteThatHangs()
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord);
            data.SaveHold = new TaskCompletionSource().Task;

            await writer.StartAsync(TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => data.Calls >= 2, "The writer never started the server's write");

            var timer = Stopwatch.StartNew();
            await writer.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"The stop took {timer.Elapsed}");
            Assert.True(writer.ExecuteTask!.IsCompletedSuccessfully);
            Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
        }

        // The bot posts as before, and the line never quotes the setting
        [Theory]
        [InlineData(null, "isn't set")]
        [InlineData("Server=127.0.0.1;Uid=minerbot-bot;Pwd=password", "names no database")]
        [InlineData("Server=127.0.0.1;Database=bot_db;Uid=minerbot-bot;Pwd=secret;password", "can't be read")]
        public async Task WithoutBotConnectionNothingIsRecorded(string? connection, string problem)
        {
            await using var discord = await StartAsync();
            var writer = Writer(discord, connection: connection);

            await writer.StartAsync(TestContext.Current.CancellationToken);
            await writer.ExecuteTask!.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            await writer.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.Equal(0, data.Calls);
            Assert.Equal($"ConnectionStrings:BotConnection {problem}, so the servers and channels the bot is in aren't recorded either", Assert.Single(logger.Entries).Message);
            Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("password") || entry.Message.Contains("minerbot-bot"));
        }

        // An exception that left the loop would stop the whole bot. A clock that fails stands in for anything that could.
        [Fact]
        public async Task FailureInTheLoopNeverEndsIt()
        {
            await using var discord = await StartAsync();
            var looping = Writer(discord, clock: new FlakyClock { Failures = 3 });

            await looping.StartAsync(TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => data.Saved.Count > 0, "The writer never wrote after the failures");

            Assert.False(looping.ExecuteTask!.IsCompleted);
            await looping.StopAsync(CancellationToken.None).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            // Once, however often it repeats within a minute
            Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("The server writer failed (InvalidOperationException)"));
        }

        // Discord.Net skips an event's later handlers after one that throws, so the watcher's never do
        [Fact]
        public async Task WatcherNeverThrowsIntoDiscordNet()
        {
            await using var discord = await StartAsync();
            var watcher = discord.Watcher;

            // What Discord.Net never passes, standing in for anything that could go wrong
            Assert.True(watcher.OnGuild(null!).IsCompletedSuccessfully);
            Assert.True(watcher.OnJoinedGuild(null!).IsCompletedSuccessfully);
            Assert.True(watcher.OnGuildAvailable(null!).IsCompletedSuccessfully);
            Assert.True(watcher.OnLeftGuild(null!).IsCompletedSuccessfully);
            Assert.True(watcher.OnRole(null!).IsCompletedSuccessfully);
            Assert.True(watcher.OnMemberUpdated(default, null!).IsCompletedSuccessfully);
        }

        sealed class FlakyClock : TimeProvider
        {
            int failures;

            public int Failures { set => failures = value; }

            public override DateTimeOffset GetUtcNow() =>
                Interlocked.Decrement(ref failures) >= 0 ? throw new InvalidOperationException("The clock failed") : Noon;
        }
    }
}
