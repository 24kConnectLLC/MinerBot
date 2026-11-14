using System.Text.Json;
using SteamWebAPIAccess.Murder_Miners_Networking;

namespace SteamWebAPIAccess.Tests
{
    // LobbyVectors.json holds lobby metadata generated from the game's source at six builds,
    // not from the enums in this repo, plus one real capture and one lobby with no version.
    // A null WantModes only checks the map name. The real capture is from a version 90 beta
    // whose bools moved before the release build, so the bot can't read its modes exactly.
    // When a game update changes the layout, add its lobbies here before changing the enums.
    public class LobbyDecodingTests
    {
        public record LobbyVector(string Build, string Label, Dictionary<string, string> Meta, string? WantModes, string WantMap);

        static readonly List<LobbyVector> vectors = JsonSerializer.Deserialize<List<LobbyVector>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "LobbyVectors.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        static readonly string[] latestMapNameSlots = { "prop_5", "prop_6", "prop_18", "prop_19", "prop_20", "prop_21", "prop_22", "prop_23" };

        public static TheoryData<string, string> AllVectors()
        {
            var data = new TheoryData<string, string>();

            foreach (var vector in vectors)
                data.Add(vector.Build, vector.Label);

            return data;
        }

        static NetworkSessionProperties read(Dictionary<string, string> meta)
        {
            var properties = NetworkSessionProperties.CreateReadOnly();
            properties.ReadProperties((IReadOnlyDictionary<string, string>)meta);

            return properties;
        }

        static (string modes, string map) decode(Dictionary<string, string> meta)
        {
            var properties = read(meta);

            ulong bools = NetPropertiesFunctions.GetBoolsFromUints(
                properties.GetIntProperty(PropertyUsage.NetSettingsBools),
                properties.GetIntProperty(PropertyUsage.NetSettingsBoolsExtended));

            string modes = GameModesMenuEntry.GetGameModeText(bools, properties.GetBonusBools(), version: properties.GetVersion()).Trim();

            return (modes, properties.GetMapName().Trim());
        }

        static LobbyVector findVector(string build, string label)
        {
            return vectors.Single(vector => vector.Build == build && vector.Label == label);
        }

        static Dictionary<string, string> withProperty(LobbyVector vector, string key, string value)
        {
            return new Dictionary<string, string>(vector.Meta) { [key] = value };
        }

        static Dictionary<string, string> withAllMapNameSlots(string value)
        {
            var meta = new Dictionary<string, string>(findVector("U41 beta29 v105", "default lobby").Meta);

            foreach (var slot in latestMapNameSlots)
                meta[slot] = value;

            return meta;
        }

        [Theory]
        [MemberData(nameof(AllVectors))]
        public void DecodesLobby(string build, string label)
        {
            var vector = findVector(build, label);

            var (modes, map) = decode(vector.Meta);

            if (vector.WantModes != null)
                Assert.Equal(vector.WantModes, modes);

            Assert.Equal(vector.WantMap, map);
        }

        // No vectors exist for these versions, they sit on either side of a version check
        [Theory]
        [InlineData("U39 v87", "89")]
        [InlineData("U40.1 v92", "90")]
        [InlineData("U41 beta20 v102", "103")]
        [InlineData("U41 beta29 v105", "104")]
        public void DecodesEveryLobbyOnVersionBoundary(string build, string version)
        {
            foreach (var vector in vectors.Where(vector => vector.Build == build))
            {
                var (modes, map) = decode(withProperty(vector, "prop_7", version));

                Assert.Equal(vector.WantModes, modes);
                Assert.Equal(vector.WantMap, map);
            }
        }

        // Version 93 is the first with the older Update 41 bools, but it still has the short map name
        [Fact]
        public void ReadsUpdate41BoolsFromVersion93()
        {
            foreach (var vector in vectors.Where(vector => vector.Build == "U41 beta18 v101"))
                Assert.Equal(vector.WantModes, decode(withProperty(vector, "prop_7", "93")).modes);
        }

        // Version 100 is the last with the short map name, but it already has the Update 41 bools
        [Fact]
        public void ReadsShortMapNameUpToVersion100()
        {
            var vector = findVector("U40.1 v92", "default lobby");

            Assert.Equal(vector.WantMap, decode(withProperty(vector, "prop_7", "100")).map);
        }

        [Theory]
        [InlineData(87, false)]
        [InlineData(102, false)]
        [InlineData(103, false)]
        [InlineData(104, true)]
        [InlineData(105, true)]
        [InlineData(200, false)]
        public void KnowsWhichVersionsUseTheLatestLayout(uint version, bool expected)
        {
            Assert.Equal(expected, NetPropertiesFunctions.UsesLatestLayout(version));
        }

