using System.Globalization;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Guilds.Models;
using DataAccessLibrary.Tests.Database;
using MySql.Data.MySqlClient;

namespace DataAccessLibrary.Tests
{
    // MinerBot's own copy of the servers the bot is in. The bot writes again whatever it isn't sure went through, so every
    // write here must be safe to repeat, and a server's row and its channels land together or not at all.
    [Collection("Database")]
    public class BotGuildDataTests(MariaDbFixture fixture)
    {
        static readonly DateTime Noon = new(2026, 11, 17, 12, 0, 0, 123, DateTimeKind.Utc);
        static readonly DateTime Joined = new(2026, 11, 14, 8, 30, 0, 456, DateTimeKind.Utc);
        const ulong RunId = 18446744073709551615;
        const ulong GuildId = 1000000000000000001, OtherGuildId = 1000000000000000002;

        // View Channel, Send Messages and Embed Links
        const ulong Permissions = 1024 | 2048 | 16384;

        async Task<MariaDbFixture> StartAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.BotGuildTable, MariaDbFixture.BotChannelTable, MariaDbFixture.GuildEventTable, MariaDbFixture.GuildDailyTable);
            return db;
        }

        async Task<long> CountAsync(string table) => (await fixture.RootQueryAsync<long>($"SELECT COUNT(*) FROM {table}")).Single();

        static BotGuild Server(ulong guildId = GuildId, string name = "Test Server", uint? members = 12, DateTime? joined = null) => new()
        {
            guild_id = guildId,
            name = name,
            icon_hash = "a_0123456789abcdef0123456789abcdef",
            member_count = members,
            bot_joined_at = joined ?? Joined,
            available = true,
            bot_permissions = Permissions
        };

        // In the category Text Channels, the server's first, unless a test says otherwise
        static BotGuildChannel Channel(ulong id, string name, int position, string type = GuildChannelType.Text, string? category = "Text Channels",
            bool view = true, bool send = true, bool embed = true, ulong? categoryId = 3900, int? categoryPosition = 0) => new()
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

        static readonly BotGuildChannel General = Channel(3000, "general", 0);
        static readonly BotGuildChannel News = Channel(3001, "news", 1, GuildChannelType.Announcement, category: null, send: false, embed: false, categoryId: null,
            categoryPosition: null);

        // As the tables have them, by name
        public sealed class GuildRow
        {
            public ulong guild_id { get; set; }
            public string name { get; set; } = "";
            public string? icon_hash { get; set; }
            public uint? member_count { get; set; }
            public DateTime? bot_joined_at { get; set; }
            public DateTime first_seen_at { get; set; }
            public DateTime? left_at { get; set; }
            public bool available { get; set; }
            public bool bot_administrator { get; set; }
            public ulong bot_permissions { get; set; }
            public DateTime updated_at { get; set; }
        }

        public sealed record ChannelRow
        {
            public ulong channel_id { get; set; }
            public ulong guild_id { get; set; }
            public string name { get; set; } = "";
            public string type { get; set; } = "";
            public string? category { get; set; }
            public ulong? category_id { get; set; }
            public int? category_position { get; set; }
            public int position { get; set; }
            public bool can_view { get; set; }
            public bool can_send { get; set; }
            public bool can_embed { get; set; }
            public DateTime updated_at { get; set; }
        }

        async Task<GuildRow?> RowAsync(ulong guildId) =>
            (await fixture.RootQueryAsync<GuildRow>($"SELECT * FROM {MariaDbFixture.BotGuildTable} WHERE guild_id = @guildId", new { guildId })).SingleOrDefault();

        Task<List<ChannelRow>> ChannelsAsync() =>
            fixture.RootQueryAsync<ChannelRow>($"SELECT channel_id, guild_id, name, type, category, category_id, category_position, `position`, can_view, can_send, can_embed, updated_at FROM {MariaDbFixture.BotChannelTable} ORDER BY channel_id");

        Task<List<(ulong, uint, ulong, DateTime, string, string?)>> EventsAsync() =>
            fixture.RootQueryAsync<(ulong, uint, ulong, DateTime, string, string?)>($"SELECT run_id, seq, guild_id, at, kind, detail FROM {MariaDbFixture.GuildEventTable} ORDER BY id");

        static ChannelRow Stored(BotGuildChannel channel, ulong guildId, DateTime at) => new()
        {
            channel_id = channel.channel_id,
            guild_id = guildId,
            name = channel.name,
            type = channel.type,
            category = channel.category,
            category_id = channel.category_id,
            category_position = channel.category_position,
            position = channel.position,
            can_view = channel.can_view,
            can_send = channel.can_send,
            can_embed = channel.can_embed,
            updated_at = at
        };

        // Every column, with the times as written to the millisecond, and the join at the time Discord says the bot joined
        [Fact]
        public async Task NewServerIsWrittenWithItsChannelsAndRecordedAsJoined()
        {
            var db = await StartAsync();

            Assert.True(await db.BotGuilds().SaveGuild(Server(), [General, News], Noon, RunId, 1));

            var row = await RowAsync(GuildId);
            Assert.NotNull(row);
            Assert.Equal(("Test Server", "a_0123456789abcdef0123456789abcdef", (uint?)12, (DateTime?)Joined, Noon, (DateTime?)null),
                (row.name, row.icon_hash, row.member_count, row.bot_joined_at, row.first_seen_at, row.left_at));
            Assert.Equal((true, false, Permissions, Noon), (row.available, row.bot_administrator, row.bot_permissions, row.updated_at));

            Assert.Equal([Stored(General, GuildId, Noon), Stored(News, GuildId, Noon)], await ChannelsAsync());
            Assert.Equal([(RunId, 1u, GuildId, Joined, GuildEventKind.Joined, (string?)"first time")], await EventsAsync());
        }

        // The row and the channels follow the server, the channels it no longer has go, and the row keeps when it was first
        // seen and the member count it had: Discord.Net's count is only right for a server new to the table. Every other
        // column takes what the server has now: a new icon, new permissions, and a new join time, as when the bot was removed
        // and added again while it was offline; a channel that became an announcement channel, and ones that lost or gained
        // Send Messages and Embed Links; a channel taken out of its category, one put in another category of the same name,
        // and one whose category moved. An icon taken away goes too.
        [Fact]
        public async Task LaterWriteChangesTheServerAndItsChannels()
        {
            var db = await StartAsync();
            var data = db.BotGuilds();
            var rules = Channel(3002, "rules", 2);
            var quiet = Channel(3004, "quiet", 4, category: "Quiet", send: false, embed: false, categoryId: 3903, categoryPosition: 4);
            var old = Channel(3005, "old", 5);

            await data.SaveGuild(Server(), [General, News, rules, quiet, old], Noon, RunId, 1);

            var renamed = General with { name = "chat", position = 5, category = null, category_id = null, category_position = null };
            var muted = News with { can_view = false, category = "Text Channels", category_id = 3901, category_position = 2 };
            var converted = rules with { type = GuildChannelType.Announcement, can_send = false, can_embed = false };
            var added = Channel(3003, "new-channel", 3, category: "Other", categoryId: 3902, categoryPosition: 1);
            var unmuted = quiet with { can_send = true, can_embed = true, category_position = 3 };
            var later = Noon.AddHours(1);
            var server = Server(name: "Renamed Server", members: 99, joined: Joined.AddDays(1)) with
            {
                icon_hash = "fedcba9876543210fedcba9876543210",
                available = false,
                bot_administrator = true,
                bot_permissions = Permissions | 8
            };

            Assert.False(await data.SaveGuild(server, [renamed, muted, converted, added, unmuted], later, RunId, 2));

            var row = await RowAsync(GuildId);
            Assert.NotNull(row);
            Assert.Equal(("Renamed Server", (string?)"fedcba9876543210fedcba9876543210", (uint?)12, (DateTime?)Joined.AddDays(1), Noon, (DateTime?)null),
                (row.name, row.icon_hash, row.member_count, row.bot_joined_at, row.first_seen_at, row.left_at));
            Assert.Equal((false, true, Permissions | 8, later), (row.available, row.bot_administrator, row.bot_permissions, row.updated_at));
            Assert.Equal([Stored(renamed, GuildId, later), Stored(muted, GuildId, later), Stored(converted, GuildId, later), Stored(added, GuildId, later),
                Stored(unmuted, GuildId, later)], await ChannelsAsync());
            Assert.Single(await EventsAsync());

            // A server that took its icon away
            await data.SaveGuild(server with { icon_hash = null }, [renamed, muted, converted, added, unmuted], later.AddMinutes(1), RunId, 3);

            Assert.Null((await RowAsync(GuildId))!.icon_hash);
        }

        // A write that's made again, such as after one that timed out but went through, lands once
        [Fact]
        public async Task SameServerWrittenAgainLandsOnce()
        {
            var db = await StartAsync();
            var data = db.BotGuilds();

            Assert.True(await data.SaveGuild(Server(), [General, News], Noon, RunId, 1));
            Assert.False(await data.SaveGuild(Server(), [General, News], Noon, RunId, 2));

            Assert.Equal(1, await CountAsync(MariaDbFixture.BotGuildTable));
            Assert.Equal([Stored(General, GuildId, Noon), Stored(News, GuildId, Noon)], await ChannelsAsync());
            Assert.Single(await EventsAsync());
        }

        // The server's row was lost, so the table says it's new again, but the event of the same run and number lands once
        [Fact]
        public async Task EventWrittenAgainLandsOnce()
        {
            var db = await StartAsync();
            var data = db.BotGuilds();

            await data.SaveGuild(Server(), [General], Noon, RunId, 1);
            await db.RootExecuteAsync($"DELETE FROM {MariaDbFixture.BotGuildTable}");

            Assert.True(await data.SaveGuild(Server(), [General], Noon.AddSeconds(5), RunId, 1));

            Assert.Single(await EventsAsync());
            Assert.Equal(1, await CountAsync(MariaDbFixture.BotGuildTable));
        }

        // A server's write deletes only its own channels that went, all of them when it has none left, and never another
        // server's: NOT IN an empty list would match every channel of every server
        [Fact]
        public async Task ServerLosesOnlyItsOwnChannels()
        {
            var db = await StartAsync();
            var data = db.BotGuilds();
            var other = Channel(3100, "other", 0);

            await data.SaveGuild(Server(), [General, News], Noon, RunId, 1);
            await data.SaveGuild(Server(OtherGuildId), [other], Noon, RunId, 2);

            Assert.Equal([Stored(General, GuildId, Noon), Stored(News, GuildId, Noon), Stored(other, OtherGuildId, Noon)], await ChannelsAsync());

            await data.SaveGuild(Server(), [], Noon.AddMinutes(1), RunId, 3);

            Assert.Equal([Stored(other, OtherGuildId, Noon)], await ChannelsAsync());
        }

        // A statement takes at most 100 of them, and a server can have up to Discord's 500 channels
        [Theory]
        [InlineData(250)]
        [InlineData(BotGuildData.ChannelLimit)]
        public async Task ManyChannelsGoInBatches(int count)
        {
            var db = await StartAsync();
            var data = db.BotGuilds();
            var channels = Enumerable.Range(0, count).Select(i => Channel(10000 + (ulong)i, $"channel-{i}", i)).ToList();

            // The server's row and its join first, so the write of its channels inserts nothing else but the row again
            await data.SaveGuild(Server(), [], Noon, RunId, 1);
            var inserts = await InsertsAsync();

            await data.SaveGuild(Server(), channels, Noon.AddMinutes(1), RunId, 2);

            Assert.Equal(count, await CountAsync(MariaDbFixture.BotChannelTable));
            Assert.Equal(1 + (count + BotGuildData.BatchLimit - 1) / BotGuildData.BatchLimit, await InsertsAsync() - inserts);

            // And the next write takes most of them away again
            await data.SaveGuild(Server(), channels[..3], Noon.AddMinutes(2), RunId, 3);

            Assert.Equal(channels[..3].Select(channel => channel.channel_id), (await ChannelsAsync()).Select(row => row.channel_id));
        }

        // The INSERT statements MariaDB has run, from every session: the Database collection runs one test at a time
        async Task<long> InsertsAsync() => long.Parse(
            (await fixture.RootQueryAsync<string>("SELECT VARIABLE_VALUE FROM information_schema.GLOBAL_STATUS WHERE VARIABLE_NAME = 'COM_INSERT'")).Single(),
            CultureInfo.InvariantCulture);

        // The row stays, marked left, its channels go, and the leave is recorded once, only for a server the row says the bot
        // is in
        [Fact]
        public async Task LeaveMarksTheServerLeftAndDeletesItsChannels()
        {
            var db = await StartAsync();
            var data = db.BotGuilds();
            var other = Channel(3100, "other", 0);
            var left = Noon.AddHours(1);

            await data.SaveGuild(Server(), [General, News], Noon, RunId, 1);
            await data.SaveGuild(Server(OtherGuildId), [other], Noon, RunId, 2);

            Assert.True(await data.LeaveGuild(GuildId, left, RunId, 3, null));
            Assert.False(await data.LeaveGuild(GuildId, left.AddMinutes(1), RunId, 4, null));
            Assert.False(await data.LeaveGuild(999, left, RunId, 5, "found gone when the bot connected"));

            var row = await RowAsync(GuildId);
            Assert.NotNull(row);
            Assert.Equal(((DateTime?)left, left, Noon), (row.left_at, row.updated_at, row.first_seen_at));
            Assert.Null((await RowAsync(OtherGuildId))!.left_at);
            Assert.Null(await RowAsync(999));
            Assert.Equal([Stored(other, OtherGuildId, Noon)], await ChannelsAsync());
            Assert.Equal((RunId, 3u, GuildId, left, GuildEventKind.Left, (string?)null), (await EventsAsync())[^1]);
            Assert.Equal(3, (await EventsAsync()).Count);
            Assert.Equal([OtherGuildId], await data.ListGuildsIn());
        }

        // A leave keeps its detail, the only word on a server the bot found gone when it connected, cut to the column's 300
        // characters, because strict mode would refuse a longer one, and the leave with it
        [Fact]
        public async Task LeaveKeepsItsDetail()
        {
            var db = await StartAsync();
            var data = db.BotGuilds();
            var left = Noon.AddHours(1);
            var detail = string.Concat(Enumerable.Range(0, 40).Select(i => $"{i:00000000}\U0001F600"));
            var stored = string.Concat(Enumerable.Range(0, 33).Select(i => $"{i:00000000}\U0001F600")) + "000";

            Assert.Equal(300, stored.EnumerateRunes().Count());

            await data.SaveGuild(Server(), [General], Noon, RunId, 1);
            await data.SaveGuild(Server(OtherGuildId), [], Noon, RunId, 2);

            Assert.True(await data.LeaveGuild(GuildId, left, RunId, 3, "found gone when the bot connected"));
            Assert.True(await data.LeaveGuild(OtherGuildId, left, RunId, 4, detail));

            Assert.Equal(
            [
                (RunId, 3u, GuildId, left, GuildEventKind.Left, (string?)"found gone when the bot connected"),
                (RunId, 4u, OtherGuildId, left, GuildEventKind.Left, (string?)stored)
            ], (await EventsAsync())[2..]);
        }

        // Back after the bot left: recorded as joined again, with how long it was away, and the row takes the new member
        // count, as Discord just sent the server
        [Theory]
        [InlineData(5, "again, after less than a day away")]
        [InlineData(25, "again, after 1 day away")]
        [InlineData(49, "again, after 2 days away")]
        public async Task ServerBackAfterALeaveIsRecordedAsJoinedAgain(int hoursAway, string detail)
        {
            var db = await StartAsync();
            var data = db.BotGuilds();
            var back = Noon.AddHours(hoursAway);

            await data.SaveGuild(Server(), [General], Noon.AddDays(-1), RunId, 1);
            await data.LeaveGuild(GuildId, Noon, RunId, 2, null);

            Assert.True(await data.SaveGuild(Server(members: 40, joined: back), [General], back.AddMinutes(1), RunId, 3));

            var row = await RowAsync(GuildId);
            Assert.NotNull(row);
            Assert.Equal(((DateTime?)null, (uint?)40, Noon.AddDays(-1)), (row.left_at, row.member_count, row.first_seen_at));
            Assert.Equal((RunId, 3u, GuildId, back, GuildEventKind.Joined, (string?)detail), (await EventsAsync())[^1]);
            Assert.Equal([Stored(General, GuildId, back.AddMinutes(1))], await ChannelsAsync());
        }

        // Discord's join time is used unless it would put the join out of order: in the future, or from before the leave
        [Theory]
        [InlineData("none")]
        [InlineData("future")]
        [InlineData("before the leave")]
        public async Task JoinTimeThatDoesntFitIsTheWritesTime(string joined)
        {
            var db = await StartAsync();
            var data = db.BotGuilds();
            var at = Noon.AddDays(1);

            await data.SaveGuild(Server(), [], Noon.AddDays(-1), RunId, 1);
            await data.LeaveGuild(GuildId, Noon, RunId, 2, null);

            var server = Server() with
            {
                bot_joined_at = joined switch
                {
                    "none" => null,
                    "future" => at.AddMinutes(1),
                    _ => Noon.AddMinutes(-1)
                }
            };

            await data.SaveGuild(server, [], at, RunId, 3);

            var joinedAgain = (await EventsAsync())[^1];
            Assert.Equal((at, GuildEventKind.Joined, (string?)"again, after 1 day away"), (joinedAgain.Item4, joinedAgain.Item5, joinedAgain.Item6));
        }

        // A statement that fails takes the whole write with it: here the event's, stuck behind a lock until the server's
        // limit for one statement ends it
        [Fact]
        public async Task ServerAndItsChannelsLandTogetherOrNotAtAll()
        {
            var db = await StartAsync();
            var data = db.BotGuilds();
            data.CallLimit = TimeSpan.FromSeconds(2);

            await data.SaveGuild(Server(OtherGuildId), [Channel(3100, "other", 0)], Noon, RunId, 1);

            await using var holder = new MySqlConnection(db.RootConnection);
            await holder.OpenAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await RawAsync(holder, $"LOCK TABLES {MariaDbFixture.GuildEventTable} WRITE");

            try
            {
                await Assert.ThrowsAnyAsync<Exception>(() => data.SaveGuild(Server(), [General, News], Noon, RunId, 2));
                await Assert.ThrowsAnyAsync<Exception>(() => data.LeaveGuild(OtherGuildId, Noon.AddHours(1), RunId, 3, null));
            }
            finally
            {
                await RawAsync(holder, "UNLOCK TABLES");
            }

            Assert.Null(await RowAsync(GuildId));
            Assert.Null((await RowAsync(OtherGuildId))!.left_at);
            Assert.Equal([3100UL], (await ChannelsAsync()).Select(row => row.channel_id));
            Assert.Single(await EventsAsync());
        }

        // Only the rows of servers the bot is in that are still marked available, and nothing for an empty list
        [Fact]
        public async Task OnlyServersTheBotIsInAreMarkedUnavailable()
        {
            var db = await StartAsync();
            var data = db.BotGuilds();
            const ulong leftId = 1000000000000000003, downId = 1000000000000000004;
            var at = Noon.AddMinutes(5);

            await data.SaveGuild(Server(), [General], Noon, RunId, 1);
            await data.SaveGuild(Server(leftId), [], Noon, RunId, 2);
            await data.LeaveGuild(leftId, Noon, RunId, 3, null);
            await data.SaveGuild(Server(downId) with { available = false }, [], Noon, RunId, 4);

            Assert.Equal(0, await data.MarkUnavailable([], at));
            Assert.Equal(1, await data.MarkUnavailable([GuildId, leftId, downId, 999], at));

            var marked = await RowAsync(GuildId);
            var left = await RowAsync(leftId);
            var down = await RowAsync(downId);
            Assert.Equal((false, at), (marked!.available, marked.updated_at));
            Assert.Equal((true, Noon), (left!.available, left.updated_at));
            Assert.Equal((false, Noon), (down!.available, down.updated_at));

            // Its channels stay as they were
            Assert.Equal([Stored(General, GuildId, Noon)], await ChannelsAsync());
        }

        [Fact]
        public async Task OnlyServersTheBotIsInAreListed()
        {
            var db = await StartAsync();
            var data = db.BotGuilds();

            Assert.Empty(await data.ListGuildsIn());

            await data.SaveGuild(Server(), [], Noon, RunId, 1);
            await data.SaveGuild(Server(OtherGuildId), [], Noon, RunId, 2);
            await data.LeaveGuild(OtherGuildId, Noon, RunId, 3, null);

            Assert.Equal([GuildId], await data.ListGuildsIn());
        }

        // The server's row and the day's row of its trend, one row a UTC day, which a later count the same day replaces
        [Fact]
        public async Task MemberCountGoesToTheServerAndItsDay()
        {
            var db = await StartAsync();
            var data = db.BotGuilds();
            var today = Noon.Date;
            var at = today.AddHours(9).AddMinutes(21);

            await data.SaveGuild(Server(), [], Noon.AddDays(-1), RunId, 1);

            await data.SaveMemberCount(GuildId, 40, today, at);
            await data.SaveMemberCount(GuildId, 41, today, at.AddSeconds(1));
            await data.SaveMemberCount(GuildId, 45, today.AddDays(1), at.AddDays(1));

            var row = await RowAsync(GuildId);
            Assert.Equal(((uint?)45, at.AddDays(1)), (row!.member_count, row.updated_at));
            Assert.Equal([(today, GuildId, (uint?)41), (today.AddDays(1), GuildId, (uint?)45)],
                await fixture.RootQueryAsync<(DateTime, ulong, uint?)>($"SELECT day, guild_id, member_count FROM {MariaDbFixture.GuildDailyTable} ORDER BY day"));
        }

        // Strict mode would refuse a name that's too long, and with it the whole server, which would never be written
        [Fact]
        public async Task LongNamesAreClipped()
        {
            var db = await StartAsync();
            var name = string.Concat(Enumerable.Range(0, 40).Select(i => $"{i:00000000}\U0001F600"));
            var stored = string.Concat(Enumerable.Range(0, 11).Select(i => $"{i:00000000}\U0001F600")) + "0";

            Assert.Equal(100, stored.EnumerateRunes().Count());

            await db.BotGuilds().SaveGuild(Server(name: name), [General with { name = name, category = name }], Noon, RunId, 1);

            Assert.Equal(stored, (await RowAsync(GuildId))!.name);
            var channel = Assert.Single(await ChannelsAsync());
            Assert.Equal((stored, (string?)stored), (channel.name, channel.category));
        }

        // The test's own session, for holding a lock
        static async Task<object?> RawAsync(MySqlConnection connection, string sql)
        {
            await using var command = new MySqlCommand(sql, connection);
            return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }
}
