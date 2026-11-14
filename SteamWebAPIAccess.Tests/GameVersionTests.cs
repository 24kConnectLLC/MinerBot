using SteamWebAPIAccess.Murder_Miners_Networking;

namespace SteamWebAPIAccess.Tests
{
    // The decoder, the lobby list and the notification job all read the version table
    public class GameVersionTests
    {
        // Whether the list shows its modes, and whether it has Update 39's Christmas flag
        record Known(uint Number, string? Heading, LobbyLayout Layout, bool ModesTrusted, bool ChristmasFlag = false);

        static readonly Known[] knownVersions =
        [
            new(105, "Update 41 (v105)", LobbyLayout.Latest, true),
            new(92, "Update 40 (v92)", LobbyLayout.Update40, true),
            new(87, "Update 39 (v87)", LobbyLayout.Update39, true, ChristmasFlag: true),
            new(80, "Update 35 (Xbox One Branch) (v80)", LobbyLayout.Update39, false),
            new(61, "Update 29 (v61)", LobbyLayout.Update39, false),
            new(53, "Update 19 (Xbox 360 Branch) (v53)", LobbyLayout.Update39, false),
            new(0, "2012 Alpha (v0)", LobbyLayout.Update39, false),
            new(39, "Legacy Egg Hunt Branch (v39)", LobbyLayout.Update39, true, ChristmasFlag: true),
            new(104, null, LobbyLayout.Latest, false),
            new(103, null, LobbyLayout.Update41Beta, true),
            new(102, null, LobbyLayout.Update41Beta, true),
            new(101, null, LobbyLayout.Update41Beta, true),
            new(200, null, LobbyLayout.Update41Beta, true),
            new(100, null, LobbyLayout.Update41BetaShortName, false),
            new(99, null, LobbyLayout.Update41BetaShortName, false),
            new(98, null, LobbyLayout.Update41BetaShortName, false),
            new(97, null, LobbyLayout.Update41BetaShortName, false),
            new(96, null, LobbyLayout.Update41BetaShortName, false),
            new(95, null, LobbyLayout.Update41BetaShortName, false),
            new(94, null, LobbyLayout.Update41BetaShortName, false),
            new(93, null, LobbyLayout.Update41BetaShortName, false),
            new(91, null, LobbyLayout.Update40, false),
            new(90, null, LobbyLayout.Update40, false),
            new(89, null, LobbyLayout.Update39, false),
            new(88, null, LobbyLayout.Update39, false)
        ];

        public static TheoryData<uint> KnownVersions()
        {
            var data = new TheoryData<uint>();

            foreach (var version in knownVersions)
                data.Add(version.Number);

            return data;
        }

        [Theory]
        [MemberData(nameof(KnownVersions))]
        public void KnowsEachVersion(uint number)
        {
            var expected = knownVersions.Single(version => version.Number == number);
            var version = GameVersions.Find(number);

            Assert.NotNull(version);
            Assert.True(GameVersions.IsKnown(number));
            Assert.Equal(expected.Heading, version.Heading);
            Assert.Equal(expected.Layout, GameVersions.LayoutOf(number));
            Assert.Equal(expected.ModesTrusted, GameVersions.ModesTrusted(number));
            Assert.Equal(expected.ChristmasFlag, GameVersions.HasChristmasFlag(number));
            Assert.True(version.Notify);
            Assert.True(GameVersions.Notifies(number));
        }

        [Fact]
        public void KnowsNoOtherVersions()
        {
            Assert.Equal(knownVersions.Select(version => version.Number).Order(), GameVersions.All.Select(version => version.Number).Order());
        }

        [Fact]
        public void BranchesKeepTheOrderOfTheLobbyList()
        {
            Assert.Equal(
                ["Update 41 (v105)", "Update 40 (v92)", "Update 39 (v87)", "Update 35 (Xbox One Branch) (v80)", "Update 29 (v61)",
                 "Update 19 (Xbox 360 Branch) (v53)", "2012 Alpha (v0)", "Legacy Egg Hunt Branch (v39)"],
                GameVersions.Branches.Select(branch => branch.Heading));
        }

        // The table must not move what the decoder reads, map names included, for any version, known or not
        [Fact]
        public void EveryVersionKeepsTheLayoutOfItsRange()
        {
            uint[] versions = [.. Enumerable.Range(0, 301).Select(version => (uint)version), NetPropertiesFunctions.INVALID_GAME_VERSION, uint.MaxValue];

            Assert.All(versions, version => Assert.Equal(GameVersions.RangeLayout(version), GameVersions.LayoutOf(version)));
        }

