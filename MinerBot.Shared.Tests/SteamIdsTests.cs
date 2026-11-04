using MinerBot.Shared.Steam;

namespace MinerBot.Shared.Tests
{
    // Made-up accounts. Account 12345 is 76561197960278073 as a SteamID64 and STEAM_0:1:6172 in the old format.
    public class SteamIdsTests
    {
        static uint AccountNumber(string? input)
        {
            var result = SteamIds.Parse(input);

            Assert.Equal(SteamIdKind.AccountNumber, result.Kind);
            Assert.Null(result.Error);
            Assert.Null(result.VanityName);
            return result.AccountNumber;
        }

        static string Invalid(string? input)
        {
            var result = SteamIds.Parse(input);

            Assert.Equal(SteamIdKind.Invalid, result.Kind);
            Assert.Equal(0u, result.AccountNumber);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
            return result.Error!;
        }

        [Theory]
        [InlineData("12345")]
        [InlineData("76561197960278073")]
        [InlineData("STEAM_0:1:6172")]
        [InlineData("STEAM_1:1:6172")]
        [InlineData("steam_0:1:6172")]
        [InlineData("[U:1:12345]")]
        [InlineData("[u:1:12345]")]
        [InlineData("U:1:12345")]
        [InlineData("u:1:12345")]
        [InlineData("  12345  ")]
        [InlineData("\t[U:1:12345]\n")]
        public void EachFormatGivesTheAccountNumber(string input)
        {
            Assert.Equal(12345u, AccountNumber(input));
        }

        // Every mix of scheme, www, capitals, a trailing slash and spaces around it
        public static TheoryData<string> ProfileLinks()
        {
            var links = new TheoryData<string>();

            foreach (var scheme in new[] { "", "http://", "https://" })
                foreach (var www in new[] { "", "www." })
                    foreach (var slash in new[] { "", "/" })
                        foreach (var spaces in new[] { "", "  " })
                        {
                            var link = $"{spaces}{scheme}{www}steamcommunity.com/profiles/76561197960278073{slash}{spaces}";

                            links.Add(link);
                            links.Add(link.ToUpperInvariant());
                        }

            return links;
        }

        [Theory]
        [MemberData(nameof(ProfileLinks))]
        public void ProfileLinksGiveTheAccountNumber(string link)
        {
            Assert.Equal(12345u, AccountNumber(link));
        }

