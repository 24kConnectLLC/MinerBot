using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Guilds.Models;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using MinerBot.Dashboard.Servers;
using MinerBot.Dashboard.Status;

namespace MinerBot.Dashboard.Tests
{
    // The servers pages' rules: each setting's status, the channels' groups, how current the copy is, the icons, the
    // members' trend and the filters. No database.
    public class ServerRulesTests
    {
        static readonly DateTime Now = new(2026, 11, 19, 14, 3, 30, DateTimeKind.Utc);
        const ulong ServerId = 1000000000000000701, OtherId = 1000000000000000702;

        static BotChannelRow Channel(ulong id, ulong guild = ServerId, string name = "general", bool view = true, bool send = true, bool embed = true,
            ulong? categoryId = 3900, string? category = "Text Channels", int? categoryPosition = 0, int position = 0) => new()
        {
            channel_id = id,
            guild_id = guild,
            name = name,
            type = GuildChannelType.Text,
            category = category,
            category_id = categoryId,
            category_position = categoryPosition,
            position = position,
            can_view = view,
            can_send = send,
            can_embed = embed
        };

        static Guild Settings(ulong armor = 0, ulong maps = 0, ulong lobbies = 0, ulong announcements = 0, ulong guild = ServerId) =>
            new() { guild_id = guild, armor_channel_id = armor, map_channel_id = maps, lobby_channel_id = lobbies, announcements_channel_id = announcements };

        static Dictionary<ulong, BotChannelRow> Copy(params BotChannelRow[] channels) => channels.ToDictionary(channel => channel.channel_id);

        static SettingStatus Status(SettingKind kind, Guild? settings, Dictionary<ulong, BotChannelRow> channels, bool botIsIn = true) =>
            SettingStatuses.Status(kind, ServerId, botIsIn, settings, channels);

        // Each status in turn: not set up, off, OK, can't post, another server's channel, can't check, and not in
        [Fact]
        public void EachSettingStatus()
        {
            var copy = Copy(Channel(1), Channel(2, send: false), Channel(3, guild: OtherId), Channel(4, view: false, send: false, embed: false, name: "___hidden___"));

            Assert.Equal(SettingState.NotSetUp, Status(SettingKind.Armor, null, copy).State);
            Assert.Equal(SettingState.Off, Status(SettingKind.Armor, Settings(), copy).State);
            Assert.Equal(SettingState.Ok, Status(SettingKind.Armor, Settings(armor: 1), copy).State);

            var cantPost = Status(SettingKind.Maps, Settings(maps: 2), copy);
            Assert.Equal(SettingState.CantPost, cantPost.State);
            Assert.Equal(["Send Messages"], cantPost.Missing);
            Assert.Equal("Can't post: missing Send Messages", SettingStatuses.Words(cantPost));

            var other = Status(SettingKind.Lobbies, Settings(lobbies: 3), copy);
            Assert.Equal(SettingState.OtherServer, other.State);
            Assert.Equal(OtherId, other.Channel?.guild_id);

            // A channel the copy doesn't have, such as a voice channel: Can't check, never counted as a problem, since the
            // copy can't tell a voice channel's chat, which posts reach, from a forum or a deleted channel, which they don't
            var unknown = Status(SettingKind.Announcements, Settings(announcements: 999), copy);
            Assert.Equal(SettingState.CantCheck, unknown.State);
            Assert.False(SettingStatuses.IsProblem(unknown.State));

            var hidden = Status(SettingKind.Armor, Settings(armor: 4), copy);
            Assert.Equal("Can't post: missing View Channel, Send Messages, Embed Links", SettingStatuses.Words(hidden));

            Assert.Equal(SettingState.NotIn, Status(SettingKind.Armor, Settings(armor: 1), copy, botIsIn: false).State);
            Assert.Equal(SettingState.NotIn, Status(SettingKind.Armor, null, copy, botIsIn: false).State);
            Assert.Equal("MinerBot isn't in this server", SettingStatuses.Label(SettingState.NotIn));
        }

