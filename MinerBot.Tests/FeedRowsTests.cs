using DataAccessLibrary.Feed;
using DataAccessLibrary.Feed.Models;
using MinerBot_2._0.Feed;
using SteamWebAPIAccess;
using SteamWebAPIAccess.Models;

namespace MinerBot.Tests
{
    // What the bot read from Steam, made into the rows of the website feed: the lobbies as the bot's own list shows them,
    // without the words it puts in for what isn't known, and the maps as plain text, never escaped or cut for Discord
    public class FeedRowsTests
    {
        const ulong Owner = 76561197960265729;
        const string Emoji = "\U0001F600";

        static LobbyInfo Lobby(uint version = GameVersions.Current) => new()
        {
            LobbyId = 109775240000000001,
            OwnerID = Owner,
            HostName = "Host",
            MapName = "Test Map",
            Modes = "Deathmatch",
            CurrentPlayers = 3,
            MaxPlayers = 8,
            VersionNumber = version,
            JoinURL = "https://example.com/join"
        };

        // The lobbies

        [Fact]
        public void LobbyOnTheCurrentVersionKeepsEverythingTheListShows()
        {
            Assert.Equal(new FeedLobby
            {
                lobby_id = 109775240000000001,
                owner_id = Owner,
                host_name = "Host",
                host_kind = FeedHostKind.Player,
                map_name = "Test Map",
                modes = "Deathmatch",
                version_number = 105,
                version_label = "Update 41",
                is_current = true,
                players = 3,
                max_players = 8
            }, FeedRows.Lobby(Lobby()));
        }

        // The list names the game's own hosts in place of the host, and the feed keeps that name with the kind beside it. A
        // player who took the name is still a player.
        [Theory]
        [InlineData(LobbyHostKind.Player, "Host", FeedHostKind.Player)]
        [InlineData(LobbyHostKind.Player, "Gamenight", FeedHostKind.Player)]
        [InlineData(LobbyHostKind.Gamenight, "Gamenight", FeedHostKind.Gamenight)]
        [InlineData(LobbyHostKind.Official, "Official", FeedHostKind.Official)]
        public void HostKindIsCarriedBesideTheNameTheListShows(LobbyHostKind kind, string name, string expected)
        {
            var lobby = Lobby();
            lobby.HostKind = kind;
            lobby.HostName = name;

            var row = FeedRows.Lobby(lobby);

            Assert.Equal((name, expected), (row.host_name, row.host_kind));
            Assert.True(FeedHostKind.IsKnown(row.host_kind));
        }

        // The branch's name only when the version is a branch's own, and current only on the version the bot counts as the
        // game's. Modes are written only where the list would show them: for a version whose layout isn't known exactly, and
        // one the bot doesn't know at all, they'd be guesses.
        [Theory]
        [InlineData(105u, "Update 41", true, "Deathmatch")]
        [InlineData(92u, "Update 40", false, "Deathmatch")]
        [InlineData(87u, "Update 39", false, "Deathmatch")]
        [InlineData(39u, "Legacy Egg Hunt Branch", false, "Deathmatch")]
        [InlineData(80u, "Update 35 (Xbox One Branch)", false, "")]
        [InlineData(0u, "2012 Alpha", false, "")]
        [InlineData(103u, "", false, "Deathmatch")]
        [InlineData(104u, "", false, "")]
        [InlineData(106u, "", false, "")]
        public void VersionGivesTheLabelAndWhetherTheModesAreKnown(uint version, string label, bool current, string modes)
        {
            var row = FeedRows.Lobby(Lobby(version));

            Assert.Equal((version, label, current, modes), (row.version_number, row.version_label, row.is_current, row.modes));
        }

        // Where the list says Host Unknown, Map Unknown and Modes Unknown, the feed is empty, for the page to word itself
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void WhatIsNotKnownIsEmpty(string? missing)
        {
            var lobby = Lobby();
            lobby.HostName = missing!;
            lobby.MapName = missing!;
            lobby.Modes = missing!;

            var row = FeedRows.Lobby(lobby);

            Assert.Equal(("", "", ""), (row.host_name, row.map_name, row.modes));
        }

