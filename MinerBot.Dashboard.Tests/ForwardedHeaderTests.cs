using System.Net;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // The visitor's address and https come from nginx alone: one hop, from loopback, and never the host. The client
    // address filter puts nginx's 127.0.0.1 on every request unless a test names another, so these can't pass because
    // TestServer leaves the address empty.
    [Collection("Database")]
    public class ForwardedHeaderTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static async Task<string?> SignedInAddressAsync(MariaDbFixture db, DashboardFactory factory, TestBrowser browser)
        {
            var key = await SignInSteps.SignedInAsync(factory, browser, TestUsers.Owner);
            var session = await db.SessionAsync(key);
            var signedIn = Assert.Single(await db.AuditAsync(), row => row.action == "auth.signin");

            Assert.Equal(session?.ip, signedIn.ip);
            return session?.ip;
        }

        [Fact]
        public async Task VisitorsAddressComesFromNginx()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);

            Assert.Equal("203.0.113.10", await SignedInAddressAsync(db, factory, factory.Visitor("203.0.113.10")));
        }

        [Fact]
        public async Task HeadersFromAnythingButLoopbackAreIgnored()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);

            Assert.Equal("198.51.100.7", await SignedInAddressAsync(db, factory, factory.Visitor("203.0.113.10", remote: "198.51.100.7")));
        }

        // Only the last hop, which nginx added. Anything before it came from the visitor, who can write anything, even a
        // loopback address that would otherwise pass for another proxy.
        [Theory]
        [InlineData("198.51.100.1, 203.0.113.10", "203.0.113.10")]
        [InlineData("198.51.100.1, 127.0.0.5", "127.0.0.5")]
        public async Task OnlyOneHopIsTrusted(string forwardedFor, string address)
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);

            Assert.Equal(address, await SignedInAddressAsync(db, factory, factory.Visitor(forwardedFor)));
        }

        [Fact]
        public async Task ForwardedHostIsNeverTrusted()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var browser = factory.Visitor();
            browser.Client.DefaultRequestHeaders.Add("X-Forwarded-Host", "evil.example.test");

            var authorize = await SignInSteps.StartAsync(browser);

            Assert.Equal("https://dashboard.test/signin-discord", authorize.RedirectUri);
        }

        // nginx always sends https. A plain http connection that says https is believed only from loopback.
        [Theory]
        [InlineData(null, "https://dashboard.test/signin-discord")]
        [InlineData("198.51.100.7", "http://dashboard.test/signin-discord")]
        public async Task HttpsComesOnlyFromNginx(string? remote, string redirectUri)
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var browser = factory.Visitor(remote: remote, baseAddress: new Uri("http://dashboard.test"));

            var authorize = await SignInSteps.StartAsync(browser);

            Assert.Equal(redirectUri, authorize.RedirectUri);
        }

        // ASPNETCORE_FORWARDEDHEADERS_ENABLED=true would trust any address. The dashboard puts the loopback limit back.
        [Fact]
        public async Task ForwardedHeadersEnabledStillTrustsOnlyLoopback()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Settings = new Dictionary<string, string?> { ["FORWARDEDHEADERS_ENABLED"] = "true" }
            });

            Assert.Equal("198.51.100.7", await SignedInAddressAsync(db, factory, factory.Visitor("203.0.113.10", remote: "198.51.100.7")));

            var http = factory.Visitor(remote: "198.51.100.7", baseAddress: new Uri("http://dashboard.test"));
            Assert.Equal("http://dashboard.test/signin-discord", (await SignInSteps.StartAsync(http)).RedirectUri);
        }

        // The refusal's audit row has the visitor's address too
        [Fact]
        public async Task RefusalRecordsTheVisitorsAddress()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);

            var response = await SignInSteps.SignInAsync(factory, factory.Visitor("203.0.113.44"), TestUsers.Visitor);

            Assert.Equal("/denied?reason=unlisted", SignInSteps.PathOf(response.Headers.Location));
            var refused = Assert.Single(await db.AuditAsync());
            Assert.Equal("203.0.113.44", refused.ip);
            Assert.Equal(AuditRole.Visitor, refused.actor_role);
        }
    }
}
