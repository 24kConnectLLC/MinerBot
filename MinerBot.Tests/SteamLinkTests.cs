using MinerBot_2._0;

namespace MinerBot.Tests
{
    // What /maps lookup and /more steam_id accept. Anything refused here never reaches Steam.
    public class SteamLinkTests
    {
        const ulong MapId = 1234567890;
        const ulong SteamId = 76561197960265729;

        [Theory]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails?id=1234567890")]
        [InlineData("https://steamcommunity.com/workshop/filedetails/?id=1234567890")]
        [InlineData("http://steamcommunity.com/sharedfiles/filedetails/?id=1234567890")]
        [InlineData("https://www.steamcommunity.com/sharedfiles/filedetails/?id=1234567890")]
        [InlineData("steamcommunity.com/sharedfiles/filedetails/?id=1234567890")]
        [InlineData("www.steamcommunity.com/workshop/filedetails/?id=1234567890")]
        [InlineData("HTTPS://STEAMCOMMUNITY.COM/SHAREDFILES/FILEDETAILS/?ID=1234567890")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?searchtext=&id=1234567890")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890#comments")]
        [InlineData("<https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890>")]
        [InlineData("  https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890  ")]
        [InlineData("1234567890")]
        [InlineData(" 1234567890 ")]
        [InlineData("<1234567890>")]
        public void WorkshopLinkOrIdIsRead(string input)
        {
            Assert.True(SteamLinks.TryParseWorkshopId(input, out var id));
            Assert.Equal(MapId, id);
        }