        // A host's name is the host's own text. The list takes out what would break its row, and a page is no safer with a
        // control that reorders the text after it, so the feed takes out the same, and keeps every script and emoji.
        [Fact]
        public void HostNameIsCleanedAsTheListCleansIt()
        {
            var lobby = Lobby();
            lobby.HostName = "Ho‮st\u0007 " + Emoji;

            Assert.Equal("Host " + Emoji, FeedRows.Lobby(lobby).host_name);

            lobby.HostName = "‮ \u0007";

            Assert.Equal("", FeedRows.Lobby(lobby).host_name);
        }

        // The columns are unsigned, and the host writes the lobby's size
        [Theory]
        [InlineData(-1, 0)]
        [InlineData(0, 0)]
        [InlineData(65535, 65535)]
        [InlineData(70000, 65535)]
        [InlineData(int.MaxValue, 65535)]
        public void PlayerCountsStayInsideTheirColumns(int players, int expected)
        {
            var lobby = Lobby();
            lobby.CurrentPlayers = players;
            lobby.MaxPlayers = players;

            var row = FeedRows.Lobby(lobby);

            Assert.Equal(((ushort)expected, (ushort)expected), (row.players, row.max_players));
        }

        // The maps

        [Fact]
        public void MapKeepsWhatSteamSaysAboutIt()
        {
            var map = FakeGetData.CreateItem(7);
            map.Title = "  A Map  ";
            map.Description = "About it";
            map.PreviewUrl = new Uri("https://example.com/preview.jpg");
            map.Creator = Owner;
            map.Tags = ["Deathmatch", "Small"];
            map.FileSize = 5000000000;
            map.Subscriptions = 10;
            map.LifetimeSubscriptions = 20;
            map.Favorited = 3;
            map.LifetimeFavorited = 4;
            map.Views = 500;

            // As Steam's reader gives them: UTC, without saying so
            map.TimeCreated = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Unspecified);
            map.TimeUpdated = new DateTime(2026, 11, 20, 18, 30, 0, DateTimeKind.Unspecified);

            var row = FeedRows.Map(map);

            Assert.Equal(new FeedMap
            {
                file_id = 7,
                title = "A Map",
                description = "About it",
                preview_url = "https://example.com/preview.jpg",
                creator_id = Owner,
                tags = "Deathmatch,Small",
                file_size = 5000000000,
                subscriptions = 10,
                lifetime_subscriptions = 20,
                favorited = 3,
                lifetime_favorited = 4,
                views = 500,
                time_created = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc),
                time_updated = new DateTime(2026, 11, 20, 18, 30, 0, DateTimeKind.Utc)
            }, row);

