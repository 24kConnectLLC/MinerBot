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
