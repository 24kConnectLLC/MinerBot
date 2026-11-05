using System.Net;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // Limits per visitor address, taken after the forwarded headers. The limiter runs on real time, and each test's
    // requests take well under its one-minute window.
    [Collection("Database")]
    public class RateLimitTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static async Task<HttpResponseMessage> RepeatAsync(int times, Func<Task<HttpResponseMessage>> request, HttpStatusCode each)
        {
            for (var i = 0; i < times; i++)
            {
                var response = await request();
                Assert.True(response.StatusCode == each, $"Request {i + 1} answered {response.StatusCode}");
            }

            return await request();
        }

        // Fails if the key were taken before the forwarded headers, when everyone would share nginx's 127.0.0.1, or if a
        // spelling that routing still takes for the sign-in page, such as /signin/, escaped to the visitor's limit
        [Theory]
        [InlineData("/signin")]
        [InlineData("/signin/")]
        [InlineData("/SignIn/")]
        public async Task SignInIsLimitedForEachVisitor(string path)
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var visitor = factory.Visitor("203.0.113.20");

            var eleventh = await RepeatAsync(RateLimits.SignInPerMinute, () => visitor.Client.GetAsync(path, Token), HttpStatusCode.Redirect);

            Assert.Equal(HttpStatusCode.TooManyRequests, eleventh.StatusCode);
            Assert.True(int.TryParse(Assert.Single(eleventh.Headers.GetValues("Retry-After")), out var seconds) && seconds is > 0 and <= 60);
            Assert.Equal(RateLimits.Rejection, await eleventh.Content.ReadAsStringAsync(Token));

            Assert.Equal(HttpStatusCode.Redirect, (await factory.Visitor("203.0.113.21").Client.GetAsync(path, Token)).StatusCode);
        }

        // Fails if the limiter sat after UseAuthentication, which answers Discord's callback itself
        [Fact]
        public async Task CallbackIsLimitedToo()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var visitor = factory.Visitor("203.0.113.22");

            var eleventh = await RepeatAsync(RateLimits.SignInPerMinute, () => visitor.Client.GetAsync("/signin-discord?code=made-up&state=made-up", Token), HttpStatusCode.Redirect);

            Assert.Equal(HttpStatusCode.TooManyRequests, eleventh.StatusCode);
            Assert.Empty(factory.Discord.Calls);
        }

        // A made-up session cookie costs a session lookup, so it never buys more than the visitor's own limit. Fails if a
        // cookie alone moved a visitor to a bigger allowance.
        [Fact]
        public async Task VisitorsAreLimitedWithOrWithoutASessionCookie()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var visitor = factory.Visitor("203.0.113.23");

            var limited = await RepeatAsync(RateLimits.VisitorPerMinute, () => visitor.Client.GetAsync("/", Token), HttpStatusCode.OK);
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

            var cookie = factory.Visitor("203.0.113.24");
            cookie.Jar.Add(DashboardFactory.PublicAddress, new Cookie(DashboardAuth.SessionCookie, SessionKeys.New()) { Secure = true });

            var sixtyFirst = await RepeatAsync(RateLimits.VisitorPerMinute, () => cookie.Client.GetAsync("/", Token), HttpStatusCode.OK);
            Assert.Equal(HttpStatusCode.TooManyRequests, sixtyFirst.StatusCode);
        }

        // Fails if a header from something other than nginx were trusted, when a new X-Forwarded-For would mean a new allowance
        [Fact]
        public async Task ForgedForwardedForDoesNotEscapeTheLimit()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var count = 0;

            Task<HttpResponseMessage> Next() => factory.Visitor($"203.0.113.{100 + ++count}", remote: "198.51.100.7").Client.GetAsync("/signin", Token);

            var eleventh = await RepeatAsync(RateLimits.SignInPerMinute, Next, HttpStatusCode.Redirect);

            Assert.Equal(HttpStatusCode.TooManyRequests, eleventh.StatusCode);
        }

        [Fact]
        public async Task LocalHealthChecksAreNeverLimited()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            using var local = factory.Local();

            var last = await RepeatAsync(69, () => local.GetAsync("/health/live", Token), HttpStatusCode.OK);

            Assert.Equal(HttpStatusCode.OK, last.StatusCode);
        }

        // Whole segments, so /signin/ counts as a sign-in and /signinx or /signout don't
        [Theory]
        [InlineData("/signin", true)]
        [InlineData("/signin/", true)]
        [InlineData("/SIGNIN/", true)]
        [InlineData("/signin/x", true)]
        [InlineData("/signin-discord", true)]
        [InlineData("/Signin-Discord", true)]
        [InlineData("/", false)]
        [InlineData("/signout", false)]
        [InlineData("/signinx", false)]
        [InlineData("/signin-discordx", false)]
        [InlineData("/admin/signin", false)]
        public void SignInPathsShareTheSignInLimit(string path, bool signIn)
        {
            Assert.Equal(signIn, RateLimits.IsSignIn(path));
        }

        [Theory]
        [InlineData("203.0.113.9", "203.0.113.9")]
        [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
        [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
        [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::/64")]
        public void IPv6VisitorsAreCountedByTheirSlash64(string address, string bucket)
        {
            Assert.Equal(bucket, RateLimits.Visitor(IPAddress.Parse(address)));
        }
    }
}