            Assert.Equal((DateTimeKind.Utc, DateTimeKind.Utc), (row.time_created.Kind, row.time_updated.Kind));
        }

        // A map with nothing but its ID and times, and numbers past what the columns hold
        [Fact]
        public void MapWithoutTextHasNoneAndBigNumbersStopAtTheirColumns()
        {
            var map = FakeGetData.CreateItem(7);
            map.Title = null!;
            map.Description = null!;
            map.PreviewUrl = null!;
            map.Tags = null!;
            map.Subscriptions = ulong.MaxValue;
            map.Views = (ulong)uint.MaxValue + 1;

            var row = FeedRows.Map(map);

            Assert.Equal(("", (string?)null, "", "", 0ul), (row.title, row.description, row.preview_url, row.tags, row.creator_id));
            Assert.Equal((uint.MaxValue, uint.MaxValue), (row.subscriptions, row.views));
        }

        // The BBCode goes as it does for the map embeds, but nothing is escaped: the page isn't Discord, and escapes its own
        [Theory]
        [InlineData("[h1]Title[/h1]\n[b]Bold[/b] and [url=https://example.com/x]a link[/url]", "Title\nBold and a link")]
        [InlineData("*stars* _under_ `tick` <@1> # heading [WIP] \\ slash", "*stars* _under_ `tick` <@1> # heading [WIP] \\ slash")]
        [InlineData("[list]\n[*]One\n[*]Two\n[/list]", "• One\n• Two")]
        [InlineData("Line one\r\nLine two", "Line one\nLine two")]
        [InlineData("  padded  ", "padded")]
        public void DescriptionIsPlainTextWithoutBBCodeAndWithoutDiscordEscapes(string description, string expected)
        {
            Assert.Equal(expected, FeedRows.Description(description));
        }

        // Like a missing one, rather than an empty text
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   \n ")]
        [InlineData("[img]https://example.com/a.png[/img]")]
        [InlineData("[b] [/b]")]
        public void DescriptionWithNothingLeftIsNone(string? description)
        {
            Assert.Null(FeedRows.Description(description));
        }

        // At most 2,000 characters, as MariaDB and the website count them, where an emoji is one
        [Fact]
        public void LongDescriptionIsCutAt2000Characters()
        {
            Assert.Equal(new string('a', 2000), FeedRows.Description(new string('a', 2001)));
            Assert.Equal(new string('a', 2000), FeedRows.Description(new string('a', 9000)));
            Assert.Equal(new string('a', 2000), FeedRows.Description(new string('a', 2000)));
            Assert.Equal(2000, WebsiteFeedData.DescriptionLimit);
        }

        // An emoji is two chars in .NET. One that falls on the cut is kept whole or left out, never halved, and the space
        // the cut leaves at the end goes.
        [Fact]
        public void CutNeverSplitsACharacterOutsideTheBasicPlane()
        {
            var last = FeedRows.Description(new string('a', 1999) + Emoji + "bbb")!;
            var past = FeedRows.Description(new string('a', 2000) + Emoji + "bbb")!;
            var all = FeedRows.Description(string.Concat(Enumerable.Repeat(Emoji, 2500)))!;
            var spaced = FeedRows.Description(new string('a', 1990) + new string(' ', 10) + "bbb")!;

            Assert.Equal(new string('a', 1999) + Emoji, last);
            Assert.Equal(new string('a', 2000), past);
            Assert.Equal(string.Concat(Enumerable.Repeat(Emoji, 2000)), all);
            Assert.Equal(new string('a', 1990), spaced);
            Assert.All(new[] { last, past, all }, text => Assert.False(char.IsHighSurrogate(text[^1])));
        }

        // Commas between the tags and no spaces around them, so the page can split on the comma
        [Fact]
        public void TagsAreJoinedByCommas()
        {
            Assert.Equal("Deathmatch,Capture the Flag,Small", FeedRows.Tags(["Deathmatch", " Capture the Flag ", "", "   ", "Small"]));
            Assert.Equal("", FeedRows.Tags([]));
            Assert.Equal("", FeedRows.Tags(null));
        }

        // A tag with a comma in it would read as two on the page, so it's left out
        [Fact]
        public void TagThatHoldsACommaIsLeftOut()
        {
            Assert.Equal("Big,Small", FeedRows.Tags(["Big", "Red, Blue", "Small"]));
        }

        // The column holds 500 characters. The tags that fit are kept whole, in Steam's order.
        [Fact]
        public void TagsThatDoNotFitTheColumnAreLeftOutWhole()
        {
            var tags = Enumerable.Range(0, 60).Select(index => $"Tag number {index:00}").ToList();

            var joined = FeedRows.Tags(tags);

            // 35 tags of 13 characters with their commas are 489 characters, and one more would be 503
            Assert.Equal(tags.Take(35), joined.Split(','));
            Assert.Equal(489, joined.Length);
            Assert.True(joined.Length <= WebsiteFeedData.TagsLimit);
        }

        // A link cut short would be another link
        [Fact]
        public void PictureLinkThatDoesNotFitIsLeftOut()
        {
            var map = FakeGetData.CreateItem(7);
            map.PreviewUrl = new Uri("https://example.com/" + new string('a', 500));

            Assert.Equal("", FeedRows.Map(map).preview_url);

            map.PreviewUrl = new Uri("https://example.com/" + new string('a', 480));

            Assert.Equal(500, FeedRows.Map(map).preview_url.Length);
        }

        // Steam's reader leaves a missing time as year 1. The feed deletes by the updates of a read's last maps, so a map
        // without its times is left out, and the rest keep Steam's order.
        [Fact]
        public void MapsWithoutTheirTimesAreLeftOutOfARead()
        {
            var noUpdate = FakeGetData.CreateItem(2);
            noUpdate.TimeUpdated = default;

            var noCreation = FakeGetData.CreateItem(4);
            noCreation.TimeCreated = default;

            var rows = FeedRows.Maps([FakeGetData.CreateItem(5), noCreation, FakeGetData.CreateItem(3), noUpdate, FakeGetData.CreateItem(1)]);

            Assert.Equal([5ul, 3, 1], rows.Select(row => row.file_id));
        }
    }
}