        // From the bot's posting code: armor, maps and lobbies are embeds and need Embed Links, and an announcement is plain
        // text, which needs no Embed Links
        [Fact]
        public void EmbedsNeedEmbedLinksAndAnnouncementsDont()
        {
            var copy = Copy(Channel(1, embed: false));

            foreach (var kind in new[] { SettingKind.Armor, SettingKind.Maps, SettingKind.Lobbies })
                Assert.Equal(["Embed Links"], Status(kind, Settings(1, 1, 1, 1), copy).Missing);

            Assert.Equal(SettingState.Ok, Status(SettingKind.Announcements, Settings(1, 1, 1, 1), copy).State);
        }

        [Fact]
        public void OnlyWhatCantPostIsAProblem()
        {
            Assert.Equal([SettingState.CantPost, SettingState.OtherServer], Enum.GetValues<SettingState>().Where(SettingStatuses.IsProblem));
        }

        // The four in /admin set_channels' order, each from its own column
        [Fact]
        public void FourKindsFromTheirColumns()
        {
            var statuses = SettingStatuses.Of(ServerId, true, Settings(1, 2, 3, 4), Copy());

            Assert.Equal([SettingKind.Armor, SettingKind.Maps, SettingKind.Lobbies, SettingKind.Announcements], statuses.Select(status => status.Kind));
            Assert.Equal([1UL, 2UL, 3UL, 4UL], statuses.Select(status => status.ChannelId));
        }

        // The channels come in Discord's order, so a group is each run of one category, and two of the same name stay apart.
        // Hidden channels are counted, not listed.
        [Fact]
        public void ChannelsGroupByCategoryWithHiddenCounted()
        {
            var channels = new[]
            {
                Channel(10, categoryId: null, category: null, categoryPosition: null, name: "rules"),
                Channel(11, categoryId: null, category: null, categoryPosition: null, name: "___hidden___", view: false),
                Channel(20, categoryId: 3900, name: "general"),
                Channel(21, categoryId: 3900, name: "___hidden___", view: false, position: 1),
                Channel(22, categoryId: 3900, name: "___hidden___", view: false, position: 2),
                Channel(30, categoryId: 3901, category: "Text Channels", categoryPosition: 1, name: "memes"),
                Channel(40, categoryId: 3902, category: "___hidden___", categoryPosition: 2, name: "___hidden___", view: false)
            };

            var groups = ChannelGroups.Of(channels);

            Assert.Equal([null, 3900UL, 3901UL, 3902UL], groups.Select(group => group.CategoryId));
            Assert.Equal(["rules", "general", "memes", ""], groups.Select(group => string.Join(", ", group.Channels.Select(channel => channel.name))));
            Assert.Equal([1, 2, 0, 1], groups.Select(group => group.Hidden));
            Assert.True(ChannelGroups.IsHiddenCategory(groups[3].Category));

            // A channel named so that the bot can see is a real one
            Assert.False(ChannelGroups.IsHidden(Channel(50, name: "___hidden___")));
        }

        static BotStatusRow Row(DateTime heartbeat, DateTime? stopped = null, string discord = DiscordState.Connected, DateTime? discordSince = null) => new()
        {
            started_at = heartbeat.AddHours(-1),
            heartbeat_at = heartbeat,
            stopped_at = stopped,
            discord_state = discord,
            discord_since = discordSince ?? heartbeat.AddHours(-1),
            steam_state = SteamState.LoggedOn
        };

        static BotEventRow Event(DateTime at, string kind, string? detail = null) => new() { id = 1, at = at, kind = kind, level = EventLevel.Warning, detail = detail };

        static CopyFreshness Freshness(BotStatusRow? row, BotEventRow? outage = null, BotEventRow? drop = null, DateTime? ended = null) =>
            CopyFreshnesses.Of(row, BotStates.Of(row, Now), Now, outage, drop, ended);

