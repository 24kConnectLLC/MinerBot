using System.Net;
using System.Security.Cryptography;
using System.Text;
using DataAccessLibrary;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Dashboard.Models;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Hosting;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // Server-side sessions: the cookie holds only a random key, the ticket is encrypted in MinerBot's database, and every
    // limit is enforced there from the row, on the fake clock
    [Collection("Database")]
    public class SessionTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static async Task<HttpStatusCode> AdminAsync(TestBrowser browser) => (await browser.Client.GetAsync("/admin", Token)).StatusCode;

        static async Task<string?> AdminLocationAsync(TestBrowser browser) => SignInSteps.PathOf((await browser.Client.GetAsync("/admin", Token)).Headers.Location);

        // Fails with the cookie handler's own encrypted envelope, or with a persistent cookie
        [Fact]
        public async Task SessionCookieHoldsOnlyTheKey()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor();

            var response = await SignInSteps.SignInAsync(factory, owner, TestUsers.Owner);

            var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), header => header.StartsWith(DashboardAuth.SessionCookie + "=", StringComparison.Ordinal));
            var parts = cookie.Split(';').Select(part => part.Trim()).ToList();
            var value = parts[0][(DashboardAuth.SessionCookie.Length + 1)..];
            var attributes = parts.Skip(1).Select(part => part.ToLowerInvariant()).ToList();

            Assert.Matches("^[A-Za-z0-9_-]{43}$", value);
            Assert.Equal(["httponly", "path=/", "samesite=lax", "secure"], attributes.Order());

            var row = Assert.Single(await db.SessionsAsync());
            Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(value)), row.id_hash);
            Assert.Equal(value, owner.SessionKey);
        }

        // The ticket's purpose includes the cookie's key, so a ticket opens only for its own session, even for someone who
        // can write to the table
        [Fact]
        public async Task TicketIsEncryptedAndBoundToItsKey()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor();
            var admin = factory.Visitor("203.0.113.11");
            var ownerKey = await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var adminKey = await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            var ownerRow = (await db.SessionAsync(ownerKey))!;
            var adminRow = (await db.SessionAsync(adminKey))!;

            Assert.True(ownerRow.ticket.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Test Owner")) < 0);
            Assert.True(ownerRow.ticket.AsSpan().IndexOf(Encoding.UTF8.GetBytes(TestUsers.Owner.IdText)) < 0);

            var tickets = factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(DbTicketStore.TicketPurpose);
            var opened = TicketSerializer.Default.Deserialize(tickets.CreateProtector(ownerKey).Unprotect(ownerRow.ticket))!;
            Assert.Equal(TestUsers.Owner.IdText, SignedInUser.From(opened.Principal)!.Id.ToString());
            Assert.ThrowsAny<CryptographicException>(() => tickets.CreateProtector(adminKey).Unprotect(ownerRow.ticket));

            // Each row given the other's ticket: neither session opens, and both are ended
            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.SessionTable} SET ticket = @ticket WHERE id_hash = @hash", new { ticket = adminRow.ticket, hash = ownerRow.id_hash });
            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.SessionTable} SET ticket = @ticket WHERE id_hash = @hash", new { ticket = ownerRow.ticket, hash = adminRow.id_hash });

            Assert.Equal("/signin?returnUrl=%2Fadmin", await AdminLocationAsync(owner));
            Assert.Equal("/signin?returnUrl=%2Fadmin", await AdminLocationAsync(admin));
            Assert.Empty(await db.SessionsAsync());
        }

        [Fact]
        public async Task IdleSessionEnds()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var stillHere = factory.Visitor();
            await SignInSteps.SignedInAsync(factory, stillHere, TestUsers.Owner);

            factory.Clock.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(1));
            Assert.Equal(HttpStatusCode.OK, await AdminAsync(stillHere));

            var away = factory.Visitor("203.0.113.11");
            var key = await SignInSteps.SignedInAsync(factory, away, TestUsers.Admin);

            factory.Clock.Advance(TimeSpan.FromMinutes(30));
            Assert.Equal("/signin?returnUrl=%2Fadmin", await AdminLocationAsync(away));
            Assert.Null(await db.SessionAsync(key));
        }

        // Fails with sliding expiration: activity never moves the end
        [Fact]
        public async Task AbsoluteLimitEndsAnActiveSession()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor();
            var key = await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var expiresAt = (await db.SessionAsync(key))!.expires_at;

            Assert.Equal((await db.SessionAsync(key))!.created_at.AddHours(12), expiresAt);

            // Every 20 minutes for 11 hours and 40 minutes, inside both the idle limit and the 60 requests a minute that
            // one address may make
            for (var minutes = 20; minutes < 12 * 60; minutes += 20)
            {
                factory.Clock.Advance(TimeSpan.FromMinutes(20));
                Assert.True(await AdminAsync(owner) == HttpStatusCode.OK, $"The session ended after {minutes} minutes");
            }

            Assert.Equal(expiresAt, (await db.SessionAsync(key))!.expires_at);

            factory.Clock.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal("/signin?returnUrl=%2Fadmin", await AdminLocationAsync(owner));
        }

        [Fact]
        public async Task LastSeenIsTouchedAtMostOnceAMinute()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor();
            var key = await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var signedIn = (await db.SessionAsync(key))!.last_seen_at;

            factory.Clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(HttpStatusCode.OK, await AdminAsync(owner));
            Assert.Equal(signedIn, (await db.SessionAsync(key))!.last_seen_at);

            factory.Clock.Advance(TimeSpan.FromSeconds(31));
            Assert.Equal(HttpStatusCode.OK, await AdminAsync(owner));
            Assert.Equal(signedIn.AddSeconds(61), (await db.SessionAsync(key))!.last_seen_at);
        }

        // The oldest goes, and another person's session isn't touched
        [Fact]
        public async Task FiveSessionsAtMost()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var other = factory.Visitor("203.0.113.30");
            var otherKey = await SignInSteps.SignedInAsync(factory, other, TestUsers.Owner);
            var browsers = new List<TestBrowser>();

            // Each from its own address, because six sign-ins from one would pass the sign-in limit
            for (var i = 0; i < DbTicketStore.MaxSessionsPerPerson + 1; i++)
            {
                factory.Clock.Advance(TimeSpan.FromMinutes(1));
                var browser = factory.Visitor($"203.0.113.{31 + i}");
                await SignInSteps.SignedInAsync(factory, browser, TestUsers.Admin);
                browsers.Add(browser);
            }

            var sessions = await db.SessionsAsync();
            Assert.Equal(DbTicketStore.MaxSessionsPerPerson, sessions.Count(row => row.user_id == TestUsers.Admin.Id));
            Assert.NotNull(await db.SessionAsync(otherKey));

            Assert.Equal("/signin?returnUrl=%2Fadmin", await AdminLocationAsync(browsers[0]));

            foreach (var browser in browsers.Skip(1))
                Assert.Equal(HttpStatusCode.OK, await AdminAsync(browser));

            Assert.Equal(HttpStatusCode.OK, await AdminAsync(other));
        }

        // A browser that's signed in skips Discord at /signin, but one with two sign-ins under way, such as in two tabs,
        // can finish the second while the first is signed in. The cookie handler would renew the old session under its
        // old key, so the sign-in ends it first.
        [Fact]
        public async Task EachSignInGetsANewKey()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor();
            var firstTab = await SignInSteps.StartAsync(owner);
            var secondTab = await SignInSteps.StartAsync(owner);

            Assert.Equal(HttpStatusCode.Redirect, (await SignInSteps.CallbackAsync(owner, factory.Discord.Expect(firstTab.CodeChallenge, TestUsers.Owner), firstTab.State)).StatusCode);
            var first = owner.SessionKey!;

            Assert.Equal(HttpStatusCode.Redirect, (await SignInSteps.CallbackAsync(owner, factory.Discord.Expect(secondTab.CodeChallenge, TestUsers.Owner), secondTab.State)).StatusCode);
            var second = owner.SessionKey!;

            Assert.NotEqual(first, second);
            Assert.Null(await db.SessionAsync(first));
            Assert.NotNull(await db.SessionAsync(second));
            Assert.Single(await db.SessionsAsync());
            Assert.Equal(2, (await db.AuditAsync()).Count(row => row.action == "auth.signin"));
        }

        // Past its 12 hours, or unused for longer than the idle limit, which would be refused anyway. Fails if either kind
        // stays, with its address and browser, until the other one's time comes.
        [Fact]
        public async Task SignInDeletesEndedSessions()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var sessions = db.Sessions();

            DashboardSession Row(DateTime expiresAt, DateTime lastSeenAt) => new()
            {
                user_id = TestUsers.SecondAdmin.Id,
                user_name = "Second Admin",
                created_at = expiresAt.AddHours(-12),
                last_seen_at = lastSeenAt,
                expires_at = expiresAt,
                ip = "203.0.113.70",
                user_agent = "Test Browser/1.0",
                ticket = [1, 2, 3]
            };

            var ended = SessionKeys.New();
            var unused = SessionKeys.New();
            var live = SessionKeys.New();
            await sessions.CreateSession(ended, Row(now.AddMinutes(-1), now.AddMinutes(-2)));
            await sessions.CreateSession(unused, Row(now.AddHours(1), now - DbTicketStore.IdleLimit - TimeSpan.FromMinutes(1)));
            await sessions.CreateSession(live, Row(now.AddHours(1), now - DbTicketStore.IdleLimit + TimeSpan.FromMinutes(1)));

            await SignInSteps.SignedInAsync(factory, factory.Visitor(), TestUsers.Owner);

            var rows = await db.SessionsAsync();
            Assert.DoesNotContain(rows, row => row.IsFor(ended));
            Assert.DoesNotContain(rows, row => row.IsFor(unused));
            Assert.Contains(rows, row => row.IsFor(live));
        }

        [Fact]
        public async Task SignInWritesItsAuditRow()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);

            await SignInSteps.SignedInAsync(factory, factory.Visitor("203.0.113.12"), TestUsers.Admin);

            var signedIn = Assert.Single(await db.AuditAsync());
            Assert.Equal("auth.signin", signedIn.action);
            Assert.Equal(AuditOutcome.Ok, signedIn.outcome);
            Assert.Equal(AuditRole.Admin, signedIn.actor_role);
            Assert.Equal(TestUsers.Admin.Id, signedIn.actor_id);
            Assert.Equal("203.0.113.12", signedIn.ip);
            Assert.False(string.IsNullOrEmpty(signedIn.request_id));
        }

        // An audit log that fails stops the sign-in, and neither the session nor the audit row is left behind. Fails if the
        // audit row is written outside the session's transaction, before it or after it.
        [Fact]
        public async Task SignInAndItsAuditRowCommitTogether()
        {
            var db = await fixture.ReadyAsync();
            var failures = new Failures { AuditAction = "auth.signin" };
            await using var factory = Failing(db, failures);

            var response = await SignInSteps.SignInAsync(factory, factory.Visitor(), TestUsers.Owner);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("can't reach its database", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token)));
            Assert.Empty(await db.SessionsAsync());
            Assert.Empty(await db.AuditAsync());
        }

        // The cap on sessions is part of the sign-in's transaction, so a sign-in whose cap fails leaves no new session.
        // Fails if the cap runs after the transaction; FiveSessionsAtMost fails if it runs before the new session is made.
        [Fact]
        public async Task SessionCapIsPartOfTheSignIn()
        {
            var db = await fixture.ReadyAsync();
            var failures = new Failures { SessionCap = true };
            await using var factory = Failing(db, failures);

            var response = await SignInSteps.SignInAsync(factory, factory.Visitor(), TestUsers.Owner);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Empty(await db.SessionsAsync());
            Assert.Empty(await db.AuditAsync());
        }

        // A sign-out whose audit row fails keeps the session, and writes no row. Fails if the audit row is written outside
        // the sign-out's transaction, before it or after it.
        [Fact]
        public async Task SignOutAndItsAuditRowCommitTogether()
        {
            var db = await fixture.ReadyAsync();
            var failures = new Failures();
            await using var factory = Failing(db, failures);
            var owner = factory.Visitor();
            var key = await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var token = await SignInSteps.FormTokenAsync(owner);

            failures.AuditAction = "auth.signout";
            var response = await SignInSteps.PostAsync(owner, "/signout", token, origin: DashboardFactory.PublicOrigin);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.NotNull(await db.SessionAsync(key));
            Assert.DoesNotContain(await db.AuditAsync(), row => row.action == "auth.signout");

            // With the database back, the same session signs out
            failures.AuditAction = null;
            Assert.Equal(HttpStatusCode.Redirect, (await SignInSteps.PostAsync(owner, "/signout", token, origin: DashboardFactory.PublicOrigin)).StatusCode);
            Assert.Null(await db.SessionAsync(key));
            Assert.Single(await db.AuditAsync(), row => row.action == "auth.signout");
        }

        // Someone taken off the lists keeps their sessions until the audit row that ends them can be written, so the log
        // never misses an ending. Fails if the audit row is written outside that transaction, before it or after it.
        [Fact]
        public async Task RemovalAndItsAuditRowCommitTogether()
        {
            var db = await fixture.ReadyAsync();
            var failures = new Failures();
            await using var factory = Failing(db, failures);
            var admin = factory.Visitor();
            var otherDevice = factory.Visitor("203.0.113.71");
            var key = await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            var otherKey = await SignInSteps.SignedInAsync(factory, otherDevice, TestUsers.Admin);

            factory.Settings.Change(("Dashboard:Admins", TestUsers.SecondAdmin.IdText));
            failures.AuditAction = "auth.refused";

            Assert.Equal(HttpStatusCode.ServiceUnavailable, await AdminAsync(admin));
            Assert.NotNull(await db.SessionAsync(key));
            Assert.NotNull(await db.SessionAsync(otherKey));
            Assert.DoesNotContain(await db.AuditAsync(), row => row.action == "auth.refused");

            // With the database back, both end together with one row
            failures.AuditAction = null;
            Assert.Equal("/signin?returnUrl=%2Fadmin", await AdminLocationAsync(admin));
            Assert.Empty(await db.SessionsAsync());
            Assert.Single(await db.AuditAsync(), row => row.action == "auth.refused");
        }

        // What the next database call should fail at, as a database would that dropped the connection just after the
        // statement ran. A row written inside a transaction is rolled back with it, and one written on its own stays.
        sealed class Failures
        {
            public string? AuditAction { get; set; }
            public bool SessionCap { get; set; }
        }

        static DashboardFactory Failing(MariaDbFixture db, Failures failures) => new(db, new DashboardFactoryOptions
        {
            Services = services =>
            {
                services.Replace(ServiceDescriptor.Transient<IAuditLogData>(provider =>
                    new FailingAudit(ActivatorUtilities.CreateInstance<AuditLogData>(provider), failures)));
                services.Replace(ServiceDescriptor.Transient<IDashboardSessionData>(provider =>
                    new FailingSessions(ActivatorUtilities.CreateInstance<DashboardSessionData>(provider), failures)));
            }
        });

        static TimeoutException Dropped() => new("The database didn't answer within 15 seconds");

        sealed class FailingAudit(AuditLogData real, Failures failures) : IAuditLogData
        {
            public async Task AddAudit(AuditEntry entry, DataTransaction? transaction = null)
            {
                await real.AddAudit(entry, transaction);

                if (entry.action == failures.AuditAction)
                    throw Dropped();
            }

            public Task<Page<AuditEntry>> ListAudit(AuditQuery query) => real.ListAudit(query);
        }

        sealed class FailingSessions(DashboardSessionData real, Failures failures) : IDashboardSessionData
        {
            public async Task<int> DeleteOldestSessions(ulong userId, int keep, DataTransaction? transaction = null)
            {
                var deleted = await real.DeleteOldestSessions(userId, keep, transaction);
                return failures.SessionCap && keep > 0 ? throw Dropped() : deleted;
            }

            public Task CreateSession(string key, DashboardSession session, DataTransaction? transaction = null) => real.CreateSession(key, session, transaction);
            public Task<int> DeleteExpiredSessions(DateTime expiredBefore, DateTime unusedBefore) => real.DeleteExpiredSessions(expiredBefore, unusedBefore);
            public Task<int> DeleteOtherSessions(ulong userId, string keepKey, DataTransaction? transaction = null) => real.DeleteOtherSessions(userId, keepKey, transaction);
            public Task<bool> DeleteSession(string key, DataTransaction? transaction = null) => real.DeleteSession(key, transaction);
            public Task<DashboardSession?> GetSession(string key, DateTime now) => real.GetSession(key, now);
            public Task<bool> RenewSession(string key, byte[] ticket) => real.RenewSession(key, ticket);
            public Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work) => real.RunInTransaction(work);
            public Task<bool> TouchSession(string key, DateTime lastSeenAt) => real.TouchSession(key, lastSeenAt);
        }

        [Fact]
        public async Task SignOutDeletesTheRowAndAudits()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor();
            var key = await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            var token = await SignInSteps.FormTokenAsync(owner);
            var response = await SignInSteps.PostAsync(owner, "/signout", token, origin: DashboardFactory.PublicOrigin);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(DashboardAuth.SessionCookie + "=;", StringComparison.Ordinal));
            Assert.Null(await db.SessionAsync(key));
            Assert.Null(owner.SessionKey);

            var signedOut = Assert.Single(await db.AuditAsync(), row => row.action == "auth.signout");
            Assert.Equal(AuditRole.Owner, signedOut.actor_role);
            Assert.Equal(TestUsers.Owner.Id, signedOut.actor_id);

            Assert.Equal("/signin?returnUrl=%2Fadmin", await AdminLocationAsync(owner));

            // Signed out already, the same form again only sends the browser home, and nothing more is written
            var again = await SignInSteps.PostAsync(owner, "/signout", token, origin: DashboardFactory.PublicOrigin);
            Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
            Assert.Equal("/", SignInSteps.PathOf(again.Headers.Location));
            Assert.Single(await db.AuditAsync(), row => row.action == "auth.signout");
        }

        // The session ended on the server, after 30 minutes unused or with its person taken off the lists, while the page
        // with the form stayed open. The form's token was made for that session, so Razor Pages' own check would refuse it
        // with the expired-form page and keep the dead cookie. Fails if sign-out answers 400.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task SignOutAfterTheSessionHasEndedStillSignsOut(bool takenOffTheList)
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = factory.Visitor();
            var key = await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            var token = await SignInSteps.FormTokenAsync(admin);

            if (takenOffTheList)
                factory.Settings.Change(("Dashboard:Admins", TestUsers.SecondAdmin.IdText));
            else
                factory.Clock.Advance(DbTicketStore.IdleLimit + TimeSpan.FromMinutes(1));

            var response = await SignInSteps.PostAsync(admin, "/signout", token, origin: DashboardFactory.PublicOrigin);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/", SignInSteps.PathOf(response.Headers.Location));
            Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(DashboardAuth.SessionCookie + "=;", StringComparison.Ordinal));
            Assert.Null(admin.SessionKey);
            Assert.Null(await db.SessionAsync(key));

            // The session had already ended, so there was nothing left to sign out of
            Assert.DoesNotContain(await db.AuditAsync(), row => row.action == "auth.signout");
        }

        // Fails open would show the overview; fails closed shows the 503 page
        [Fact]
        public async Task DatabaseDownFailsClosed()
        {
            var db = await fixture.ReadyAsync();
            var keys = DashboardFactory.NewKeysFolder();

            try
            {
                string key;

                await using (var working = new DashboardFactory(db, new DashboardFactoryOptions { KeysFolder = keys }))
                    key = await SignInSteps.SignedInAsync(working, working.Visitor(), TestUsers.Owner);

                var dead = new DeadDatabase();
                await using var down = new DashboardFactory(db, new DashboardFactoryOptions { KeysFolder = keys, BotConnection = dead.BotConnection });
                var browser = down.Visitor();
                browser.Jar.Add(DashboardFactory.PublicAddress, new Cookie(DashboardAuth.SessionCookie, key) { Secure = true });

                var response = await browser.Client.GetAsync("/admin", Token);
                var page = await response.Content.ReadAsStringAsync(Token);

                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.DoesNotContain("Overview", page);
                Assert.Contains("can't reach its database", WebUtility.HtmlDecode(page));
            }
            finally
            {
                DashboardFactory.DeleteFolder(keys);
            }
        }

        [Fact]
        public async Task SessionSurvivesARestart()
        {
            var db = await fixture.ReadyAsync();
            var keys = DashboardFactory.NewKeysFolder();

            try
            {
                string key;

                await using (var before = new DashboardFactory(db, new DashboardFactoryOptions { KeysFolder = keys }))
                    key = await SignInSteps.SignedInAsync(before, before.Visitor(), TestUsers.Owner);

                await using var after = new DashboardFactory(db, new DashboardFactoryOptions { KeysFolder = keys });
                var browser = after.Visitor();
                browser.Jar.Add(DashboardFactory.PublicAddress, new Cookie(DashboardAuth.SessionCookie, key) { Secure = true });

                Assert.Equal(HttpStatusCode.OK, await AdminAsync(browser));
            }
            finally
            {
                DashboardFactory.DeleteFolder(keys);
            }
        }

        [Fact]
        public async Task KeysAreKeptInTheConfiguredFolder()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            await SignInSteps.StartAsync(factory.Visitor());

            Assert.NotEmpty(Directory.GetFiles(factory.KeysFolder, "key-*.xml"));
            Assert.Empty(Directory.GetFiles(factory.KeysFolder, ".write-check"));
            Assert.Equal(DashboardHosting.ApplicationName, factory.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("abc")]
        public async Task ForgedOrMalformedCookieIsAnonymous(string? value)
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var browser = factory.Visitor();
            browser.Jar.Add(DashboardFactory.PublicAddress, new Cookie(DashboardAuth.SessionCookie, value ?? SessionKeys.New()) { Secure = true });

            Assert.Equal("/signin?returnUrl=%2Fadmin", await AdminLocationAsync(browser));
            Assert.Empty(await db.SessionsAsync());
        }

        // The store never accepts a ticket for someone who isn't on the lists, whatever calls it
        [Fact]
        public async Task StoreRefusesSomeoneOffTheLists()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var store = factory.Services.GetRequiredService<DbTicketStore>();
            var visitor = new SignedInUser(TestUsers.Visitor.Id, "Test Visitor", true);
            var ticket = new AuthenticationTicket(visitor.ToPrincipal(), DashboardAuth.CookieScheme);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.StoreAsync(ticket, new DefaultHttpContext(), Token));

            var admin = new SignedInUser(TestUsers.Admin.Id, "Test Admin", false);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.StoreAsync(new AuthenticationTicket(admin.ToPrincipal(), DashboardAuth.CookieScheme), new DefaultHttpContext(), Token));
        }
    }
}