        // Older builds keep the player count where BTS is now
        [Fact]
        public void OlderLobbyPlayerCountIsNotAGameMode()
        {
            var vector = findVector("U41 beta20 v102", "default lobby");

            Assert.Equal(vector.WantModes, decode(withProperty(vector, "prop_13", "1")).modes);
        }

        // The map slots are filled so that treating this lobby as an old build would change its name
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("-1")]
        public void LobbyWithoutAReadableVersionIsInvalid(string? version)
        {
            var meta = new Dictionary<string, string> { ["prop_5"] = "895630364", ["prop_6"] = "1421871492" };

            if (version != null)
                meta["prop_7"] = version;

            var properties = read(meta);

            Assert.Equal(NetPropertiesFunctions.INVALID_GAME_VERSION, properties.GetVersion());
            Assert.Equal("NONAME", properties.GetMapName().Trim());
        }

        // Lobby metadata comes from the host, so these values don't have to be real
        [Fact]
        public void ForgedMapNameBytesDecodeToQuestionMarks()
        {
            var meta = withAllMapNameSlots(uint.MaxValue.ToString());

            Assert.Equal(new string('?', NetPropertiesFunctions.MAP_NAME_LEN), decode(meta).map);
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("-1")]
        [InlineData("99999999999")]
        public void UnreadableMapNameSlotsDecodeToNoName(string value)
        {
            Assert.Equal("NONAME", decode(withAllMapNameSlots(value)).map);
        }

        // The Christmas Event is Update 39's official playlist, which also sets Infection. No vector has its flag.

        static Dictionary<string, string> withChristmasFlag(Dictionary<string, string> meta, string boolsProperty = "prop_12")
        {
            // In the second of the two uints that hold the bools
            int bit = (int)NetSettingType1.boolIsChristmasPlaylist - 1 - 32;

            return new Dictionary<string, string>(meta) { [boolsProperty] = (uint.Parse(meta[boolsProperty]) | 1u << bit).ToString() };
        }

        [Theory]
        [InlineData("87")]
        [InlineData("39")]
        public void ChristmasEventIsNamedInPlaceOfItsInfection(string version)
        {
            var vector = findVector("U39 v87", "toggle boolIsInfection");

            Assert.Equal("Christmas Event", decode(withChristmasFlag(withProperty(vector, "prop_7", version))).modes);
        }

        [Fact]
        public void ChristmasEventKeepsItsTeams()
        {
            var vector = findVector("U39 v87", "boolIsTeams+boolIsInfection");

            Assert.Equal("Team Christmas Event", decode(withChristmasFlag(vector.Meta)).modes);
        }

        // The event is the mode, so it isn't also a Murder Match
        [Fact]
        public void ChristmasFlagWithoutInfectionIsStillTheChristmasEvent()
        {
            var vector = findVector("U39 v87", "default lobby");

            Assert.Equal("Christmas Event", decode(withChristmasFlag(vector.Meta)).modes);
        }

        // 88 and 89 decode with the same layout, but the flag is only known to be right for 87 and 39
        [Theory]
        [InlineData("88")]
        [InlineData("89")]
        public void ChristmasFlagIsOnlyReadFor87And39(string version)
        {
            var vector = findVector("U39 v87", "toggle boolIsInfection");

            Assert.Equal("Infection", decode(withChristmasFlag(withProperty(vector, "prop_7", version))).modes);
        }

        // Later builds use that bit for other settings, the latest one for its time limit
        [Theory]
        [InlineData("U40.1 v92", "prop_12")]
        [InlineData("U41 beta18 v101", "prop_12")]
        [InlineData("U41 typo v200", "prop_12")]
        [InlineData("U41 beta29 v105", "prop_15")]
        public void ChristmasFlagIsNotReadForLaterBuilds(string build, string boolsProperty)
        {
            foreach (var vector in vectors.Where(vector => vector.Build == build))
                Assert.DoesNotContain("Christmas", decode(withChristmasFlag(vector.Meta, boolsProperty)).modes);
        }

        // The bools are stored in 2 uints, a 65th bool would silently overwrite the first one
        [Fact]
        public void BoolsFitInTheLobbyMetadata()
        {
            int boolCount = (int)NetSettingType.END_BOOLS - (int)NetSettingType.BEGIN_BOOLS - 1;

            Assert.True(boolCount <= 64, $"NetSettingType has {boolCount} bools, the lobby metadata only has room for 64");
        }
    }
}