        // Current while the bot runs and is connected, apart from the 5 minutes after an outage of MinerBot's database,
        // when it can still date from the outage's start
        [Fact]
        public void CopyIsCurrentWhileConnected()
        {
            Assert.True(Freshness(Row(Now.AddSeconds(-10))).Current);

            var outage = Event(Now.AddMinutes(-5).AddMilliseconds(1), EventWords.DatabaseUnreachable, "from 2026-11-19 13:40:00 to 2026-11-19 13:58:30 UTC");
            var during = Freshness(Row(Now.AddSeconds(-10)), outage: outage);
            Assert.False(during.Current);
            Assert.Equal(new DateTime(2026, 11, 19, 13, 40, 0, DateTimeKind.Utc), during.AsOf);
            Assert.Equal(DateTimeKind.Utc, during.AsOf!.Value.Kind);

            Assert.True(Freshness(Row(Now.AddSeconds(-10)), outage: Event(Now.AddMinutes(-5), EventWords.DatabaseUnreachable, outage.detail)).Current);

            // A detail the page can't read still says there was an outage
            var unread = Freshness(Row(Now.AddSeconds(-10)), outage: Event(Now.AddMinutes(-1), EventWords.DatabaseUnreachable, "from somewhere"));
            Assert.False(unread.Current);
            Assert.Null(unread.AsOf);
        }

        // Otherwise as old as the first drop since the last connection, or the bot's last check-in, whichever came first
        [Fact]
        public void CopyIsAsOfTheDropOrTheLastCheckIn()
        {
            Assert.Null(Freshness(null).AsOf);
            Assert.False(Freshness(null).Current);

            var stopped = Now.AddHours(-2);
            Assert.Equal(stopped, Freshness(Row(stopped, stopped: stopped)).AsOf);

            // Late isn't checking in either, though its row still says connected
            var late = Freshness(Row(Now.AddMinutes(-4)));
            Assert.False(late.Current);
            Assert.Equal(Now.AddMinutes(-4), late.AsOf);

            var offline = Now.AddMinutes(-30);
            Assert.Equal(offline, Freshness(Row(offline)).AsOf);
            Assert.Equal(offline.AddMinutes(-5), Freshness(Row(offline), drop: Event(offline.AddMinutes(-5), EventWords.DiscordDisconnected)).AsOf);

            // Running, but off Discord
            var dropped = Row(Now.AddSeconds(-10), discord: DiscordState.Disconnected, discordSince: Now.AddMinutes(-3));
            Assert.Equal(Now.AddMinutes(-4), Freshness(dropped, drop: Event(Now.AddMinutes(-4), EventWords.DiscordDisconnected, "WebSocketException")).AsOf);
            Assert.Equal(Now.AddMinutes(-3), Freshness(dropped).AsOf);
        }

