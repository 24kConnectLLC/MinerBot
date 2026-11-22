using System.Net;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Pages.Admin;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Requests;

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

            // This one exactly: not one that only starts with it, not another port, and not this one beside another
            await Refused(HttpStatusCode.Forbidden, token, origin: DashboardFactory.PublicOrigin + ".evil.example.test");
            await Refused(HttpStatusCode.Forbidden, token, origin: DashboardFactory.PublicOrigin + ":8443");

            var twice = new HttpRequestMessage(HttpMethod.Post, "/signout") { Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("__RequestVerificationToken", token)]) };
            Assert.True(twice.Headers.TryAddWithoutValidation("Origin", [DashboardFactory.PublicOrigin, "https://murderminershub.com"]));
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.Client.SendAsync(twice, Token)).StatusCode);
            Assert.NotNull(await db.SessionAsync(key));

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
            Assert.Contains("samesite=lax", attributes);
            Assert.Contains("path=/", attributes);
            Assert.DoesNotContain(attributes, attribute => attribute.StartsWith("domain", StringComparison.Ordinal));
        }

        // Every form that sends a request to the bot or ends a session, the controls page's, the armor page's Announce now, a
        // server's test message and refresh, the leave page's, the blacklist's custom-link lookup and the Sessions page's,
        // needs the form's token and the dashboard's own origin, as sign-out does, before anything is read or written. The
        // same form sent properly works.
        [Fact]
        public async Task EveryNewFormNeedsItsTokenAndTheDashboardsOrigin()
        {
            var db = await fixture.ReadyBotAsync();
            await db.AddArmorAsync(ArmorSteps.Submission(1, Shared.Armor.ArmorStatus.Approved));
            await StatusSteps.ServersAsync(db);
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.81");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var other = factory.Visitor("203.0.113.82");
            await SignInSteps.SignedInAsync(factory, other, TestUsers.Admin);
            var page = await RequestSteps.ControlsAsync(owner);
            var before = (await db.AuditAsync()).Count;
            var server = "/admin/servers/" + StatusSteps.ServerId;
            var paths = ControlsModel.Handlers.Keys.Select(handler => "/admin/bot/controls?handler=" + handler)
                .Concat(["/admin/armor/1?handler=Announce", server + "?handler=TestMessage", server + "?handler=Refresh", server + "/leave", "/admin/blacklist/add",
                    "/admin/sessions?handler=End", "/admin/sessions?handler=EndOthers"]);

            foreach (var path in paths)
            {
                KeyValuePair<string, string>[] fields = [new("formkey", RequestFields.NewKey()), new("confirm", "restart"), new("channel", "3001"),
                    new("steamid", "https://steamcommunity.com/id/gabelogannewell"), new("session", Convert.ToHexStringLower(DashboardSessionData.HashOf(other.SessionKey!)))];

                Assert.Equal(HttpStatusCode.BadRequest, (await SignInSteps.PostAsync(owner, path, null, origin: DashboardFactory.PublicOrigin, fields: fields)).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await SignInSteps.PostAsync(owner, path, page.Token, origin: "https://murderminershub.com", fields: fields)).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await SignInSteps.PostAsync(owner, path, page.Token, fetchSite: "same-site", fields: fields)).StatusCode);
            }

            Assert.Empty(await db.SentAsync());
            Assert.Equal(before, (await db.AuditAsync()).Count);
            Assert.Equal(2, (await db.SessionsAsync()).Count);
            Assert.Equal(0, await db.EntryCountAsync());

            var sent = await RequestSteps.PostControlAsync(owner, page, ControlWords.ClearCaches);
            Assert.Equal(HttpStatusCode.Redirect, sent.StatusCode);
            Assert.Single(await db.SentAsync());
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
