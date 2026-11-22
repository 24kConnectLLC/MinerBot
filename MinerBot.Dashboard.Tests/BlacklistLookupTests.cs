using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Blacklist;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Requests;
using static MinerBot.Dashboard.Tests.Support.BlacklistSteps;

namespace MinerBot.Dashboard.Tests
{
    // A custom profile link on the blacklist's add page: the bot is asked which account it is, the page comes back with
    // every box kept and the lookup's box, and the next press adds the account the bot's answer names, read on the server
    // from this person's own lookup of that name from the last 30 minutes, never from anything the browser sends
    [Collection("Database")]
    public class BlacklistLookupTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        const string Link = "https://steamcommunity.com/id/gabelogannewell";
        const string Name = "gabelogannewell";

        // The answer the bot gives for that link, and the account the table holds for it
        const uint Account = 22202;
        const string Answer = "That link is account [U:1:22202].";

        static async Task<TestBrowser> SignedInAsync(DashboardFactory factory, TestUser user, string address)
        {
            var browser = factory.Visitor(address);
            await SignInSteps.SignedInAsync(factory, browser, user);
            return browser;
        }

        static string Text(string html) => Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(html), "<[^>]+>", " "), "\\s+", " ");

        // The add form as a browser sends it, with the boxes a person typed, its token, a key and the lookup given
        static async Task<HttpResponseMessage> AddAsync(TestBrowser browser, string steamId = Link, string? lookup = null, string? key = null,
            string notes = "Kept Notes", params (string Name, string Value)[] more)
        {
            var token = SignInSteps.FormToken(await browser.Client.GetStringAsync("/admin/blacklist/add", Token));
            var fields = new List<KeyValuePair<string, string>>
            {
                new("steamid", steamId), new("name", "Kept Name"), new("reason", "Kept Reason"), new("expires", "2027-01-02"), new("notes", notes),
                new("formkey", key ?? RequestFields.NewKey()), new("lookup", lookup ?? "")
            };

            fields.AddRange(more.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));
            return await SignInSteps.PostAsync(browser, "/admin/blacklist/add", token, origin: DashboardFactory.PublicOrigin, fields: fields);
        }

        static async Task<List<BotRequestRow>> LookupsAsync(MariaDbFixture db) => (await db.SentAsync()).Where(row => row.kind == RequestKinds.SteamLookup).ToList();

        // The page a lookup answers with: 200, the boxes as typed, the lookup's number in its hidden field, and its box
        static async Task<(string Html, ulong Lookup)> LookedUpAsync(HttpResponseMessage response, string words)
        {
            var html = await response.Content.ReadAsStringAsync(Token);

            Assert.True(response.StatusCode == HttpStatusCode.OK, $"The add answered {response.StatusCode}");
            Assert.Contains(words, Text(html));
            Assert.Equal("Kept Name", BoxValue(html, "name"));
            Assert.Equal("Kept Reason", BoxValue(html, "reason"));
            Assert.Equal("2027-01-02", BoxValue(html, "expires"));
            Assert.Equal("Kept Notes", BoxValue(html, "notes"));
            Assert.Contains("id=\"result\"", html);
            Assert.DoesNotContain("Check again", html);

            return (html, ulong.Parse(BoxValue(html, "lookup")!, CultureInfo.InvariantCulture));
        }

        // As the bot answers a lookup: taken a second after it was asked for, and done with the account
        static async Task AnsweredAsync(MariaDbFixture db, ulong id, DateTime at, string result = Answer, ulong? number = Account)
        {
            await RequestSteps.ClaimAsync(db, id, at.AddSeconds(1));
            await RequestSteps.FinishAsync(db, id, RequestStates.Done, result, at.AddSeconds(2), number);
        }

        // A custom link asks the bot, with the name alone: the page comes back with every box as typed, the request's number
        // in a hidden field and its box, which refreshes itself, and nothing is added. The name is only ever in its own box,
        // never in a log line.
        [Fact]
        public async Task ACustomLinkStartsALookupAndKeepsTheOtherBoxes()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.160");

            var (html, lookup) = await LookedUpAsync(await AddAsync(admin), BlacklistWords.LookupSent);

            var row = Assert.Single(await LookupsAsync(db));
            Assert.Equal((row.id, Name, TestUsers.Admin.Id, RequestRoles.Admin, (ulong?)null), (lookup, row.argument, row.actor_id, row.actor_role, row.guild_id));
            Assert.Equal(Link, BoxValue(html, "steamid"));
            Assert.Contains($"Look up a Steam profile link, request {row.id}, asked for by Test Admin (Admin)", Text(html));
            Assert.Contains(BlacklistWords.LookupThen, Text(html));
            Assert.Contains($"data-refresh=\"/admin/requests/{row.id}?handler=result\"", html);
            Assert.True(PageTests.LoadsRefreshScript(html), "The page with the lookup's box doesn't load refresh.js");
            Assert.Equal(1, Regex.Count(html, Name));
            Assert.Equal(0, await db.EntryCountAsync());
            Assert.False(factory.Logs.Text.Contains(Name, StringComparison.Ordinal), "A log line holds the custom link's name");
            PageTests.AssertAccessible("the add page with a lookup's box", html);

            var audit = Assert.Single(await db.AuditAsync(), entry => entry.action == RequestKinds.SteamLookup);
            Assert.Equal(("request", row.id.ToString(CultureInfo.InvariantCulture)), (audit.target_type!, audit.target_id!));
        }

        // Once the bot has answered, the form sent again adds the account its answer names, whatever account the browser
        // sends beside it, and leads to the entry's page with one audit row. Fails if the add takes an account from the form.
        [Fact]
        public async Task TheAddUsesTheBotsAccountNeverTheBrowsers()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.161");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var (_, lookup) = await LookedUpAsync(await AddAsync(admin), BlacklistWords.LookupSent);
            await AnsweredAsync(db, lookup, now);

            var added = await AddAsync(admin, lookup: lookup.ToString(CultureInfo.InvariantCulture), more: [("account", "12345"), ("result_number", "12345")]);

            Assert.Equal(HttpStatusCode.Redirect, added.StatusCode);
            Assert.Equal($"/admin/blacklist/{Account}?done=added", SignInSteps.PathOf(added.Headers.Location));
            Assert.Equivalent(Entry(Account, "Kept Name", "Kept Reason", new DateTime(2027, 1, 2), "Kept Notes"), await db.EntryAsync(Account), strict: true);
            Assert.Null(await db.EntryAsync(12345));
            Assert.Equal("blacklist.add", Assert.Single(await db.BlacklistAuditAsync()).action);
            Assert.Single(await LookupsAsync(db));

            // Sent again, it's already there, and says so, adding nothing more
            var again = await AddAsync(admin, lookup: lookup.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Contains(BlacklistWords.AlreadyAdded, Text(await again.Content.ReadAsStringAsync(Token)));
            Assert.Single(await db.BlacklistAuditAsync(), entry => entry.outcome == "ok");
        }

        // Another person's lookup, though done, is a form someone made up: a 400 that adds nothing. Fails if the lookup's
        // person isn't checked.
        [Fact]
        public async Task AnotherPersonsLookupIsARefusedForm()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.162");
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.163");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var (_, lookup) = await LookedUpAsync(await AddAsync(owner), BlacklistWords.LookupSent);
            await AnsweredAsync(db, lookup, now);

            Assert.Equal(HttpStatusCode.BadRequest, (await AddAsync(admin, lookup: lookup.ToString(CultureInfo.InvariantCulture))).StatusCode);
            Assert.Equal(0, await db.EntryCountAsync());
            Assert.Single(await LookupsAsync(db));
        }

        // Another kind's request, such as a test message whose number is a message's, is a form someone made up too, and so
        // is a lookup that isn't a request's number. Fails if the request's kind isn't checked.
        [Fact]
        public async Task AnotherKindsRequestIsARefusedForm()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.164");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var test = await RequestSteps.AddAsync(db, factory, RequestKinds.GuildTestMessage, now, TestUsers.Admin, argument: null);
            await AnsweredAsync(db, test, now, "Sent a test message.", Account);

            Assert.Equal(HttpStatusCode.BadRequest, (await AddAsync(admin, lookup: test.ToString(CultureInfo.InvariantCulture))).StatusCode);

            foreach (var made in new[] { "abc", "0", "-1", "1.5", "18446744073709551616" })
                Assert.Equal(HttpStatusCode.BadRequest, (await AddAsync(admin, lookup: made)).StatusCode);

            Assert.Equal(0, await db.EntryCountAsync());
            Assert.Empty(await LookupsAsync(db));
        }

        // A lookup of one link never adds another's account: the box changed to another link asks the bot about that one.
        // Fails if the lookup's name isn't compared.
        [Fact]
        public async Task ALookupOfAnotherLinkStartsAnother()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.165");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var (_, lookup) = await LookedUpAsync(await AddAsync(admin), BlacklistWords.LookupSent);
            await AnsweredAsync(db, lookup, now);

            var (_, other) = await LookedUpAsync(await AddAsync(admin, "https://steamcommunity.com/id/someone_else", lookup.ToString(CultureInfo.InvariantCulture)),
                BlacklistWords.LookupSent);

            Assert.NotEqual(lookup, other);
            Assert.Equal([Name, "someone_else"], (await LookupsAsync(db)).Select(row => row.argument));
            Assert.Equal(0, await db.EntryCountAsync());
        }

        // A lookup more than 30 minutes old is asked again, since a custom link can move to another account. A page viewed
        // between keeps the session going. Fails if the age isn't checked.
        [Fact]
        public async Task AnOldLookupIsAskedAgain()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.166");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var (_, lookup) = await LookedUpAsync(await AddAsync(admin), BlacklistWords.LookupSent);
            await AnsweredAsync(db, lookup, now);

            factory.Clock.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync("/admin/blacklist", Token)).StatusCode);
            factory.Clock.Advance(TimeSpan.FromMinutes(11));

            var (_, again) = await LookedUpAsync(await AddAsync(admin, lookup: lookup.ToString(CultureInfo.InvariantCulture)), BlacklistWords.LookupSent);

            Assert.NotEqual(lookup, again);
            Assert.Equal(2, (await LookupsAsync(db)).Count);
            Assert.Equal(0, await db.EntryCountAsync());
        }

        // A lookup the bot hasn't answered yet, waiting or taken, says to press again in a few seconds, with its box, and
        // neither adds nor asks again
        [Fact]
        public async Task ALookupStillRunningSaysWait()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.167");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var (_, lookup) = await LookedUpAsync(await AddAsync(admin), BlacklistWords.LookupSent);
            var (waitingHtml, waiting) = await LookedUpAsync(await AddAsync(admin, lookup: lookup.ToString(CultureInfo.InvariantCulture)), BlacklistWords.LookupWaiting);

            Assert.Equal(lookup, waiting);
            Assert.Contains("Queued", Text(waitingHtml));

            await RequestSteps.ClaimAsync(db, lookup, now.AddSeconds(1));
            var (runningHtml, running) = await LookedUpAsync(await AddAsync(admin, lookup: lookup.ToString(CultureInfo.InvariantCulture)), BlacklistWords.LookupWaiting);

            Assert.Equal(lookup, running);
            Assert.Contains("Running", Text(runningHtml));
            Assert.Single(await LookupsAsync(db));
            Assert.Equal(0, await db.EntryCountAsync());
        }

        // A field to correct after the lookup, such as notes with a pasted tab, which the browser sends and the server refuses,
        // keeps the lookup in the form, so the press after the correction adds the account the bot named rather than asking
        // the bot again. Fails if a form the fields send back drops its lookup.
        [Fact]
        public async Task AFieldToCorrectKeepsTheLookup()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.172");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var (_, lookup) = await LookedUpAsync(await AddAsync(admin), BlacklistWords.LookupSent);
            await AnsweredAsync(db, lookup, now);

            var refused = await AddAsync(admin, lookup: lookup.ToString(CultureInfo.InvariantCulture), notes: "Seen in\tlobby");
            var html = await refused.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains(BlacklistWords.Problem(BlacklistField.Notes, TextProblem.ControlCharacter), Text(html));
            Assert.Equal(lookup.ToString(CultureInfo.InvariantCulture), BoxValue(html, "lookup"));
            Assert.Equal(0, await db.EntryCountAsync());

            // Corrected, and sent as the page drew it
            var added = await AddAsync(admin, lookup: BoxValue(html, "lookup"), key: BoxValue(html, "formkey"));

            Assert.Equal(HttpStatusCode.Redirect, added.StatusCode);
            Assert.Equal($"/admin/blacklist/{Account}?done=added", SignInSteps.PathOf(added.Headers.Location));
            Assert.Equivalent(Entry(Account, "Kept Name", "Kept Reason", new DateTime(2027, 1, 2), "Kept Notes"), await db.EntryAsync(Account), strict: true);
            Assert.Single(await LookupsAsync(db));
        }

        // Steam's answer that no profile has that link adds nothing, and says so by the box
        [Fact]
        public async Task ALookupThatFoundNoProfileAddsNothing()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.168");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var (_, lookup) = await LookedUpAsync(await AddAsync(admin), BlacklistWords.LookupSent);
            await AnsweredAsync(db, lookup, now, "Steam has no profile with that custom link.", null);

            var none = await AddAsync(admin, lookup: lookup.ToString(CultureInfo.InvariantCulture));
            var html = await none.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
            Assert.Contains(BlacklistWords.LookupNoProfile, Text(html));
            Assert.Contains("<p id=\"steamid-problem\" class=\"field-problem\">", html);
            Assert.Equal("Kept Name", BoxValue(html, "name"));
            Assert.Equal(0, await db.EntryCountAsync());
            Assert.Single(await LookupsAsync(db));
            PageTests.AssertAccessible("a lookup that found no profile", html);
        }

        // A lookup that failed, or expired without the bot taking it, is asked again rather than used
        [Fact]
        public async Task AFailedOrExpiredLookupStartsAnother()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.169");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var (_, failed) = await LookedUpAsync(await AddAsync(admin), BlacklistWords.LookupSent);
            await RequestSteps.ClaimAsync(db, failed, now.AddSeconds(1));
            await RequestSteps.FinishAsync(db, failed, RequestStates.Failed, "Steam didn't answer the lookup (TimeoutException).", now.AddSeconds(2));

            var (_, second) = await LookedUpAsync(await AddAsync(admin, lookup: failed.ToString(CultureInfo.InvariantCulture)), BlacklistWords.LookupSent);
            Assert.NotEqual(failed, second);

            // Left waiting past its expiry and the grace after it
            factory.Clock.Advance(RequestKinds.UsualExpiry + RequestViews.Waits.QueuedGrace + TimeSpan.FromSeconds(1));
            var (_, third) = await LookedUpAsync(await AddAsync(admin, lookup: second.ToString(CultureInfo.InvariantCulture)), BlacklistWords.LookupSent);

            Assert.NotEqual(second, third);
            Assert.Equal(3, (await LookupsAsync(db)).Count);
            Assert.Equal(0, await db.EntryCountAsync());
        }

        // The form sent twice with the same key, as a reload of the page sends it, finds the same request and writes no other
        [Fact]
        public async Task ReloadingTheLookupWritesNoSecondRequest()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.170");
            var key = RequestFields.NewKey();

            var (firstHtml, first) = await LookedUpAsync(await AddAsync(admin, key: key), BlacklistWords.LookupSent);
            var (secondHtml, second) = await LookedUpAsync(await AddAsync(admin, key: key), BlacklistWords.LookupSent);

            Assert.Equal(first, second);
            Assert.Single(await LookupsAsync(db));
            Assert.NotEqual(key, BoxValue(firstHtml, "formkey"));
            Assert.NotEqual(BoxValue(firstHtml, "formkey"), BoxValue(secondHtml, "formkey"));
        }

        // While requests to the bot are off, a custom link can't be looked up, and the page says so by the box, with what to
        // enter instead, writing nothing; the name stays in its box alone, and in no log line
        [Fact]
        public async Task LookupWithRequestsOffSaysSo()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions { Settings = new Dictionary<string, string?> { ["Dashboard:RequestSigningKey"] = "" } });
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.171");

            foreach (var link in new[] { Link, "steamcommunity.com/id/gabelogannewell/" })
            {
                var off = await AddAsync(admin, link);
                var html = await off.Content.ReadAsStringAsync(Token);

                Assert.Equal(HttpStatusCode.BadRequest, off.StatusCode);
                Assert.Contains(BlacklistWords.CustomLinkOff, Text(html));
                Assert.Contains("aria-invalid=\"true\"", html);
                Assert.Equal(link, BoxValue(html, "steamid"));
                Assert.Equal(1, Regex.Count(html, Name));
                PageTests.AssertAccessible("a custom link while requests are off", html);
            }

            Assert.Empty(await db.RequestsAsync());
            Assert.Equal(0, await db.EntryCountAsync());
            Assert.False(factory.Logs.Text.Contains(Name, StringComparison.Ordinal), "A log line holds the custom link's name");
        }
    }
}