        // A run that stops or crashes while connected records no drop, and a later run that never connects writes nothing to
        // the copy, so the end of the run that made the last connection dates it, before that later run's own failed
        // attempts, its row's since, or its stop
        [Fact]
        public void CopyIsAsOfTheEndOfTheRunThatLastConnected()
        {
            var stopped = Now.AddDays(-1);
            var restarted = Now.AddMinutes(-10);
            var attempt = Event(restarted.AddSeconds(4), EventWords.DiscordDisconnected, "WebSocketException");
            var running = Row(Now.AddSeconds(-10), discord: DiscordState.Connecting, discordSince: restarted.AddSeconds(5));
            var ended = CopyFreshnesses.RunEnded(Event(stopped, EventWords.Stopping), Event(restarted, EventWords.Started), null);

            Assert.Equal(stopped, ended);
            Assert.Equal(stopped, Freshness(running, drop: attempt, ended: ended).AsOf);
            Assert.Equal(stopped, Freshness(running, ended: ended).AsOf);
            Assert.Equal("MinerBot isn't connected to Discord, so this copy is as of when it last heard from Discord.", Freshness(running, drop: attempt, ended: ended).Reason);
            Assert.Equal(stopped, Freshness(Row(Now.AddMinutes(-2), stopped: Now.AddMinutes(-2), discord: DiscordState.Connecting), drop: attempt, ended: ended).AsOf);
            Assert.Equal(stopped, Freshness(Row(Now.AddMinutes(-30), discord: DiscordState.Connecting), drop: attempt, ended: ended).AsOf);

            // A drop before that run ended still comes first
            Assert.Equal(stopped.AddHours(-1), Freshness(running, drop: Event(stopped.AddHours(-1), EventWords.DiscordDisconnected), ended: ended).AsOf);

            // A run that ended without a clean stop: its last heartbeat, which the next run's detail gives, else that start
            var unclean = Event(restarted.AddSeconds(1), EventWords.UncleanEnd, "the run that started at 2026-11-15 10:00:00 UTC never stopped cleanly; its last heartbeat was at 2026-11-18 14:03:00 UTC");
            var heartbeat = CopyFreshnesses.RunEnded(null, Event(restarted, EventWords.Started), unclean);
            Assert.Equal(new DateTime(2026, 11, 18, 14, 3, 0, DateTimeKind.Utc), heartbeat);
            Assert.Equal(DateTimeKind.Utc, heartbeat!.Value.Kind);
            Assert.Equal(restarted, CopyFreshnesses.RunEnded(null, Event(restarted, EventWords.Started), Event(restarted, EventWords.UncleanEnd, "its last heartbeat was at 2026-11-18 14:03:00 UTC")));
            Assert.Equal(restarted, CopyFreshnesses.RunEnded(null, Event(restarted, EventWords.Started), null));

            // Nothing since the last connection: that run goes on
            Assert.Null(CopyFreshnesses.RunEnded(null, null, null));
        }

        // A server in an outage keeps its channels as they were before it, so its page doesn't call them current, whatever
        // the rest of the copy is; a copy that's already older keeps its time
        [Fact]
        public void ServerInAnOutageIsNotCurrent()
        {
            var current = Freshness(Row(Now.AddSeconds(-10)));
            var outage = CopyFreshnesses.ForServer(current, inOutage: true);

            Assert.True(current.Current);
            Assert.False(outage.Current);
            Assert.Null(outage.AsOf);
            Assert.Equal("Discord reports this server unavailable, so MinerBot's copy of its channels is as it was before the outage. The rest of the copy is current.", outage.Reason);
            Assert.Same(current, CopyFreshnesses.ForServer(current, inOutage: false));

            var older = Freshness(Row(Now.AddMinutes(-30)));
            Assert.Same(older, CopyFreshnesses.ForServer(older, inOutage: true));
        }

        [Theory]
        [InlineData("from 2026-11-18 00:34:42 to 2026-11-18 00:39:01 UTC", "2026-11-18T00:34:42Z")]
        [InlineData("from 2026-11-18 00:34:42 to 2026-11-18 00:39:01", null)]
        [InlineData("2026-11-18 00:34:42", null)]
        [InlineData(null, null)]
        public void OutageStartIsReadFromTheBotsDetail(string? detail, string? start)
        {
            Assert.Equal(start == null ? null : DateTime.Parse(start, null, System.Globalization.DateTimeStyles.AdjustToUniversal), CopyFreshnesses.OutageStart(detail));
        }

        // Discord's CDN, made only from the ID and a hash in Discord's form
        [Theory]
        [InlineData("0123456789abcdef0123456789abcdef", "https://cdn.discordapp.com/icons/1000000000000000701/0123456789abcdef0123456789abcdef.png?size=64")]
        [InlineData("a_0123456789abcdef0123456789abcdef", "https://cdn.discordapp.com/icons/1000000000000000701/a_0123456789abcdef0123456789abcdef.png?size=64")]
        [InlineData("0123456789ABCDEF0123456789ABCDEF", null)]
        [InlineData("0123456789abcdef", null)]
        [InlineData("../../x/0123456789abcdef0123456", null)]
        [InlineData("0123456789abcdef0123456789abcdef\n", null)]
        [InlineData(null, null)]
        public void IconsOnlyFromDiscordsHashes(string? hash, string? url)
        {
            Assert.Equal(url, ServerRows.IconUrl(ServerId, hash));
        }

