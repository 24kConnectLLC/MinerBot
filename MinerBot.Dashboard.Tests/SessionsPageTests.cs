using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Status;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // The Sessions and security page, the Owner's alone: who is signed in with their addresses and browsers, ending one
    // session or every other one with a sign-in from the last 15 minutes, each recorded without the ended session's address,
    // and the dashboard's checks of itself as its own database login
    [Collection("Database")]
    public partial class SessionsPageTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        const string Page = "/admin/sessions";

        static string Text(string html) => Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(html), "<[^>]+>", " "), "\\s+", " ");

        static async Task<TestBrowser> SignedInAsync(DashboardFactory factory, TestUser user, string address, string? browser = null)
        {
            var visitor = factory.Visitor(address);

            if (browser != null)
                visitor.Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", browser);

            await SignInSteps.SignedInAsync(factory, visitor, user);
            return visitor;
        }

        // A session's name on the page and in its form, its key's hash in small-letter hex
        static string HashOf(TestBrowser browser) => Convert.ToHexStringLower(DashboardSessionData.HashOf(browser.SessionKey!));

        // One of the page's forms as a browser sends it from the dashboard, with the page's token
        static async Task<HttpResponseMessage> PostAsync(TestBrowser browser, string handler, params (string Name, string Value)[] fields)
        {
            var token = SignInSteps.FormToken(await browser.Client.GetStringAsync(Page, Token));
            return await SignInSteps.PostAsync(browser, $"{Page}?handler={handler}", token, origin: DashboardFactory.PublicOrigin,
                fields: fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));
        }

        static void Done(HttpResponseMessage response, string done)
        {
            Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"The form answered {response.StatusCode}");
            Assert.Equal($"{Page}?done={done}", SignInSteps.PathOf(response.Headers.Location));
        }

        static async Task<HttpStatusCode> AdminAsync(TestBrowser browser) => (await browser.Client.GetAsync("/admin", Token)).StatusCode;

        static async Task<List<AuditEntry>> EndingsAsync(MariaDbFixture db) =>
            [.. (await db.AuditAsync()).Where(entry => entry.action is AuditWords.Ended or AuditWords.EndedOthers)];

        // An Admin's GET goes to /denied, and so do their forms, even with a good token, ending nothing. Fails if the page
        // loses the Owner policy.
        [Fact]
        public async Task OnlyTheOwnerOpensIt()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.180");
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.181");

            var get = await admin.Client.GetAsync(Page, Token);
            Assert.Equal(HttpStatusCode.Redirect, get.StatusCode);
            Assert.StartsWith("/denied", SignInSteps.PathOf(get.Headers.Location));

            var token = SignInSteps.FormToken(await admin.Client.GetStringAsync("/admin", Token));

            foreach (var (handler, fields) in new (string, KeyValuePair<string, string>[])[] { ("End", [new("session", HashOf(owner))]), ("EndOthers", []) })
            {
                var post = await SignInSteps.PostAsync(admin, $"{Page}?handler={handler}", token, origin: DashboardFactory.PublicOrigin, fields: fields);
                Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
                Assert.StartsWith("/denied", SignInSteps.PathOf(post.Headers.Location));
            }

            Assert.Equal(2, (await db.SessionsAsync()).Count);
            Assert.Empty(await EndingsAsync(db));
            Assert.Equal(HttpStatusCode.OK, await AdminAsync(owner));
        }

        // The sessions in use, the Owner's marked as this one, each with its person, role on the lists now, times, address and
        // browser; an idle one and one past its 12 hours aren't listed, and one whose person is off the lists says it ends at
        // its next click. No key, and nothing of a ticket.
        [Fact]
        public async Task ItListsLiveSessionsWithTheirAddresses()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.182", "Owner Browser 1.0");
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.183", "Admin Browser 2.0");
            var idle = await SignedInAsync(factory, TestUsers.SecondAdmin, "198.51.100.184", "Idle Browser");
            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.SessionTable} SET last_seen_at = @at WHERE user_id = @id",
                new { at = now.AddMinutes(-31), id = TestUsers.SecondAdmin.Id });

            byte[] ticket = [1, 2, 3, 4, 5, 6, 7, 8];
            await db.RootExecuteAsync($@"INSERT INTO {MariaDbFixture.SessionTable} (id_hash, user_id, user_name, created_at, last_seen_at, expires_at, ip, user_agent, ticket)
                VALUES (@unlisted, @visitor, 'Test Visitor', @created, @created, @expires, '192.0.2.77', 'Old Browser', @ticket),
                       (@ended, @visitor, 'Test Visitor', @old, @now, @now, '192.0.2.78', 'Ended Browser', @ticket)",
                new { unlisted = RandomNumberGenerator.GetBytes(32), ended = RandomNumberGenerator.GetBytes(32), visitor = TestUsers.Visitor.Id, created = now.AddMinutes(-20),
                    expires = now.AddHours(11), old = now.AddHours(-13), now, ticket });

            var html = await owner.Client.GetStringAsync(Page, Token);
            var text = Text(html);

            // The sessions' own table; the changes to sessions below it list every sign-in, with its address
            var listed = html[html.IndexOf("id=\"signed-in\"", StringComparison.Ordinal)..html.IndexOf("id=\"changes\"", StringComparison.Ordinal)];

            Assert.Contains("<title>Sessions and security - MinerBot dashboard</title>", html);
            Assert.Contains(SessionWords.Intro, text);
            Assert.Contains("198.51.100.182", listed);
            Assert.Contains("198.51.100.183", listed);
            Assert.Contains("192.0.2.77", listed);
            Assert.DoesNotContain("198.51.100.184", listed);
            Assert.DoesNotContain("Idle Browser", html);
            Assert.DoesNotContain("192.0.2.78", html);
            Assert.DoesNotContain("Ended Browser", html);

            Assert.Contains($"Test Owner {TestUsers.Owner.Id} Owner {TimeWords.Utc(now)} Within the last 15 minutes About {TimeWords.Utc(now)} {TimeWords.Utc(now.AddMinutes(30))} "
                + "198.51.100.182 Owner Browser 1.0 " + SessionWords.ThisSession, text);
            Assert.Contains($"Test Admin {TestUsers.Admin.Id} Admin {TimeWords.Utc(now)} Within the last 15 minutes", text);
            Assert.Contains($"Test Visitor {TestUsers.Visitor.Id} No longer on the lists: ends at its next click {TimeWords.Utc(now.AddMinutes(-20))} About", text);
            Assert.Contains($"<a href=\"/admin/audit?person={TestUsers.Admin.Id}\" class=\"address\">{TestUsers.Admin.Id}</a>", html);
            Assert.Contains($"<input type=\"hidden\" name=\"session\" value=\"{HashOf(admin)}\" />", html);
            Assert.DoesNotContain($"value=\"{HashOf(owner)}\"", html);
            Assert.Equal(1, Regex.Count(text, Regex.Escape(SessionWords.ThisSession)));

            foreach (var key in new[] { owner.SessionKey!, admin.SessionKey!, idle.SessionKey! })
                Assert.DoesNotContain(key, html, StringComparison.Ordinal);

            Assert.DoesNotContain(Convert.ToBase64String(ticket), html, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToHexString(ticket), html, StringComparison.OrdinalIgnoreCase);
            PageTests.AssertAccessible("the Sessions page", html);
            PageTests.AssertTablesTakeTheKeyboard("the Sessions page", html);
            PageTests.AssertTimesStayWhole("the Sessions page", html);
            Assert.False(PageTests.LoadsRefreshScript(html), "The Sessions page loads refresh.js");
        }

        // An ended session's cookie stops at its next request, which goes to the sign-in, and its next form writes nothing;
        // the same person's other session goes on. Fails if a session is ended by its person.
        [Fact]
        public async Task EndingASessionStopsItsCookieAtTheNextRequest()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.185");
            var ended = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.186");
            var other = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.187");
            var controls = await RequestSteps.ControlsAsync(ended);

            Done(await PostAsync(owner, "End", ("session", HashOf(ended))), "ended");

            var next = await ended.Client.GetAsync("/admin", Token);
            Assert.Equal(HttpStatusCode.Redirect, next.StatusCode);
            Assert.Equal("/signin?returnUrl=%2Fadmin", SignInSteps.PathOf(next.Headers.Location));

            var post = await RequestSteps.PostControlAsync(ended, controls, ControlWords.ClearCaches);
            Assert.Equal(HttpStatusCode.SeeOther, post.StatusCode);
            Assert.Equal("/signin?ended=1&returnUrl=%2Fadmin%2Fbot%2Fcontrols", SignInSteps.PathOf(post.Headers.Location));
            Assert.Empty(await db.SentAsync());

            Assert.Equal(HttpStatusCode.OK, await AdminAsync(other));
            Assert.Equal(HttpStatusCode.OK, await AdminAsync(owner));

            var audit = Assert.Single(await EndingsAsync(db));
            Assert.Equal((AuditWords.Ended, AuditOutcome.Ok, (ulong?)TestUsers.Owner.Id, AuditRole.Owner, "session", (string?)null, "198.51.100.185"),
                (audit.action, audit.outcome, audit.actor_id, audit.actor_role, audit.target_type!, audit.target_id, audit.ip!));
            Assert.StartsWith($"The session of Test Admin (Discord ID {TestUsers.Admin.Id}), signed in at ", audit.detail);
            Assert.Contains(SessionWords.Done("ended")!, Text(await owner.Client.GetStringAsync($"{Page}?done=ended", Token)));
        }

        // Every other session ends, the Admins' and the Owner's other devices' alike, and this one goes on, with one audit row
        // that counts them. Fails if this session ends too.
        [Fact]
        public async Task EndingEveryOtherSessionKeepsOnlyYours()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.188");
            var phone = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.189");
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.190");
            var second = await SignedInAsync(factory, TestUsers.SecondAdmin, "198.51.100.191");

            Done(await PostAsync(owner, "EndOthers"), "ended-others");

            Assert.Equal(HttpStatusCode.OK, await AdminAsync(owner));

            foreach (var gone in new[] { phone, admin, second })
                Assert.Equal(HttpStatusCode.Redirect, (await gone.Client.GetAsync("/admin", Token)).StatusCode);

            Assert.True(Assert.Single(await db.SessionsAsync()).IsFor(owner.SessionKey!), "The session left isn't the one that ended the others");

            var audit = Assert.Single(await EndingsAsync(db));
            Assert.Equal((AuditWords.EndedOthers, AuditOutcome.Ok, "session"), (audit.action, audit.outcome, audit.target_type!));
            Assert.StartsWith("3 sessions, of Discord users ", audit.detail);

            foreach (var person in new[] { TestUsers.Owner, TestUsers.Admin, TestUsers.SecondAdmin })
                Assert.Contains(person.IdText, audit.detail);

            Assert.Contains(SessionWords.Done("ended-others")!, Text(await owner.Client.GetStringAsync($"{Page}?done=ended-others", Token)));
        }

        // This session can't be ended here, but by signing out: it says so, and nothing ends
        [Fact]
        public async Task YourOwnSessionIsntEndedHere()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.192");

            var refused = await PostAsync(owner, "End", ("session", HashOf(owner)));
            var html = await refused.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains(SessionWords.YourOwn, Text(html));
            Assert.Contains("<div class=\"notice notice-problem\" role=\"alert\">", html);
            Assert.Single(await db.SessionsAsync());
            Assert.Empty(await EndingsAsync(db));
            Assert.Equal(HttpStatusCode.OK, await AdminAsync(owner));
            PageTests.AssertAccessible("the Sessions page refusing to end its own session", html);
        }

        // Ending needs a sign-in from the last 15 minutes: past them, the page says so with the way to sign in again, both
        // forms are refused with a 403 and recorded, and nothing ends. Fails if ending needs no fresh sign-in.
        [Fact]
        public async Task EndingNeedsAFreshSignIn()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.193");
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.194");
            var signedIn = factory.Clock.GetUtcNow().UtcDateTime;

            factory.Clock.Advance(TimeSpan.FromMinutes(16));
            var stale = await owner.Client.GetStringAsync(Page, Token);

            Assert.Contains(SessionWords.NeedsFresh(new DateTimeOffset(signedIn)), Text(stale));
            Assert.Contains(WebUtility.HtmlEncode(FreshSignIn.SignInAgainLink(Page)), stale);

            foreach (var (handler, action) in new[] { ("End", AuditWords.Ended), ("EndOthers", AuditWords.EndedOthers) })
            {
                var refused = await PostAsync(owner, handler, ("session", HashOf(admin)));
                var html = await refused.Content.ReadAsStringAsync(Token);

                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                Assert.Contains(SessionWords.NotFresh(new DateTimeOffset(signedIn)), Text(html));
                Assert.Contains(WebUtility.HtmlEncode(FreshSignIn.SignInAgainLink(Page)), html);
                PageTests.AssertAccessible("an ending refused for an old sign-in", html);

                var audit = Assert.Single(await EndingsAsync(db), entry => entry.action == action);
                Assert.Equal((AuditOutcome.Refused, RequestWords.NotFreshDetail, "198.51.100.193", (ulong?)TestUsers.Owner.Id), (audit.outcome, audit.detail!, audit.ip!, audit.actor_id));
            }

            Assert.Equal(2, (await db.SessionsAsync()).Count);
            Assert.Equal(HttpStatusCode.OK, await AdminAsync(admin));
        }

        // Ending sessions needs a fresh sign-in whether requests to the bot are on or off, so the way to sign in again stays,
        // but while they're off its note doesn't promise the usual sign-in message, which the page's own check says isn't
        // sent then. Fails if the note promises the message whatever.
        [Fact]
        public async Task WithRequestsOffSigningInAgainPromisesNoMessage()
        {
            var db = await fixture.ReadyAsync();
            await using var on = new DashboardFactory(db);
            var owner = await SignedInAsync(on, TestUsers.Owner, "198.51.100.155");

            on.Clock.Advance(TimeSpan.FromMinutes(16));
            Assert.Contains(ControlWords.SignInAgainNote, Text(await owner.Client.GetStringAsync(Page, Token)));

            await using var off = new DashboardFactory(db, new DashboardFactoryOptions { Settings = new Dictionary<string, string?> { ["Dashboard:RequestSigningKey"] = "" } });
            var offOwner = await SignedInAsync(off, TestUsers.Owner, "198.51.100.156");

            off.Clock.Advance(TimeSpan.FromMinutes(16));
            var html = await offOwner.Client.GetStringAsync(Page, Token);
            var text = Text(html);

            Assert.Contains(WebUtility.HtmlEncode(FreshSignIn.SignInAgainLink(Page)), html);
            Assert.Contains(SessionWords.SignInAgainNoMessage, text);
            Assert.DoesNotContain("sends you the usual sign-in message", text);
            Assert.Contains("Off: the dashboard has no request-signing key, so sign-ins send the owner no message.", text);
            PageTests.AssertAccessible("the Sessions page with requests off", html);
        }

        // A session that ended before the form came, as from a page drawn earlier, says so, with no audit row
        [Fact]
        public async Task EndingASessionThatsGoneSaysSo()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.195");

            Done(await PostAsync(owner, "End", ("session", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)))), "gone");

            Assert.Contains(SessionWords.Done("gone")!, Text(await owner.Client.GetStringAsync($"{Page}?done=gone", Token)));
            Assert.Empty(await EndingsAsync(db));
            Assert.Single(await db.SessionsAsync());
        }

        // A session named by anything but 64 small-letter hex digits is a form someone made up
        [Fact]
        public async Task AMadeUpSessionIdIsABadRequest()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.196");
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.197");
            var hash = HashOf(admin);

            foreach (var made in new[] { "", "abc", hash.ToUpperInvariant(), hash[..63], hash + "0", hash[..62] + "zz", admin.SessionKey! })
                Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(owner, "End", ("session", made))).StatusCode);

            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(owner, "End")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(owner, "Explode")).StatusCode);
            Assert.Equal(2, (await db.SessionsAsync()).Count);
            Assert.Empty(await EndingsAsync(db));
        }

        // The audit row of an ending names the ended session's person and sign-in, never its address or browser, which
        // Admins would read on the audit page; the address it has is the Owner's own. Fails if the detail holds the address.
        [Fact]
        public async Task TheEndedSessionsAddressIsntInItsAuditRow()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.198");
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.199", "Secret Browser 3.0");
            var second = await SignedInAsync(factory, TestUsers.SecondAdmin, "198.51.100.200", "Other Secret Browser");

            Done(await PostAsync(owner, "End", ("session", HashOf(admin))), "ended");
            Done(await PostAsync(owner, "EndOthers"), "ended-others");

            var endings = await EndingsAsync(db);
            Assert.Equal(2, endings.Count);

            foreach (var row in endings)
            {
                var written = string.Join("\n", row.detail, row.before_json, row.after_json, row.target_id);

                foreach (var secret in new[] { "198.51.100.199", "198.51.100.200", "Secret Browser" })
                    Assert.DoesNotContain(secret, written, StringComparison.Ordinal);

                Assert.Equal("198.51.100.198", row.ip);
            }

            Assert.Contains("1 session, of Discord user " + TestUsers.SecondAdmin.IdText, endings[1].detail);
            Assert.Equal(HttpStatusCode.Redirect, (await second.Client.GetAsync("/admin", Token)).StatusCode);
        }

        // A browser's text is visitor text: encoded, with its hidden characters shown as their codes
        [Fact]
        public async Task BrowserTextIsEncoded()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.201");
            await SignedInAsync(factory, TestUsers.Admin, "198.51.100.202", "Mozilla <script>alert(1)</script> x\u202Ey");

            var html = await owner.Client.GetStringAsync(Page, Token);

            Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<bdi class=\"revealed\">Mozilla &lt;script&gt;alert(1)&lt;/script&gt; x<mark class=\"hidden-char\" title=\"right-to-left override\">[U&#x2B;202E]</mark>y</bdi>", html);
            Assert.DoesNotContain("\u202E", html, StringComparison.Ordinal);
            PageTests.AssertAccessible("the Sessions page with a browser's made-up text", html);
        }

        // With both connection strings on the dashboard's own login, made with exactly the rights deploy gives it, the
        // rights, keys, structure and requests checks pass, Access says it's off, and the page has the sign-in's message.
        // Without the right on schema_version, the structure check says so in its own words. Fails if the rights file loses
        // its one right on schema_version, or MariaDB's refusal of the read isn't the one the check knows.
        [Fact]
        public async Task SelfChecksWithTheDashboardsOwnLogin()
        {
            var db = await fixture.ReadyBotAsync();

            await DashboardLogin.AsDashboardAsync(db, async password =>
            {
                await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
                {
                    SiteConnection = DashboardLogin.ConnectionAs(db, password, MariaDbFixture.SiteDatabase),
                    BotConnection = DashboardLogin.ConnectionAs(db, password, MariaDbFixture.BotDatabase)
                });
                var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.203");

                var response = await owner.Client.GetAsync(Page, Token);
                var html = await response.Content.ReadAsStringAsync(Token);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                string Check(string name)
                {
                    var row = Regex.Match(html, $"<th scope=\"row\">{Regex.Escape(name)}</th>\\s*<td><span class=\"marker ([a-z-]+)\">([A-Za-z]+)</span></td>\\s*<td>(.*?)</td>",
                        RegexOptions.Singleline);
                    Assert.True(row.Success, $"The page has no check called {name}");
                    return $"{row.Groups[2].Value}: {WebUtility.HtmlDecode(row.Groups[3].Value)}";
                }

                var keys = TimeWords.Count(Directory.GetFiles(factory.KeysFolder, "key-*.xml").Length, "sign-in key");

                Assert.Equal("OK: The login has exactly the rights this version uses, with at most 15 connections and 20 seconds a statement.", Check(SelfChecks.Rights));
                Assert.Equal($"OK: Writable: {factory.KeysFolder}, with {keys}, and so is the temporary folder each new key passes through.", Check(SelfChecks.Keys));
                Assert.Equal("OK: Up to date: migrations 1 to 4.", Check(SelfChecks.Structure));
                Assert.Equal("Note: Off: /admin opens with a Discord sign-in alone. The plan recommends Access.", Check(SelfChecks.Access));
                Assert.Equal("OK: On: the dashboard signs its requests.", Check(SelfChecks.Requests));
                Assert.Equal("Note: The bot hasn't recorded a direct message to the owner yet.", Check(SelfChecks.DirectMessages));
                Assert.StartsWith("OK: On: every sign-in asks the bot to send the owner a direct message. 1 sign-in asked for a message in the last hour, of the bot's 20. "
                    + "The latest, request ", Check(SelfChecks.SigninMessages));
                Assert.EndsWith(": Queued.", Check(SelfChecks.SigninMessages));
                PageTests.AssertAccessible("the Sessions page as the dashboard's own login", html);

                // Without that right, as after a deploy whose rights step didn't run, the rights check names it as missing, and
                // the structure check says what it can't read and how to put it back, rather than MariaDB's error number
                await db.RootExecuteAsync($"REVOKE SELECT ON {MariaDbFixture.BotDatabase}.schema_version FROM {DashboardLogin.Grantee}");
                html = await owner.Client.GetStringAsync(Page, Token);

                Assert.Equal($"Problem: The login lacks rights this version uses: table SELECT {MariaDbFixture.BotDatabase} schema_version. Pages that need them fail. "
                    + "./setup-dashboard.sh --no-questions applies them again.", Check(SelfChecks.Rights));
                Assert.Equal("Problem: The login can't read schema_version. ./setup-dashboard.sh --no-questions applies its rights again.", Check(SelfChecks.Structure));
            });
        }
    }
}
