using MinerBot.Dashboard.Auth;

namespace MinerBot.Dashboard.Tests
{
    // Where a sign-in goes afterwards: a path on this site that Kestrel can send in a Location header as it is, and the
    // overview for anything else
    public class ReturnUrlTests
    {
        [Theory]
        [InlineData("/")]
        [InlineData("/admin")]
        [InlineData("/denied?reason=x")]
        [InlineData("/admin/caf%C3%A9?q=a%20b")]
        public void PathOnThisSiteIsKept(string url)
        {
            Assert.True(ReturnUrls.IsLocal(url));
            Assert.Equal(url, ReturnUrls.Pick(url));
        }

        // Another site, or no address at all
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("admin")]
        [InlineData("https://evil.example.test/")]
        [InlineData("//evil.example.test")]
        [InlineData("/\\evil.example.test")]
        public void AnotherSiteGoesToTheOverview(string? url)
        {
            Assert.False(ReturnUrls.IsLocal(url));
            Assert.Equal(ReturnUrls.Default, ReturnUrls.Pick(url));
        }

        // A character browsers drop before reading the rest, or one Kestrel won't put in a header, which would fail the
        // redirect after Discord once the session had been stored. Written by number, so none of them can turn into
        // something else in the source.
        public static TheoryData<string> NotPlainAscii => new()
        {
            "/caf" + (char)0xE9,
            "/admin" + (char)0x2028,
            "/admin" + (char)0xA0,
            "/admin" + (char)0x7F,
            "/a" + (char)0x09 + "b",
            "/a b"
        };

        // Fails if anything but printable ASCII gets through
        [Theory]
        [MemberData(nameof(NotPlainAscii))]
        public void PathKestrelWouldRefuseGoesToTheOverview(string url)
        {
            Assert.False(ReturnUrls.IsLocal(url));
            Assert.Equal(ReturnUrls.Default, ReturnUrls.Pick(url));
        }

        [Fact]
        public void LongPathGoesToTheOverview()
        {
            Assert.True(ReturnUrls.IsLocal("/" + new string('a', 511)));
            Assert.False(ReturnUrls.IsLocal("/" + new string('a', 512)));
        }
    }
}