        [Theory]
        [InlineData(1000000000000000701UL, "/admin/servers/1000000000000000701")]
        [InlineData(9223372036854775807UL, "/admin/servers/9223372036854775807")]
        [InlineData(9223372036854775808UL, null)]
        [InlineData(0UL, null)]
        public void LinksOnlyForIdsTheRouteTakes(ulong id, string? link)
        {
            Assert.Equal(link, ServerRows.Link(id));
        }

        static GuildDayRow Day(int day, uint? members) => new() { day = new DateTime(2026, 11, day, 0, 0, 0, DateTimeKind.Utc), guild_id = ServerId, member_count = members };

        // The change from the first count in the stretch to the last, since the first count when counting began later
        [Fact]
        public void MembersTrend()
        {
            var from = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

            Assert.Null(ServerRows.Trend([], from));

            var since = ServerRows.Trend([Day(18, 120), Day(19, 125)], from)!;
            Assert.Equal(5, since.Change);
            Assert.True(since.SinceFirstCount);
            Assert.Equal("up 5 since 2026-11-18", ServerRows.TrendWords(since, 30));

            var full = ServerRows.Trend([Day(18, 120), Day(19, 118)], new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc))!;
            Assert.Equal("down 2 over 30 days", ServerRows.TrendWords(full, 30));

            Assert.Equal("first counted on 2026-11-19", ServerRows.TrendWords(ServerRows.Trend([Day(19, 125)], from)!, 30));
            Assert.Equal("no change since 2026-11-18", ServerRows.TrendWords(ServerRows.Trend([Day(18, 125), Day(19, null), Day(19, 125)], from)!, 30));
        }

        static BotGuildRow Guild(ulong id, DateTime? left = null, bool available = true) =>
            new() { guild_id = id, name = "Server " + id, left_at = left, available = available, first_seen_at = Now, updated_at = Now };

        // Each filter shows its servers, and has problems takes in an outage as well as a setting MinerBot can't post in
        [Fact]
        public void FiltersShowTheirServers()
        {
            var guilds = new[] { Guild(1), Guild(2), Guild(3), Guild(4, available: false), Guild(5), Guild(6, left: Now.AddDays(-1)) };
            var settings = new[] { Settings(armor: 11, guild: 1), Settings(armor: 12, guild: 3), Settings(guild: 4), Settings(armor: 99, guild: 5), Settings(armor: 16, guild: 6) };
            var channels = new[] { Channel(11, guild: 1), Channel(12, guild: 3, send: false) };

            var rows = ServerRows.Rows(guilds, settings, channels, [], Now);
            ulong[] Shown(string word) => rows.Where(ServerRows.FromWord(word)!.Shows).Select(row => row.Guild.guild_id).ToArray();

            Assert.Equal([1UL, 2UL, 3UL, 4UL, 5UL], Shown("in"));
            Assert.Equal([2UL], Shown("not-set-up"));
            Assert.Equal([3UL, 4UL], Shown("problems"));
            Assert.Equal([5UL], Shown("cant-check"));
            Assert.Equal([6UL], Shown("left"));
            Assert.Equal([1UL, 2UL, 3UL, 4UL, 5UL, 6UL], Shown("all"));

            Assert.Same(ServerRows.Default, ServerRows.FromWord(null));
            Assert.Same(ServerRows.Default, ServerRows.FromWord(""));
            Assert.Null(ServerRows.FromWord("everything"));

            // The settings of the left server, and of one never recorded, are apart, and only the servers the bot is in have
            // their channels looked up
            Assert.Equal([6UL, 7UL], ServerRows.SettingsElsewhere(guilds, [.. settings, Settings(armor: 17, guild: 7)]).Select(row => row.guild_id));
            Assert.Equal([11UL, 12UL, 99UL], ServerRows.SettingChannels(guilds, settings).Order());
        }
    }
}
