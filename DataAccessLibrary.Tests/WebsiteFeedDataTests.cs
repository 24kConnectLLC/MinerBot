using DataAccessLibrary.Feed;
using DataAccessLibrary.Feed.Models;
using DataAccessLibrary.Tests.Database;
using MySql.Data.MySqlClient;

namespace DataAccessLibrary.Tests
{
    // The feed the bot writes for the website's Lobbies and Maps pages, in the website's own tables, which the fixture
    // builds from the website's migration. Each read of Steam lands whole or not at all, a row keeps when it was first
    // seen, and a read where Steam gave no answer changes nothing but when the bot tried. Made-up lobbies and maps only.
    [Collection("Database")]
    public class WebsiteFeedDataTests(MariaDbFixture fixture)
    {
        static readonly DateTime Noon = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        static readonly DateTime Later = Noon.AddSeconds(30);

        // Steam's times for the maps, in whole seconds as Steam gives them
        static readonly DateTime Monday = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);

        const ulong Host = 76561197960265729;
        const string Emoji = "\U0001F600";

        async Task<MariaDbFixture> StartAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.FeedLobbyTable, MariaDbFixture.FeedMapTable, MariaDbFixture.FeedStateTable);
            return db;
        }

        static FeedLobby Lobby(ulong id, string host = "Host", ushort players = 1) => new()
        {
            lobby_id = id,
            owner_id = Host + id,
            host_name = host,
            host_kind = FeedHostKind.Player,
            map_name = "Test Map",
            modes = "Deathmatch",
            version_number = 105,
            version_label = "Update 41",
            is_current = true,
            players = players,
            max_players = 8
        };

        static FeedMap Map(ulong id, DateTime updated, string title = "Test Map", uint subscriptions = 5) => new()
        {
            file_id = id,
            title = title,
            description = "A made-up map.",
            preview_url = "https://example.com/preview.jpg",
            creator_id = Host,
            tags = "Deathmatch,Small",
            file_size = 123456,
            subscriptions = subscriptions,
            lifetime_subscriptions = subscriptions + 1,
            favorited = 2,
            lifetime_favorited = 3,
            views = 40,
            time_created = Monday,
            time_updated = updated
        };

        // As the tables have them, by name
        public sealed record LobbyRow
        {
            public ulong lobby_id { get; set; }
            public ulong owner_id { get; set; }
            public string host_name { get; set; } = "";
            public string host_kind { get; set; } = "";
            public string map_name { get; set; } = "";
            public string modes { get; set; } = "";
            public uint version_number { get; set; }
            public string version_label { get; set; } = "";
            public int is_current { get; set; }
            public ushort players { get; set; }
            public ushort max_players { get; set; }
            public DateTime first_seen { get; set; }
            public DateTime last_seen { get; set; }
        }

        public sealed record MapRow
        {
            public ulong file_id { get; set; }
            public string title { get; set; } = "";
            public string? description { get; set; }
            public string preview_url { get; set; } = "";
            public ulong creator_id { get; set; }
            public string tags { get; set; } = "";
            public ulong file_size { get; set; }
            public uint subscriptions { get; set; }
            public uint lifetime_subscriptions { get; set; }
            public uint favorited { get; set; }
            public uint lifetime_favorited { get; set; }
            public uint views { get; set; }
            public DateTime time_created { get; set; }
            public DateTime time_updated { get; set; }
            public DateTime first_seen { get; set; }
            public DateTime last_seen { get; set; }
        }

        public sealed record StateRow
        {
            public string feed { get; set; } = "";
            public DateTime checked_at { get; set; }
            public DateTime? ok_at { get; set; }
            public uint item_count { get; set; }
        }

        Task<List<LobbyRow>> LobbiesAsync() => fixture.RootQueryAsync<LobbyRow>($"SELECT * FROM {MariaDbFixture.FeedLobbyTable} ORDER BY lobby_id");

        Task<List<MapRow>> MapsAsync() => fixture.RootQueryAsync<MapRow>($"SELECT * FROM {MariaDbFixture.FeedMapTable} ORDER BY file_id");

        Task<List<StateRow>> StateAsync() => fixture.RootQueryAsync<StateRow>($"SELECT * FROM {MariaDbFixture.FeedStateTable} ORDER BY feed");

        static LobbyRow Stored(FeedLobby lobby, DateTime firstSeen, DateTime lastSeen) => new()
        {
            lobby_id = lobby.lobby_id,
            owner_id = lobby.owner_id,
            host_name = lobby.host_name,
            host_kind = lobby.host_kind,
            map_name = lobby.map_name,
            modes = lobby.modes,
            version_number = lobby.version_number,
            version_label = lobby.version_label,
            is_current = lobby.is_current ? 1 : 0,
            players = lobby.players,
            max_players = lobby.max_players,
            first_seen = firstSeen,
            last_seen = lastSeen
        };

        static MapRow Stored(FeedMap map, DateTime firstSeen, DateTime lastSeen) => new()
        {
            file_id = map.file_id,
            title = map.title,
            description = map.description,
            preview_url = map.preview_url,
            creator_id = map.creator_id,
            tags = map.tags,
            file_size = map.file_size,
            subscriptions = map.subscriptions,
            lifetime_subscriptions = map.lifetime_subscriptions,
            favorited = map.favorited,
            lifetime_favorited = map.lifetime_favorited,
            views = map.views,
            time_created = map.time_created,
            time_updated = map.time_updated,
            first_seen = firstSeen,
            last_seen = lastSeen
        };

        static StateRow State(string feed, DateTime checkedAt, DateTime? okAt, uint count) => new() { feed = feed, checked_at = checkedAt, ok_at = okAt, item_count = count };

        // The lobbies

        // Every column, with the biggest IDs the columns hold, a lobby of the game's own host, and one whose host, map,
        // modes and branch aren't known
        [Fact]
        public async Task FirstReadWritesEveryLobbyAndTheFeedsRow()
        {
            var db = await StartAsync();
            var gamenight = Lobby(2, host: "Gamenight") with { host_kind = FeedHostKind.Gamenight, owner_id = ulong.MaxValue - 1, players = 8 };
            var unknown = new FeedLobby { lobby_id = ulong.MaxValue, owner_id = Host, version_number = 106, max_players = ushort.MaxValue };

            Assert.Equal(3, await db.Feed().ReplaceLobbies([Lobby(1), gamenight, unknown], Noon));

            Assert.Equal([Stored(Lobby(1), Noon, Noon), Stored(gamenight, Noon, Noon), Stored(unknown, Noon, Noon)], await LobbiesAsync());
            Assert.Equal([State(FeedName.Lobbies, Noon, Noon, 3)], await StateAsync());
        }

        // The whole set is replaced: a lobby that's still open keeps when it was first seen and takes everything else from
        // the read, a new one is added, and one that closed goes
        [Fact]
        public async Task LaterReadKeepsFirstSeenAddsNewLobbiesAndDeletesClosedOnes()
        {
            var db = await StartAsync();
            var feed = db.Feed();

            await feed.ReplaceLobbies([Lobby(1), Lobby(2), Lobby(3)], Noon);

            var changed = Lobby(2, host: "Another Host", players: 5) with
            {
                owner_id = Host,
                host_kind = FeedHostKind.Official,
                map_name = "Another Map",
                modes = "",
                version_number = 92,
                version_label = "Update 40",
                is_current = false,
                max_players = 16
            };

            Assert.Equal(2, await feed.ReplaceLobbies([changed, Lobby(4)], Later));

            Assert.Equal([Stored(changed, Noon, Later), Stored(Lobby(4), Later, Later)], await LobbiesAsync());
            Assert.Equal([State(FeedName.Lobbies, Later, Later, 2)], await StateAsync());
        }

        // Steam answering that nobody hosts is an answer, so the table is emptied
        [Fact]
        public async Task AnswerWithNoLobbiesEmptiesTheTable()
        {
            var db = await StartAsync();
            var feed = db.Feed();

            await feed.ReplaceLobbies([Lobby(1), Lobby(2)], Noon);

            Assert.Equal(0, await feed.ReplaceLobbies([], Later));

            Assert.Empty(await LobbiesAsync());
            Assert.Equal([State(FeedName.Lobbies, Later, Later, 0)], await StateAsync());
        }

        // A read where Steam gave no answer: the lobbies, when the bot last got an answer and how many it wrote then all
        // stay, and only when it tried moves
        [Fact]
        public async Task ReadThatFailedChangesOnlyWhenTheBotTried()
        {
            var db = await StartAsync();
            var feed = db.Feed();

            await feed.ReplaceLobbies([Lobby(1), Lobby(2)], Noon);
            await feed.MarkChecked(FeedName.Lobbies, Later);

            Assert.Equal([Stored(Lobby(1), Noon, Noon), Stored(Lobby(2), Noon, Noon)], await LobbiesAsync());
            Assert.Equal([State(FeedName.Lobbies, Later, Noon, 2)], await StateAsync());
        }

        // Before the bot has had one answer from Steam, the feed's row says so
        [Fact]
        public async Task FeedThatNeverHadAnAnswerHasNone()
        {
            var db = await StartAsync();
            var feed = db.Feed();

            await feed.MarkChecked(FeedName.Lobbies, Noon);
            await feed.MarkChecked(FeedName.Maps, Noon);
            await feed.MarkChecked(FeedName.Maps, Later);

            Assert.Equal([State(FeedName.Lobbies, Noon, null, 0), State(FeedName.Maps, Later, null, 0)], await StateAsync());
            Assert.Empty(await LobbiesAsync());
            Assert.Empty(await MapsAsync());
        }

        // A write that's made again, such as after one that timed out but went through, lands once
        [Fact]
        public async Task SameReadWrittenAgainLandsOnce()
        {
            var db = await StartAsync();
            var feed = db.Feed();

            await feed.ReplaceLobbies([Lobby(1), Lobby(1), Lobby(2)], Noon);
            Assert.Equal(2, await feed.ReplaceLobbies([Lobby(1), Lobby(2)], Noon));

            Assert.Equal([Stored(Lobby(1), Noon, Noon), Stored(Lobby(2), Noon, Noon)], await LobbiesAsync());
            Assert.Equal([State(FeedName.Lobbies, Noon, Noon, 2)], await StateAsync());
        }

        // More lobbies than one statement writes still land together
        [Fact]
        public async Task ReadOfMoreLobbiesThanOneStatementHolds()
        {
            var db = await StartAsync();
            var lobbies = Enumerable.Range(1, WebsiteFeedData.LobbyBatch + 20).Select(id => Lobby((ulong)id)).ToList();

            Assert.Equal(lobbies.Count, await db.Feed().ReplaceLobbies(lobbies, Noon));
            Assert.Equal(lobbies.Count, (await LobbiesAsync()).Count);

            await db.Feed().ReplaceLobbies(lobbies.Take(3).ToList(), Later);

            Assert.Equal([1ul, 2, 3], (await LobbiesAsync()).Select(row => row.lobby_id));
        }

        // The columns hold whole seconds, so that's what is written, whatever the server would do with the rest. Text that
        // is longer than its column is cut to it, with a character outside the Basic Multilingual Plane kept whole.
        [Fact]
        public async Task TimesAreWholeSecondsAndLongTextIsCutToItsColumn()
        {
            var db = await StartAsync();
            var longHost = new string('h', WebsiteFeedData.HostNameLimit - 1) + Emoji + "x";
            var lobby = Lobby(1, host: longHost) with { map_name = new string('m', 200), modes = new string('o', 300), version_label = new string('v', 100) };

            await db.Feed().ReplaceLobbies([lobby], Noon.AddMilliseconds(999));

            var row = Assert.Single(await LobbiesAsync());
            Assert.Equal((Noon, Noon), (row.first_seen, row.last_seen));
            Assert.Equal(new string('h', WebsiteFeedData.HostNameLimit - 1) + Emoji, row.host_name);
            Assert.Equal((new string('m', 128), new string('o', 255), new string('v', 64)), (row.map_name, row.modes, row.version_label));
            Assert.Equal([State(FeedName.Lobbies, Noon, Noon, 1)], await StateAsync());
        }

        // The maps

        // Every column, with a map that has no description, picture, author or tags
        [Fact]
        public async Task FirstReadWritesEveryMapAndTheFeedsRow()
        {
            var db = await StartAsync();
            var bare = new FeedMap { file_id = ulong.MaxValue, time_created = Monday, time_updated = Monday.AddHours(1) };
            var full = Map(1, Monday.AddHours(2)) with { file_size = ulong.MaxValue, views = uint.MaxValue };

            Assert.Equal(2, await db.Feed().SaveMaps([full, bare], Noon));

            Assert.Equal([Stored(full, Noon, Noon), Stored(bare, Noon, Noon)], await MapsAsync());
            Assert.Null((await MapsAsync())[1].description);
            Assert.Equal([State(FeedName.Maps, Noon, Noon, 2)], await StateAsync());
        }

        // A map that's read again keeps when it was first seen and takes everything else from the read, a description that
        // went included
        [Fact]
        public async Task MapReadAgainKeepsFirstSeenAndTakesItsNewValues()
        {
            var db = await StartAsync();
            var feed = db.Feed();

            await feed.SaveMaps([Map(1, Monday), Map(2, Monday.AddHours(1))], Noon);

            var updated = Map(1, Monday.AddDays(1), title: "Renamed Map", subscriptions: 9) with
            {
                description = null,
                preview_url = "",
                tags = "",
                creator_id = 0,
                time_created = Monday.AddMinutes(1)
            };

            Assert.Equal(2, await feed.SaveMaps([updated, Map(2, Monday.AddHours(1))], Later));

            Assert.Equal([Stored(updated, Noon, Later), Stored(Map(2, Monday.AddHours(1)), Noon, Later)], await MapsAsync());
            Assert.Equal([State(FeedName.Maps, Later, Later, 2)], await StateAsync());
        }

        // The removal rule. A map's number here is also the hour of its last update, so a higher number is a newer map, and a
        // read lists its maps in Steam's order, the newest first unless a test says otherwise.

        static FeedMap Hour(ulong id) => Map(id, Monday.AddHours(id));

        static List<FeedMap> Read(params ulong[] ids) => ids.Select(Hour).ToList();

        async Task<List<ulong>> MapIdsAsync() => (await MapsAsync()).Select(row => row.file_id).ToList();

        // A map the last read held, that this read doesn't and reaches down to, was hidden or removed on Steam, so it goes.
        // The maps that newer ones pushed out of the read stay, as the page's history.
        [Fact]
        public async Task MapHiddenSinceTheLastReadIsDeletedAndTheHistoryStays()
        {
            var db = await StartAsync();
            var feed = db.Feed();

            await feed.SaveMaps(Read(8, 7, 6, 5, 4, 3), Noon);

            // Map 6 was hidden and map 9 is new, so the read still ends at map 3
            Assert.Equal(6, await feed.SaveMaps(Read(9, 8, 7, 5, 4, 3), Later));
            Assert.Equal([3ul, 4, 5, 7, 8, 9], await MapIdsAsync());

            // Two new maps push maps 4 and 3 out of the read
            await feed.SaveMaps(Read(11, 10, 9, 8, 7, 5), Later.AddMinutes(5));
            Assert.Equal([3ul, 4, 5, 7, 8, 9, 10, 11], await MapIdsAsync());

            // Map 8 was hidden, which brings map 4 back into the read
            await feed.SaveMaps(Read(11, 10, 9, 7, 5, 4), Later.AddMinutes(10));
            Assert.Equal([3ul, 4, 5, 7, 9, 10, 11], await MapIdsAsync());

            var history = (await MapsAsync())[0];
            Assert.Equal((Noon, Later), (history.first_seen, history.last_seen));
            Assert.Equal([State(FeedName.Maps, Later.AddMinutes(10), Later.AddMinutes(10), 6)], await StateAsync());
        }

        // A map updated at the very second the read reaches down to counts as within it
        [Fact]
        public async Task HiddenMapUpdatedAtTheSecondTheReadReachesIsDeleted()
        {
            var db = await StartAsync();
            var feed = db.Feed();
            var twin = Map(60, Monday.AddHours(6));

            await feed.SaveMaps([Hour(9), Hour(8), Hour(7), Hour(6), twin, Hour(5)], Noon);
            await feed.SaveMaps(Read(9, 8, 7, 6, 5, 4), Later);

            Assert.Equal([4ul, 5, 6, 7, 8, 9], await MapIdsAsync());
        }

        // Steam's order isn't exactly by time_updated: now and then it ranks a map far above the place its last update
        // gives it. Here map 1 stands near the top, then moves down the read as newer maps come, sits at its end, and
        // leaves it. A rule that went by the read's oldest map would delete, at each of those reads, every row newer than
        // map 1 that the read doesn't hold, which is the whole history. Nothing may be deleted at all.
        [Fact]
        public async Task MapThatSteamRanksAboveItsPlaceDeletesNoHistory()
        {
            var db = await StartAsync();
            var feed = db.Feed();
            var at = Noon;

            Task<int> ReadAsync(params ulong[] ids) => feed.SaveMaps(Read(ids), at = at.AddMinutes(5));

            await ReadAsync(10, 9, 8, 7, 6, 5);
            await ReadAsync(13, 12, 11, 10, 9, 8);

            await ReadAsync(13, 12, 1, 11, 10, 9);
            await ReadAsync(14, 13, 12, 1, 11, 10);
            await ReadAsync(15, 14, 13, 12, 1, 11);
            await ReadAsync(16, 15, 14, 13, 12, 1);
            await ReadAsync(17, 16, 15, 14, 13, 12);

            Assert.Equal([1ul, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17], await MapIdsAsync());
        }

        // The same with two such maps side by side, at the read's end too
        [Fact]
        public async Task TwoMapsAboveTheirPlaceAtTheReadsEndDeleteNoHistory()
        {
            var db = await StartAsync();
            var feed = db.Feed();

            await feed.SaveMaps(Read(13, 12, 11, 10, 9, 8), Noon);
            await feed.SaveMaps(Read(15, 14, 13, 12, 2, 1), Later);
            await feed.SaveMaps(Read(16, 15, 14, 13, 2, 1), Later.AddMinutes(5));
            await feed.SaveMaps(Read(18, 17, 16, 15, 14, 13), Later.AddMinutes(10));

            Assert.Equal([1ul, 2, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18], await MapIdsAsync());
        }

        // A map that was hidden is still noticed while another stands above its place in the same read
        [Fact]
        public async Task HiddenMapIsDeletedWhileAnotherStandsAboveItsPlace()
        {
            var db = await StartAsync();
            var feed = db.Feed();

            await feed.SaveMaps(Read(9, 8, 1, 7, 6, 5), Noon);
            await feed.SaveMaps(Read(9, 1, 7, 6, 5, 4), Later);

            Assert.Equal([1ul, 4, 5, 6, 7, 9], await MapIdsAsync());
        }

        // Only a row of the last good read can go. One that an earlier read held, and that this read reaches down to
        // without holding it, stays: it left the read before, and nothing says it left Steam.
        [Fact]
        public async Task RowTheLastReadDidNotHoldIsNeverDeleted()
        {
            var db = await StartAsync();
            var feed = db.Feed();

            await feed.SaveMaps(Read(9, 8, 7, 6, 5, 4), Noon);
            await feed.SaveMaps(Read(15, 14, 13, 12, 11, 10), Later);

            // Maps 12, 11 and 10 were hidden, and the read now reaches down to map 1, past maps 9 to 4, which it doesn't hold
            await feed.SaveMaps(Read(15, 14, 13, 3, 2, 1), Later.AddMinutes(5));

            Assert.Equal([1ul, 2, 3, 4, 5, 6, 7, 8, 9, 13, 14, 15], await MapIdsAsync());
        }

        // The price of not trusting the read's last maps: when one of its last two is hidden, the read no longer reaches down
        // to it, so its row stays
        [Fact]
        public async Task HiddenMapAmongTheReadsLastTwoStays()
        {
            var db = await StartAsync();
            var feed = db.Feed();

            await feed.SaveMaps(Read(9, 8, 7, 6, 5, 4), Noon);
            await feed.SaveMaps(Read(9, 8, 7, 6, 5, 3), Later);
            await feed.SaveMaps(Read(9, 8, 7, 6, 3, 2), Later.AddMinutes(5));

            Assert.Equal([2ul, 3, 4, 5, 6, 7, 8, 9], await MapIdsAsync());
            Assert.Equal(3, WebsiteFeedData.ReachMaps);
        }

        // The last good read is the one the feed's row names. Reads that failed since don't change it, so a map hidden
        // meanwhile still goes at the next good read, and a feed whose row is gone deletes nothing.
        [Fact]
        public async Task LastGoodReadIsTheOneTheFeedsRowNames()
        {
            var db = await StartAsync();
            var feed = db.Feed();

            await feed.SaveMaps(Read(9, 8, 7, 6, 5, 4), Noon);
            await feed.MarkChecked(FeedName.Maps, Later);
            await feed.MarkChecked(FeedName.Maps, Later.AddMinutes(5));
            await feed.SaveMaps(Read(9, 7, 6, 5, 4, 3), Later.AddMinutes(10));

            Assert.Equal([3ul, 4, 5, 6, 7, 9], await MapIdsAsync());

            await db.ClearAsync(MariaDbFixture.FeedStateTable);
            await feed.SaveMaps(Read(9, 6, 5, 4, 3, 2), Later.AddMinutes(15));

            Assert.Equal([2ul, 3, 4, 5, 6, 7, 9], await MapIdsAsync());
            Assert.Equal([State(FeedName.Maps, Later.AddMinutes(15), Later.AddMinutes(15), 6)], await StateAsync());
        }

        // A read written again, as after a write that timed out but went through, deletes nothing more
        [Fact]
        public async Task SameMapReadWrittenAgainLandsOnce()
        {
            var db = await StartAsync();
            var feed = db.Feed();

            await feed.SaveMaps(Read(9, 8, 7, 6, 5, 4), Noon);
            await feed.SaveMaps(Read(10, 9, 7, 6, 5, 4), Later);
            await feed.SaveMaps(Read(10, 9, 7, 6, 5, 4), Later);

            Assert.Equal([4ul, 5, 6, 7, 9, 10], await MapIdsAsync());
            Assert.Equal([State(FeedName.Maps, Later, Later, 6)], await StateAsync());
        }

        // A read that failed is recorded with MarkChecked, and no map changes
        [Fact]
        public async Task MapReadThatFailedChangesOnlyWhenTheBotTried()
        {
            var db = await StartAsync();
            var feed = db.Feed();

            await feed.SaveMaps([Map(1, Monday)], Noon);
            await feed.MarkChecked(FeedName.Maps, Later);

            Assert.Equal([Stored(Map(1, Monday), Noon, Noon)], await MapsAsync());
            Assert.Equal([State(FeedName.Maps, Later, Noon, 1)], await StateAsync());
        }

        // More maps than one statement writes, with the longest description the feed takes, in a script outside the Basic
        // Multilingual Plane, over a connection string that names no character set, as the bot's own may not
        [Fact]
        public async Task LongDescriptionsLandWholeOverAnyConnectionString()
        {
            var db = await StartAsync();
            var description = string.Concat(Enumerable.Repeat(Emoji, WebsiteFeedData.DescriptionLimit));
            var maps = Enumerable.Range(1, WebsiteFeedData.MapBatch + 5)
                .Select(id => Map((ulong)id, Monday.AddMinutes(id), title: $"Map {Emoji} {id}") with { description = description })
                .ToList();

            var feed = new WebsiteFeedData(new SQLConnectionFactory(new Dictionary<DBConnectionName, string>
            {
                [DBConnectionName.Feed] = db.LoginConnectionWithoutCharacterSet(MariaDbFixture.SiteDatabase)
            }));

            Assert.Equal(maps.Count, await feed.SaveMaps(maps, Noon));

            var rows = await MapsAsync();
            Assert.Equal(maps.Count, rows.Count);
            Assert.All(rows, row => Assert.Equal(description, row.description));
            Assert.Equal($"Map {Emoji} 1", rows[0].title);
        }

        // A description longer than the feed takes is cut, and a title to its column
        [Fact]
        public async Task LongerTextIsCut()
        {
            var db = await StartAsync();
            var map = Map(1, Monday, title: new string('t', 300)) with { description = new string('d', WebsiteFeedData.DescriptionLimit + 50) };

            await db.Feed().SaveMaps([map], Noon);

            var row = Assert.Single(await MapsAsync());
            Assert.Equal((new string('t', 255), new string('d', WebsiteFeedData.DescriptionLimit)), (row.title, row.description));
        }

        // What the repository refuses before any SQL, so a row that would break a rule of the feed never reaches the tables

        public static TheoryData<string, Func<WebsiteFeedData, Task>> Refused() => new()
        {
            { "a local time", feed => feed.ReplaceLobbies([Lobby(1)], Noon.ToLocalTime()) },
            { "a host kind the website doesn't know", feed => feed.ReplaceLobbies([Lobby(1) with { host_kind = "bot" }], Noon) },
            { "a lobby without its texts", feed => feed.ReplaceLobbies([Lobby(1) with { modes = null! }], Noon) },
            { "more lobbies than a read writes", feed => feed.ReplaceLobbies(Enumerable.Range(1, WebsiteFeedData.LobbyLimit + 1).Select(id => Lobby((ulong)id)).ToList(), Noon) },
            { "a read with no maps", feed => feed.SaveMaps([], Noon) },
            { "more maps than a read writes", feed => feed.SaveMaps(Enumerable.Range(1, WebsiteFeedData.MapLimit + 1).Select(id => Map((ulong)id, Monday)).ToList(), Noon) },
            { "a map without its update time", feed => feed.SaveMaps([Map(1, default)], Noon) },
            { "a map without its creation time", feed => feed.SaveMaps([Map(1, Monday) with { time_created = default }], Noon) },
            { "a map's local time", feed => feed.SaveMaps([Map(1, Monday.ToLocalTime())], Noon) },
            { "a picture link that doesn't fit", feed => feed.SaveMaps([Map(1, Monday) with { preview_url = "https://example.com/" + new string('a', 500) }], Noon) },
            { "tags that don't fit", feed => feed.SaveMaps([Map(1, Monday) with { tags = new string('a', 501) }], Noon) },
            { "a feed the website doesn't have", feed => feed.MarkChecked("armor", Noon) },
            { "a local time for a read that failed", feed => feed.MarkChecked(FeedName.Maps, Noon.ToLocalTime()) }
        };

        [Theory]
        [MemberData(nameof(Refused))]
        public async Task RowThatBreaksARuleIsRefusedBeforeAnySql(string what, Func<WebsiteFeedData, Task> write)
        {
            var database = new NoDatabase();

            await Assert.ThrowsAnyAsync<ArgumentException>(() => write(new WebsiteFeedData(database)));

            Assert.True(database.Created == 0, $"{what} reached the database");
        }

        // The rights and the tables

        static readonly string[] FeedTables = [MariaDbFixture.FeedLobbyTable, MariaDbFixture.FeedMapTable, MariaDbFixture.FeedStateTable];

        // A login of its own with the rights given on the tables named, for the tests of what the feed needs
        async Task<WebsiteFeedData> FeedAsAsync(MariaDbFixture db, string login, string rights, params string[] tables)
        {
            var password = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

            await db.RootExecuteAsync($"DROP USER IF EXISTS '{login}'@'%'");
            await db.RootExecuteAsync($"CREATE USER '{login}'@'%' IDENTIFIED BY '{password}'");

            foreach (var table in tables)
                await db.RootExecuteAsync($"GRANT {rights} ON {table} TO '{login}'@'%'");

            return new WebsiteFeedData(new SQLConnectionFactory(new Dictionary<DBConnectionName, string>
            {
                [DBConnectionName.Feed] = db.ConnectionAs(login, password, MariaDbFixture.SiteDatabase)
            }));
        }

        // What README and deploy/README.md tell the server's owner to grant: with these four rights on the three tables, and
        // nothing else anywhere, every write of the feed goes through
        [Fact]
        public async Task FourRightsOnTheThreeTablesAreEnough()
        {
            var db = await StartAsync();
            var feed = await FeedAsAsync(db, "feed_writer", "SELECT, INSERT, UPDATE, DELETE", FeedTables);

            try
            {
                await feed.ReplaceLobbies([Lobby(1), Lobby(2)], Noon);
                await feed.ReplaceLobbies([Lobby(2)], Later);
                await feed.ReplaceLobbies([], Later);
                await feed.SaveMaps([Map(1, Monday), Map(2, Monday.AddHours(1))], Noon);
                await feed.SaveMaps([Map(2, Monday.AddHours(1))], Later);
                await feed.MarkChecked(FeedName.Lobbies, Later);
                await feed.MarkChecked(FeedName.Maps, Later);

                Assert.Empty(await LobbiesAsync());
                Assert.Equal([1ul, 2], (await MapsAsync()).Select(row => row.file_id));
                Assert.Equal([State(FeedName.Lobbies, Later, Later, 0), State(FeedName.Maps, Later, Later, 1)], await StateAsync());
            }
            finally
            {
                await db.RootExecuteAsync("DROP USER IF EXISTS 'feed_writer'@'%'");
            }
        }

        // Without a right on the tables, MariaDB refuses the write with error 1142, which the bot's feed writer waits out
        [Fact]
        public async Task LoginWithoutTheRightsIsRefusedWithError1142()
        {
            var db = await StartAsync();
            var feed = await FeedAsAsync(db, "feed_reader", "SELECT", FeedTables);

            try
            {
                var lobbies = await Assert.ThrowsAsync<MySqlException>(() => feed.ReplaceLobbies([Lobby(1)], Noon));
                var maps = await Assert.ThrowsAsync<MySqlException>(() => feed.SaveMaps([Map(1, Monday)], Noon));
                var state = await Assert.ThrowsAsync<MySqlException>(() => feed.MarkChecked(FeedName.Maps, Noon));

                Assert.Equal(new[] { 1142, 1142, 1142 }, new[] { lobbies.Number, maps.Number, state.Number });
                Assert.Empty(await LobbiesAsync());
                Assert.Empty(await MapsAsync());
                Assert.Empty(await StateAsync());
            }
            finally
            {
                await db.RootExecuteAsync("DROP USER IF EXISTS 'feed_reader'@'%'");
            }
        }

        // A read is one transaction: when a statement after the upserts fails, here the first that touches the feed's
        // row, for want of a right on that table, the lobbies and the maps stay exactly as they were
        [Fact]
        public async Task ReadWhoseLastStatementFailsChangesNothing()
        {
            var db = await StartAsync();

            await db.Feed().ReplaceLobbies([Lobby(1), Lobby(2)], Noon);
            await db.Feed().SaveMaps([Map(1, Monday), Map(2, Monday.AddHours(1))], Noon);

            var feed = await FeedAsAsync(db, "feed_half", "SELECT, INSERT, UPDATE, DELETE", MariaDbFixture.FeedLobbyTable, MariaDbFixture.FeedMapTable);

            try
            {
                var lobbies = await Assert.ThrowsAsync<MySqlException>(() => feed.ReplaceLobbies([Lobby(2, host: "Changed"), Lobby(3)], Later));
                var maps = await Assert.ThrowsAsync<MySqlException>(() => feed.SaveMaps([Map(2, Monday.AddHours(1), title: "Changed"), Map(3, Monday.AddHours(2))], Later));

                Assert.Equal(new[] { 1142, 1142 }, new[] { lobbies.Number, maps.Number });
                Assert.Equal([Stored(Lobby(1), Noon, Noon), Stored(Lobby(2), Noon, Noon)], await LobbiesAsync());
                Assert.Equal([Stored(Map(1, Monday), Noon, Noon), Stored(Map(2, Monday.AddHours(1)), Noon, Noon)], await MapsAsync());
                Assert.Equal([State(FeedName.Lobbies, Noon, Noon, 2), State(FeedName.Maps, Noon, Noon, 2)], await StateAsync());
            }
            finally
            {
                await db.RootExecuteAsync("DROP USER IF EXISTS 'feed_half'@'%'");
            }
        }

        // The bot may be deployed before the website has made the tables. Every write then fails with MariaDB's error 1146,
        // which the bot's feed writer waits out, and nothing is made in their place.
        [Fact]
        public async Task DatabaseWithoutTheTablesRefusesWithError1146()
        {
            var db = fixture.Require();
            var (name, connection) = await db.CreateDatabaseAsync("no_feed");

            var feed = new WebsiteFeedData(new SQLConnectionFactory(new Dictionary<DBConnectionName, string> { [DBConnectionName.Feed] = connection }));

            var lobbies = await Assert.ThrowsAsync<MySqlException>(() => feed.ReplaceLobbies([Lobby(1)], Noon));
            var none = await Assert.ThrowsAsync<MySqlException>(() => feed.ReplaceLobbies([], Noon));
            var maps = await Assert.ThrowsAsync<MySqlException>(() => feed.SaveMaps([Map(1, Monday)], Noon));
            var state = await Assert.ThrowsAsync<MySqlException>(() => feed.MarkChecked(FeedName.Lobbies, Noon));

            Assert.Equal(new[] { 1146, 1146, 1146, 1146 }, new[] { lobbies.Number, none.Number, maps.Number, state.Number });
            Assert.Empty(await db.RootQueryAsync<string>("SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = @name", new { name }));
        }
    }
}