        [Fact]
        public void BiggestIdIsRead()
        {
            Assert.True(SteamLinks.TryParseWorkshopId("18446744073709551615", out var id));
            Assert.Equal(ulong.MaxValue, id);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not a link")]
        [InlineData("0")]
        [InlineData("00")]
        [InlineData("18446744073709551616")]
        [InlineData("99999999999999999999999")]
        [InlineData("-1")]
        [InlineData("+1234567890")]
        [InlineData("12 34")]
        [InlineData("<<1234567890>>")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=abc")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=-1")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=0")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=+1234567890")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=99999999999999999999999")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=1&id=2")]
        [InlineData("https://steamcommunity.com.example.com/sharedfiles/filedetails/?id=1234567890")]
        [InlineData("https://example.com/steamcommunity.com/sharedfiles/filedetails/?id=1234567890")]
        [InlineData("https://notsteamcommunity.com/sharedfiles/filedetails/?id=1234567890")]
        [InlineData("https://example.com/sharedfiles/filedetails/?id=1234567890")]
        [InlineData("ftp://steamcommunity.com/sharedfiles/filedetails/?id=1234567890")]
        [InlineData("https://steamcommunity.com/id/someone/?id=1234567890")]
        [InlineData("https://steamcommunity.com/sharedfiles/filedetails/more/?id=1234567890")]
        [InlineData("https://steamcommunity.com/?id=1234567890")]
        public void OtherWorkshopInputIsRefused(string? input)
        {
            Assert.False(SteamLinks.TryParseWorkshopId(input, out _));
        }

        // .../id/foo/games was once read as foogames, which can be someone else's name
        [Theory]
        [InlineData("https://steamcommunity.com/id/foo", "foo")]
        [InlineData("https://steamcommunity.com/id/foo/", "foo")]
        [InlineData("https://steamcommunity.com/id/foo/games", "foo")]
        [InlineData("https://steamcommunity.com/id/foo/games/?tab=all", "foo")]
        [InlineData("https://steamcommunity.com/id/foo?x=1#frag", "foo")]
        [InlineData("http://www.steamcommunity.com/id/foo", "foo")]
        [InlineData("steamcommunity.com/id/foo", "foo")]
        [InlineData("<https://steamcommunity.com/id/foo>", "foo")]
        [InlineData("HTTPS://STEAMCOMMUNITY.COM/ID/Foo", "Foo")]
        [InlineData("https://steamcommunity.com/id/%66oo", "foo")]
        [InlineData("https://steamcommunity.com/id/a_b-C9", "a_b-C9")]
        [InlineData("https://steamcommunity.com/id/1234567890/", "1234567890")]
        [InlineData("https://steamcommunity.com/id/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
        public void ProfileLinkByNameGivesTheName(string input, string expected)
        {
            Assert.True(SteamLinks.TryParseProfile(input, out var name, out var steamId));
            Assert.Equal(expected, name);
            Assert.Equal(0UL, steamId);
        }

        [Theory]
        [InlineData("https://steamcommunity.com/profiles/76561197960265729")]
        [InlineData("https://steamcommunity.com/profiles/76561197960265729/")]
        [InlineData("https://steamcommunity.com/profiles/76561197960265729/games")]
        [InlineData("http://www.steamcommunity.com/profiles/76561197960265729")]
        [InlineData("steamcommunity.com/profiles/76561197960265729")]
        [InlineData("<https://steamcommunity.com/profiles/76561197960265729>")]
        [InlineData("HTTPS://STEAMCOMMUNITY.COM/PROFILES/76561197960265729")]
        [InlineData("76561197960265729")]
        [InlineData(" 76561197960265729 ")]
        public void ProfileLinkByIdOrABareIdGivesTheId(string input)
        {
            Assert.True(SteamLinks.TryParseProfile(input, out var name, out var steamId));
            Assert.Null(name);
            Assert.Equal(SteamId, steamId);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not a link")]
        [InlineData("https://steamcommunity.com/id/")]
        [InlineData("https://steamcommunity.com/id//games")]
        [InlineData("https://steamcommunity.com/id/foo%2Fbar")]
        [InlineData("https://steamcommunity.com/id/foo bar")]
        [InlineData("https://steamcommunity.com/id/foo.bar")]
        [InlineData("https://steamcommunity.com/id/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
        [InlineData("https://steamcommunity.com/groups/foo")]
        [InlineData("https://steamcommunity.com/foo")]
        [InlineData("https://steamcommunity.com.example.com/id/foo")]
        [InlineData("https://example.com/steamcommunity.com/id/foo")]
        [InlineData("https://notsteamcommunity.com/id/foo")]
        [InlineData("ftp://steamcommunity.com/id/foo")]
        [InlineData("https://steamcommunity.com.example.com/profiles/76561197960265729")]
        [InlineData("https://steamcommunity.com/profiles/")]
        [InlineData("https://steamcommunity.com/profiles/abc")]
        [InlineData("https://steamcommunity.com/profiles/0")]
        [InlineData("https://steamcommunity.com/profiles/1234567890")]
        [InlineData("https://steamcommunity.com/profiles/99999999999999999999999")]
        [InlineData("https://steamcommunity.com/profiles/+76561197960265729")]
        [InlineData("0")]
        [InlineData("42")]
        [InlineData("18446744073709551615")]
        [InlineData("18446744073709551616")]
        [InlineData("76561197960265729x")]
        public void OtherProfileInputIsRefused(string? input)
        {
            Assert.False(SteamLinks.TryParseProfile(input, out var name, out var steamId));
            Assert.Null(name);
            Assert.Equal(0UL, steamId);
        }

        // Only a valid individual account in the public universe has a profile to show
        [Theory]
        [InlineData(76561197960265728UL)]
        [InlineData(76561215140134913UL)]
        [InlineData(103582791429521412UL)]
        [InlineData(112589994979229697UL)]
        [InlineData(117093594606600193UL)]
        [InlineData(148618791998193665UL)]
        [InlineData(364791574111977473UL)]
        public void IdThatIsNotAnIndividualPublicAccountIsRefused(ulong steamId)
        {
            Assert.False(SteamLinks.TryParseProfile($"{steamId}", out _, out _));
            Assert.False(SteamLinks.TryParseProfile($"https://steamcommunity.com/profiles/{steamId}", out _, out _));
        }

        // A regex that ignores case, and char.IsLetterOrDigit, both take the Kelvin sign for a letter
        [Fact]
        public void NameWithALetterOutsideAsciiIsRefused()
        {
            var kelvin = char.ConvertFromUtf32(0x212A);

            Assert.False(SteamLinks.TryParseProfile($"https://steamcommunity.com/id/{kelvin}ey", out _, out _));
            Assert.False(SteamLinks.TryParseProfile($"https://steamcommunity.com/id/{Uri.EscapeDataString(kelvin)}ey", out _, out _));
        }
    }
}
