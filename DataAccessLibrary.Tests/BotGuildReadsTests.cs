using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Guilds.Models;
using DataAccessLibrary.Tests.Database;

namespace DataAccessLibrary.Tests
{
    // The dashboard's reads of the bot's copy of its servers, from rows the bot's own BotGuildData writes, and of the
    // website's settings, from rows GuildData writes, as the bot's /admin set_channels does
    [Collection("Database")]
    public class BotGuildReadsTests(MariaDbFixture fixture)
    {
        static readonly DateTime Noon = new(2026, 11, 17, 12, 0, 0, 123, DateTimeKind.Utc);
        const ulong RunId = 18446744073709551615;
        const ulong GuildId = 1000000000000000001, OtherGuildId = 1000000000000000002, LeftGuildId = 1000000000000000003;

        async Task<MariaDbFixture> StartAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.BotGuildTable, MariaDbFixture.BotChannelTable, MariaDbFixture.GuildEventTable, MariaDbFixture.GuildDailyTable,
                MariaDbFixture.GuildTable);
            return db;
        }

        static BotGuild Server(ulong id, string name, bool administrator = false) => new()
        {
            guild_id = id,
            name = name,
            icon_hash = "a_0123456789abcdef0123456789abcdef",
            member_count = 12,
            bot_joined_at = Noon.AddDays(-3),
            available = true,
            bot_administrator = administrator,
            bot_permissions = 1024 | 2048 | 16384
        };

        static BotGuildChannel Channel(ulong id, string name, int position, ulong? categoryId, int? categoryPosition, string? category = "Text Channels",
            bool view = true) => new()
        {
            channel_id = id,
            name = name,
            type = GuildChannelType.Text,
            category = categoryId == null ? null : category,
            category_id = categoryId,
            category_position = categoryPosition,
            position = position,
            can_view = view,
            can_send = view,
            can_embed = view
        };

        [Fact]
        public async Task EmptyTablesReadAsNothing()
        {
            var db = await StartAsync();
            var reads = db.GuildReads();

            Assert.Empty(await reads.ListGuilds());
            Assert.Null(await reads.GetGuild(GuildId));
            Assert.Empty(await reads.ListChannels(GuildId));
            Assert.Empty(await reads.FindChannels([1, 2]));
            Assert.Empty(await reads.FindChannels([]));
            Assert.Empty(await reads.ListGuildEvents(GuildId, 50));
            Assert.Empty(await reads.ListMemberDays(GuildId, Noon.Date.AddDays(-90), Noon.Date));
            Assert.Empty(await reads.MemberTrends(Noon.Date.AddDays(-30)));
            Assert.Empty(await db.GuildSettings().ListSettings());
            Assert.Null(await db.GuildSettings().GetSettings(GuildId));
        }

        // Every server in or left, by name, as the bot wrote it, every time UTC
        [Fact]
        public async Task ServersAreReadAsTheBotWroteThem()
        {
            var db = await StartAsync();
            var guilds = db.BotGuilds();

            await guilds.SaveGuild(Server(GuildId, "Zebra Server", administrator: true), [], Noon, RunId, 1);
            await guilds.SaveGuild(Server(OtherGuildId, "Alpha Server"), [], Noon, RunId, 2);
            await guilds.SaveGuild(Server(LeftGuildId, "Middle Server"), [], Noon, RunId, 3);
            await guilds.MarkUnavailable([OtherGuildId], Noon.AddMinutes(1));
            await guilds.LeaveGuild(LeftGuildId, Noon.AddMinutes(2), RunId, 4, null);
            var reads = db.GuildReads();

            var all = await reads.ListGuilds();
            Assert.Equal(["Alpha Server", "Middle Server", "Zebra Server"], all.Select(guild => guild.name));
            Assert.False(all[0].available);
            Assert.Equal(Noon.AddMinutes(2), all[1].left_at);
            Assert.True(all[2].bot_administrator);
            Assert.Equal(1024UL | 2048 | 16384, all[2].bot_permissions);
            Assert.Equal("a_0123456789abcdef0123456789abcdef", all[2].icon_hash);
            Assert.Equal(12U, all[2].member_count);
            Assert.All(all, guild => Assert.Equal(DateTimeKind.Utc, guild.first_seen_at.Kind));
            Assert.All(all, guild => Assert.Equal(DateTimeKind.Utc, guild.updated_at.Kind));
            Assert.Equal(DateTimeKind.Utc, all[1].left_at!.Value.Kind);

            var one = await reads.GetGuild(GuildId);
            Assert.NotNull(one);
            Assert.Equal(Noon.AddDays(-3), one.bot_joined_at);
            Assert.Equal(DateTimeKind.Utc, one.bot_joined_at!.Value.Kind);
            Assert.Null(one.left_at);
            Assert.Null(await reads.GetGuild(GuildId + 10));
        }

        // Discord's order: the channels outside a category first, then each category by its position, two at one position
        // kept apart by their IDs, and each one's channels by their positions, the lower ID first on a tie
        [Fact]
        public async Task ChannelsComeInDiscordsOrder()
        {
            var db = await StartAsync();

            await db.BotGuilds().SaveGuild(Server(GuildId, "Test Server"),
            [
                Channel(40, "last", 0, categoryId: 3800, categoryPosition: 1, category: "Voice Notes"),
                Channel(31, "second-category", 0, categoryId: 3901, categoryPosition: 0),
                Channel(20, "position-one", 1, categoryId: 3900, categoryPosition: 0),
                Channel(22, "tie-later", 0, categoryId: 3900, categoryPosition: 0),
                Channel(21, "tie-first", 0, categoryId: 3900, categoryPosition: 0),
                Channel(11, "outside-five", 5, categoryId: null, categoryPosition: null),
                Channel(12, "outside-zero", 0, categoryId: null, categoryPosition: null),
                Channel(13, "___hidden___", 1, categoryId: null, categoryPosition: null, view: false)
            ], Noon, RunId, 1);
            await db.BotGuilds().SaveGuild(Server(OtherGuildId, "Other Server"), [Channel(50, "elsewhere", 0, 3900, 0)], Noon, RunId, 2);

            var channels = await db.GuildReads().ListChannels(GuildId);

            Assert.Equal(["outside-zero", "___hidden___", "outside-five", "tie-first", "tie-later", "position-one", "second-category", "last"], channels.Select(channel => channel.name));
            Assert.Equal([null, null, null, 3900UL, 3900UL, 3900UL, 3901UL, 3800UL], channels.Select(channel => channel.category_id));
            Assert.False(channels[1].can_view);
            Assert.Equal("Voice Notes", channels[^1].category);
            Assert.All(channels, channel => Assert.Equal(GuildId, channel.guild_id));
            Assert.All(channels, channel => Assert.Equal(DateTimeKind.Utc, channel.updated_at.Kind));

            // By ID, whichever server each is in, and none for an ID the copy doesn't have
            var found = await db.GuildReads().FindChannels([21, 50, 999]);
            Assert.Equal([(21UL, GuildId), (50UL, OtherGuildId)], found.OrderBy(channel => channel.channel_id).Select(channel => (channel.channel_id, channel.guild_id)));
        }

        // A server's joins and leaves, newest first, with the bot's details, and its members by day
        [Fact]
        public async Task JoinsLeavesAndMemberCounts()
        {
            var db = await StartAsync();
            var guilds = db.BotGuilds();
            var today = Noon.Date;

            await guilds.SaveGuild(Server(GuildId, "Test Server"), [], Noon.AddDays(-2), RunId, 1);
            await guilds.LeaveGuild(GuildId, Noon.AddDays(-1), RunId, 2, "found gone when the bot connected");
            await guilds.SaveGuild(Server(GuildId, "Test Server") with { bot_joined_at = Noon }, [], Noon, RunId, 3);
            await guilds.SaveGuild(Server(OtherGuildId, "Other Server"), [], Noon, RunId, 4);

            await guilds.SaveMemberCount(GuildId, 100, today.AddDays(-40), Noon);
            await guilds.SaveMemberCount(GuildId, 110, today.AddDays(-20), Noon);
            await guilds.SaveMemberCount(GuildId, 118, today.AddDays(-1), Noon);
            await guilds.SaveMemberCount(GuildId, 120, today, Noon);
            await guilds.SaveMemberCount(OtherGuildId, 5, today, Noon);
            var reads = db.GuildReads();

            var events = await reads.ListGuildEvents(GuildId, 50);
            Assert.Equal([(GuildEventKind.Joined, "again, after 1 day away"), (GuildEventKind.Left, "found gone when the bot connected"), (GuildEventKind.Joined, "first time")],
                events.Select(row => (row.kind, row.detail)));
            Assert.All(events, row => Assert.Equal(DateTimeKind.Utc, row.at.Kind));
            Assert.Single(await reads.ListGuildEvents(GuildId, 1));

            var days = await reads.ListMemberDays(GuildId, today.AddDays(-30), today.AddDays(1));
            Assert.Equal([(today.AddDays(-20), 110U), (today.AddDays(-1), 118U), (today, 120U)], days.Select(row => (row.day, row.member_count!.Value)));
            Assert.All(days, row => Assert.Equal(DateTimeKind.Utc, row.day.Kind));

            var trends = await reads.MemberTrends(today.AddDays(-30));
            Assert.Equal([(GuildId, today.AddDays(-20), 110U), (GuildId, today, 120U), (OtherGuildId, today, 5U)],
                trends.Select(row => (row.guild_id, row.day, row.member_count!.Value)));
        }

        // The website's settings as the bot's /admin set_channels saves them, by server ID, and an ID one apart is another
        // server's. A Discord ID past a long's range reads back whole.
        [Fact]
        public async Task SettingsAreReadByServer()
        {
            var db = await StartAsync();
            var settings = db.Guilds();

            await settings.SaveGuild(new Guild { guild_id = GuildId, armor_channel_id = 1, map_channel_id = 2, lobby_channel_id = 0, announcements_channel_id = 4 });
            await settings.SaveGuild(new Guild { guild_id = 18446744073709551615, armor_channel_id = 18446744073709551614 });
            await settings.SaveGuild(new Guild { guild_id = 5 });
            var reads = db.GuildSettings();

            var all = await reads.ListSettings();
            Assert.Equal([5UL, GuildId, 18446744073709551615], all.Select(row => row.guild_id));
            Assert.Equal((1UL, 2UL, 0UL, 4UL), (all[1].armor_channel_id, all[1].map_channel_id, all[1].lobby_channel_id, all[1].announcements_channel_id));
            Assert.Equal(18446744073709551614, all[2].armor_channel_id);

            Assert.Equal(4UL, (await reads.GetSettings(GuildId))?.announcements_channel_id);
            Assert.Null(await reads.GetSettings(GuildId + 1));
            Assert.Null(await reads.GetSettings(GuildId - 1));
        }
    }
}
