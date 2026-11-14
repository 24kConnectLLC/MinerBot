namespace SteamWebAPIAccess.Tests
{
    public class LobbyNameTests
    {
        [Theory]
        [InlineData(null, "")]
        [InlineData("", "")]
        [InlineData("Bob", "Bob")]
        [InlineData("  Bob  ", "Bob")]
        [InlineData("Bob\n\n\tSmith", "Bob Smith")]
        [InlineData("Bob\u2028Smith", "Bob Smith")]
        public void CleansHostName(string? hostName, string expected)
        {
            Assert.Equal(expected, GetData.CleanHostName(hostName));
        }

        [Fact]
        public void CapsHostNameAtTheSteamNameLimit()
        {
            Assert.Equal(32, GetData.CleanHostName(new string('a', 4000)).Length);
        }

        // This emoji and this styled letter are each two UTF-16 units, and half of one isn't a character. An emoji made of
        // several, such as a flag, can still be cut between them.
        [Fact]
        public void CapNeverSplitsASurrogatePair()
        {
            var smiley = char.ConvertFromUtf32(0x1F600);
            var scriptM = char.ConvertFromUtf32(0x1D4DC);

            Assert.Equal(new string('a', 31), GetData.CleanHostName(new string('a', 31) + smiley));
            Assert.Equal(new string('a', 30) + scriptM, GetData.CleanHostName(new string('a', 30) + scriptM + scriptM));
        }

        [Theory]
        [InlineData("Bob's_Map.v2!", "Bob's_Map.v2!")]
        [InlineData("a`b", "a'b")]
        [InlineData("  Skyline  ", "Skyline")]
        public void CleansMapName(string mapName, string expected)
        {
            Assert.Equal(expected, GetData.CleanMapName(mapName));
        }
    }
}