        [Theory]
        [InlineData("1", 1u)]
        [InlineData("76561197960265729", 1u)]
        [InlineData("STEAM_0:1:0", 1u)]
        [InlineData("[U:1:1]", 1u)]
        [InlineData("4294967295", 4294967295u)]
        [InlineData("76561202255233023", 4294967295u)]
        [InlineData("STEAM_0:1:2147483647", 4294967295u)]
        [InlineData("[U:1:4294967295]", 4294967295u)]
        [InlineData("STEAM_0:0:2147483647", 4294967294u)]
        [InlineData("https://steamcommunity.com/profiles/76561202255233023", 4294967295u)]
        [InlineData("0000012345", 12345u)]
        public void EdgesOfTheRange(string input, uint accountNumber)
        {
            Assert.Equal(accountNumber, AccountNumber(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("0")]
        [InlineData("[U:1:0]")]
        [InlineData("STEAM_0:0:0")]
        [InlineData("76561197960265728")]
        [InlineData("76561202255233024")]
        [InlineData("4294967296")]
        [InlineData("103582791429521408")]
        [InlineData("76561193665298437")]
        [InlineData("148618791998193669")]
        [InlineData("18446744073709551615")]
        [InlineData("99999999999999999999")]
        [InlineData("123456789012345678901")]
        [InlineData("STEAM_0:2:5")]
        [InlineData("STEAM_2:0:1")]
        [InlineData("STEAM_0:0:2147483648")]
        [InlineData("STEAM_0:1:2147483648")]
        [InlineData("STEAM_0:0:4294967295")]
        [InlineData("STEAM_0:0:99999999999")]
        [InlineData("[U:1:4294967296]")]
        [InlineData("[U:2:5]")]
        [InlineData("[G:1:5]")]
        [InlineData("[U:1:5")]
        [InlineData("U:1:5]")]
        [InlineData("-5")]
        [InlineData("+5")]
        [InlineData("12 345")]
        [InlineData("12,345")]
        [InlineData("1e5")]
        [InlineData("\u0661\u0662\u0663\u0664\u0665")]
        [InlineData("\uFF11\uFF12\uFF13")]
        [InlineData("Test Player")]
        public void AnythingElseIsInvalid(string? input)
        {
            Invalid(input);
        }

        [Theory]
        [InlineData("steamcommunity.com.example.com/profiles/76561197960278073")]
        [InlineData("https://example.com/steamcommunity.com/profiles/76561197960278073")]
        [InlineData("https://steamcommunity.com/profiles/76561197960278073/home")]
        [InlineData("https://steamcommunity.com/profiles/76561197960278073?l=english")]
        [InlineData("https://steamcommunity.com/profiles/76561197960278073//")]
        [InlineData("ftp://steamcommunity.com/profiles/76561197960278073")]
        [InlineData("https://steamcommunity.com/profiles/")]
        [InlineData("https://steamcommunity.com/profiles/103582791429521408")]
        public void OtherLinksAreInvalid(string link)
        {
            Invalid(link);
        }

        // A profile link holds a SteamID64, never a short account number
        [Fact]
        public void ProfileLinkWithAnAccountNumberIsInvalid()
        {
            Assert.Contains("SteamID64", Invalid("https://steamcommunity.com/profiles/12345"));
        }

        [Fact]
        public void SteamId64OfAnotherKindSaysSo()
        {
            Assert.Contains("isn't an individual Steam account", Invalid("103582791429521408"));
            Assert.Contains("out of range", Invalid("4294967296"));
            Assert.Contains("account 0", Invalid("76561197960265728"));
        }

        [Theory]
        [InlineData("https://steamcommunity.com/id/some_name/", "some_name")]
        [InlineData("steamcommunity.com/id/Some-Name", "Some-Name")]
        [InlineData("  http://www.steamcommunity.com/id/abc  ", "abc")]
        public void CustomLinksNeedALookup(string link, string name)
        {
            var result = SteamIds.Parse(link);

            Assert.Equal(SteamIdKind.NeedsLookup, result.Kind);
            Assert.Equal(name, result.VanityName);
            Assert.Equal(0u, result.AccountNumber);
            Assert.Null(result.Error);
        }

        [Theory]
        [InlineData("https://steamcommunity.com/id/")]
        [InlineData("https://steamcommunity.com/id/bad name")]
        [InlineData("https://steamcommunity.com/id/bad.name")]
        [InlineData("https://steamcommunity.com/id/abcdefghijklmnopqrstuvwxyz1234567")]
        [InlineData("https://steamcommunity.com/id/name/games")]
        public void BadCustomLinksAreInvalid(string link)
        {
            Invalid(link);
        }

        [Fact]
        public void AccountNumbersAreWrittenBackOut()
        {
            Assert.Equal(76561197960278073ul, SteamIds.ToSteamId64(12345));
            Assert.Equal(76561197960265729ul, SteamIds.ToSteamId64(1));
            Assert.Equal(76561202255233023ul, SteamIds.ToSteamId64(uint.MaxValue));
            Assert.Equal("[U:1:12345]", SteamIds.ToSteam3(12345));
            Assert.Throws<ArgumentOutOfRangeException>(() => SteamIds.ToSteamId64(0));
        }

        [Theory]
        [InlineData(1u)]
        [InlineData(12345u)]
        [InlineData(4294967295u)]
        public void WrittenFormsReadBackTheSame(uint accountNumber)
        {
            Assert.Equal(accountNumber, AccountNumber(SteamIds.ToSteamId64(accountNumber).ToString()));
            Assert.Equal(accountNumber, AccountNumber(SteamIds.ToSteam3(accountNumber)));
        }
    }
}