        // A lobby on one of these is listed under Other versions with Modes Unknown, and announced
        [Theory]
        [InlineData(1)]
        [InlineData(38)]
        [InlineData(40)]
        [InlineData(81)]
        [InlineData(86)]
        [InlineData(106)]
        [InlineData(199)]
        [InlineData(201)]
        [InlineData(uint.MaxValue)]
        public void VersionThatIsNotInTheTableIsUnseen(uint number)
        {
            Assert.Null(GameVersions.Find(number));
            Assert.False(GameVersions.IsKnown(number));
            Assert.False(GameVersions.ModesTrusted(number));
            Assert.False(GameVersions.HasChristmasFlag(number));
            Assert.True(GameVersions.Notifies(number));
        }

        // Not newer than 105, it was a typo on an Update 41 branch
        [Fact]
        public void Version200HasTheOlderUpdate41Layout()
        {
            var version = GameVersions.Find(200);

            Assert.NotNull(version);
            Assert.Null(version.Heading);
            Assert.Equal(LobbyLayout.Update41Beta, version.Layout);
            Assert.True(version.ModesTrusted);
            Assert.False(NetPropertiesFunctions.UsesLatestLayout(200));
        }

        [Theory]
        [InlineData(0, LobbyLayout.Update39, true, false, "2012 Alpha (v0)")]
        [InlineData(1, LobbyLayout.Update39, false, false, null)]
        [InlineData(38, LobbyLayout.Update39, false, false, null)]
        [InlineData(39, LobbyLayout.Update39, true, true, "Legacy Egg Hunt Branch (v39)")]
        [InlineData(40, LobbyLayout.Update39, false, false, null)]
        [InlineData(52, LobbyLayout.Update39, false, false, null)]
        [InlineData(53, LobbyLayout.Update39, true, false, "Update 19 (Xbox 360 Branch) (v53)")]
        [InlineData(54, LobbyLayout.Update39, false, false, null)]
        [InlineData(60, LobbyLayout.Update39, false, false, null)]
        [InlineData(61, LobbyLayout.Update39, true, false, "Update 29 (v61)")]
        [InlineData(62, LobbyLayout.Update39, false, false, null)]
        [InlineData(79, LobbyLayout.Update39, false, false, null)]
        [InlineData(80, LobbyLayout.Update39, true, false, "Update 35 (Xbox One Branch) (v80)")]
        [InlineData(81, LobbyLayout.Update39, false, false, null)]
        [InlineData(86, LobbyLayout.Update39, false, false, null)]
        [InlineData(87, LobbyLayout.Update39, true, true, "Update 39 (v87)")]
        [InlineData(88, LobbyLayout.Update39, true, false, null)]
        [InlineData(89, LobbyLayout.Update39, true, false, null)]
        [InlineData(90, LobbyLayout.Update40, true, false, null)]
        [InlineData(91, LobbyLayout.Update40, true, false, null)]
        [InlineData(92, LobbyLayout.Update40, true, true, "Update 40 (v92)")]
        [InlineData(93, LobbyLayout.Update41BetaShortName, true, false, null)]
        [InlineData(100, LobbyLayout.Update41BetaShortName, true, false, null)]
        [InlineData(101, LobbyLayout.Update41Beta, true, true, null)]
        [InlineData(103, LobbyLayout.Update41Beta, true, true, null)]
        [InlineData(104, LobbyLayout.Latest, true, false, null)]
        [InlineData(105, LobbyLayout.Latest, true, true, "Update 41 (v105)")]
        [InlineData(106, LobbyLayout.Latest, false, false, null)]
        [InlineData(199, LobbyLayout.Latest, false, false, null)]
        [InlineData(200, LobbyLayout.Update41Beta, true, true, null)]
        [InlineData(201, LobbyLayout.Latest, false, false, null)]
        public void KnowsBothSidesOfEveryEdge(uint number, LobbyLayout layout, bool known, bool modesTrusted, string? heading)
        {
            Assert.Equal(layout, GameVersions.LayoutOf(number));
            Assert.Equal(known, GameVersions.IsKnown(number));
            Assert.Equal(modesTrusted, GameVersions.ModesTrusted(number));
            Assert.Equal(heading, GameVersions.Find(number)?.Heading);
        }
    }
}
