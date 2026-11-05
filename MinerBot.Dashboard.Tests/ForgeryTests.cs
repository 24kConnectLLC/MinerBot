using System.Net;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // A change needs the form's antiforgery token and has to come from the dashboard's own pages. murderminershub.com
    // counts as the same site to browsers, so a request from it carries the Lax session cookie and says same-site.
    [Collection("Database")]
    public class ForgeryTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        [Fact]
        public async Task SignOutNeedsTheTokenAndTheDashboardsOwnOrigin()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = factory.Visitor();
            var key = await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            var token = await SignInSteps.FormTokenAsync(admin);

            async Task Refused(HttpStatusCode status, string? formToken, string? origin = null, string? fetchSite = null, HttpMethod? method = null, string path = "/signout")
            {
                var response = await SignInSteps.PostAsync(admin, path, formToken, origin, fetchSite, method);
                Assert.Equal(status, response.StatusCode);
                Assert.NotNull(await db.SessionAsync(key));
            }

            // No token, or one that isn't real, from the right origin: the antiforgery check
            await Refused(HttpStatusCode.BadRequest, null, origin: DashboardFactory.PublicOrigin);
            await Refused(HttpStatusCode.BadRequest, "made-up", origin: DashboardFactory.PublicOrigin);

            // A real token, from the WordPress site, which browsers treat as the same site
            await Refused(HttpStatusCode.Forbidden, token, origin: "https://murderminershub.com");
            await Refused(HttpStatusCode.Forbidden, token, fetchSite: "same-site");
            await Refused(HttpStatusCode.Forbidden, token, fetchSite: "cross-site");
            await Refused(HttpStatusCode.Forbidden, token, origin: "null");
            await Refused(HttpStatusCode.Forbidden, token);

            // An origin the browser names must be this one, whatever Sec-Fetch-Site says
            await Refused(HttpStatusCode.Forbidden, token, origin: "https://murderminershub.com", fetchSite: "same-origin");

            // Any method that can change something
            await Refused(HttpStatusCode.Forbidden, null, method: HttpMethod.Delete, path: "/admin");
            await Refused(HttpStatusCode.Forbidden, token, method: HttpMethod.Put, path: "/signout");

            // The dashboard's own origin, or only Sec-Fetch-Site same-origin, with the token
            var signedOut = await SignInSteps.PostAsync(admin, "/signout", token, origin: DashboardFactory.PublicOrigin);
            Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
            Assert.Equal("/", SignInSteps.PathOf(signedOut.Headers.Location));
            Assert.Null(await db.SessionAsync(key));
        }

        [Fact]
        public async Task SameOriginFetchMetadataIsEnough()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = factory.Visitor();
            var key = await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            var token = await SignInSteps.FormTokenAsync(admin);

            var response = await SignInSteps.PostAsync(admin, "/signout", token, fetchSite: "same-origin");

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Null(await db.SessionAsync(key));
        }

        [Fact]
        public async Task RefusalSaysWhyInPlainWords()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var response = await SignInSteps.PostAsync(factory.Visitor(), "/signout", "made-up", origin: "https://murderminershub.com");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("This request came from another site, so the dashboard didn't act on it.", await response.Content.ReadAsStringAsync(Token));
        }

        [Fact]
        public async Task AntiforgeryCookieIsAHostCookie()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var admin = factory.Visitor();
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            var page = await admin.Client.GetAsync("/admin", Token);

            var cookie = Assert.Single(page.Headers.GetValues("Set-Cookie"), header => header.StartsWith("__Host-mb-af=", StringComparison.Ordinal));
            var attributes = cookie.Split(';').Skip(1).Select(attribute => attribute.Trim().ToLowerInvariant()).ToList();

            Assert.Contains("secure", attributes);
            Assert.Contains("httponly", attributes);
            Assert.Contains("samesite=strict", attributes);
            Assert.Contains("path=/", attributes);
            Assert.DoesNotContain(attributes, attribute => attribute.StartsWith("domain", StringComparison.Ordinal));
        }

        // The forged-request page shows the plain 400 page, not a stack trace
        [Fact]
        public async Task MissingTokenShowsTheExpiredFormPage()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = factory.Visitor();
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            var response = await SignInSteps.PostAsync(admin, "/signout", null, origin: DashboardFactory.PublicOrigin);
            var page = await response.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("That form had expired or was incomplete", page);
            Assert.Contains("Request ID", page);
        }
    }
}
