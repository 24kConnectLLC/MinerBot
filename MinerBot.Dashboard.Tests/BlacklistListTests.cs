using System.Net;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Blacklist;
using MinerBot.Dashboard.Tests.Support;
using static MinerBot.Dashboard.Tests.Support.BlacklistSteps;

namespace MinerBot.Dashboard.Tests
{
    // The blacklist's list: one search by SteamID or name, 25 to a page, each entry's account, name, reason, end and last
    // change, and the rows no list can show, which can only be removed
    [Collection("Database")]
    public class BlacklistListTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static readonly DateTime Noon = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

        static AuditEntry Change(string target, string outcome, DateTime at, string name = "Test Owner", string role = AuditRole.Owner) => new()
        {
            at = at,
            actor_id = TestUsers.Owner.Id,
            actor_name = name,
            actor_role = role,
            action = "blacklist.edit",
            outcome = outcome,
            target_type = "blacklist",
            target_id = target
        };

        [Fact]
        public async Task EachEntryShowsItsAccountEndAndLastChange()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(
                Entry(12345, "Alpha Player", "Cheating", new DateTime(2099, 1, 2, 3, 4, 5)),
                Entry(23456, "Bravo Player", "Griefing", new DateTime(2001, 1, 1)),
                Entry(34567, "Charlie Player", null, null));
            await db.RootExecuteAsync($@"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username, expire_date) VALUES
                (0, 'Delta Player', NULL), (45678, 'Echo Player', '0000-00-00 00:00:00')");

            // The latest change that went through counts, not a later refusal, and an entry the dashboard never changed has none
            var audit = db.Audit();
            await audit.AddAudit(Change("12345", AuditOutcome.Ok, Noon));
            await audit.AddAudit(Change("12345", AuditOutcome.Ok, Noon.AddMinutes(5), "Night\U0000200BRaid", AuditRole.Admin));
            await audit.AddAudit(Change("12345", AuditOutcome.Refused, Noon.AddMinutes(10)));

            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.40");
            var html = await admin.Client.GetStringAsync("/admin/blacklist", Token);
            var text = WebUtility.HtmlDecode(html);

            Assert.Contains(BlacklistWords.NotEnforced, text);
            Assert.Contains("href=\"/admin/blacklist/add\"", html);
            Assert.Contains("5 entries, by name.", text);

            Assert.Contains($"<a href=\"/admin/blacklist/12345\">{SteamId64}</a>", html);
            Assert.Contains("[U:1:12345]", html);
            Assert.Contains("<a href=\"/admin/blacklist/0\">[U:1:0] (not a Steam account)</a>", html);
            Assert.Contains("2099-01-02 03:04 UTC", text);

            // Expired only for an end that has passed, by UTC
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, ">Expired<"));
            Assert.Contains("2001-01-01 00:00 UTC", text);
            Assert.Contains("No end", text);
            Assert.Contains("Not a real date", text);
            Assert.Contains("No reason", text);

            Assert.Contains("Night<mark class=\"hidden-char\" title=\"zero-width space\">[U&#x2B;200B]</mark>Raid</bdi> (Admin), 2026-11-05 12:05 UTC", html);
            Assert.Equal(4, System.Text.RegularExpressions.Regex.Count(text, "No change recorded here"));

            // By name, as the website's table sorts them
            Assert.True(text.IndexOf("Alpha Player", StringComparison.Ordinal) < text.IndexOf("Bravo Player", StringComparison.Ordinal));
            Assert.True(text.IndexOf("Delta Player", StringComparison.Ordinal) < text.IndexOf("Echo Player", StringComparison.Ordinal));
            PageTests.AssertAccessible("the blacklist", html);
        }

        // A SteamID in any form finds its account, and is tried as part of a name too, since either is enough. A custom
        // profile link can only be a name.
        [Theory]
        [InlineData("12345", new uint[] { 12345, 51234 })]
        [InlineData(SteamId64, new uint[] { 12345 })]
        [InlineData("https://steamcommunity.com/profiles/" + SteamId64, new uint[] { 12345 })]
        [InlineData("[U:1:12345]", new uint[] { 12345 })]
        [InlineData("STEAM_0:1:6172", new uint[] { 12345 })]
        [InlineData("test player", new uint[] { 12345 })]
        [InlineData("  Test  ", new uint[] { 12345 })]
        [InlineData("100%", new uint[] { 54321 })]
        [InlineData("https://steamcommunity.com/id/someone", new uint[0])]
        [InlineData("[U:1:99999]", new uint[0])]
        public async Task SearchFindsAnySteamIdOrAPartOfAName(string search, uint[] found)
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345, "Test Player"), Entry(51234, "Player 12345"), Entry(54321, "100% Cheater"));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.41");

            var html = await admin.Client.GetStringAsync("/admin/blacklist?q=" + Uri.EscapeDataString(search), Token);
            var listed = System.Text.RegularExpressions.Regex.Matches(html, "<a href=\"/admin/blacklist/([0-9]+)\">").Select(match => uint.Parse(match.Groups[1].Value));

            Assert.Equal(found.Order(), listed.Order());
            Assert.Equal(search.Trim(), BoxValue(html, "q"));
            Assert.Contains(found.Length switch
            {
                0 => "No entry matches that search.",
                1 => "1 entry that matches the search, by name.",
                var count => $"{count} entries that match the search, by name."
            }, WebUtility.HtmlDecode(html));
        }

        // A custom profile link can only be looked for in names, and the page says so above the results, and where to get
        // the account's number. No other search says it.
        [Fact]
        public async Task CustomLinkSearchSaysWhereToGetTheNumber()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345, "Test Player"));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.47");

            var html = await admin.Client.GetStringAsync("/admin/blacklist?q=" + Uri.EscapeDataString("https://steamcommunity.com/id/someone"), Token);
            var text = WebUtility.HtmlDecode(html);

            Assert.Contains("<div class=\"notice\" role=\"status\">", html);
            Assert.Contains(BlacklistWords.CustomLinkSearch, text);
            Assert.Contains("MinerBot's /more steam_id command in Discord gives the SteamID64 of a custom link written as https://steamcommunity.com/id/ and the name.", text);
            Assert.True(text.IndexOf(BlacklistWords.CustomLinkSearch, StringComparison.Ordinal) < text.IndexOf("No entry matches that search.", StringComparison.Ordinal));
            PageTests.AssertAccessible("a search for a custom link", html);

            foreach (var search in new[] { "", "Test", "[U:1:12345]", "https://steamcommunity.com/profiles/" + SteamId64, "not an id" })
                Assert.DoesNotContain(BlacklistWords.CustomLinkLookup, WebUtility.HtmlDecode(await admin.Client.GetStringAsync("/admin/blacklist?q=" + Uri.EscapeDataString(search), Token)));
        }

        // The box stops at 100 characters, so only an address someone typed is longer
        [Fact]
        public async Task SearchPastTheBoxesLimitIsRefused()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.42");

            Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync("/admin/blacklist?q=" + new string('a', 100), Token)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.GetAsync("/admin/blacklist?q=" + new string('a', 101), Token)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync("/admin/blacklist?q=" + Uri.EscapeDataString(string.Concat(Enumerable.Repeat("\U0001F600", 100))), Token)).StatusCode);
            Assert.Contains("maxlength=\"100\"", await admin.Client.GetStringAsync("/admin/blacklist", Token));
        }

        // 25 to a page, as the armor list pages: anything that isn't a page number is the first page
        [Fact]
        public async Task PagesOf25()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync([.. Enumerable.Range(1, 30).Select(i => Entry((uint)i, $"Player {i:00}"))]);
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.43");

            static int Rows(string html) => System.Text.RegularExpressions.Regex.Count(html, "<a href=\"/admin/blacklist/[0-9]+\">");

            var first = await admin.Client.GetStringAsync("/admin/blacklist", Token);
            Assert.Equal(25, Rows(first));
            Assert.Contains("href=\"/admin/blacklist?page=2\">Next page</a>", first);
            Assert.Contains("Page 1 of 2", first);

            var second = await admin.Client.GetStringAsync("/admin/blacklist?page=2", Token);
            Assert.Equal(5, Rows(second));
            Assert.Contains("href=\"/admin/blacklist\">Previous page</a>", second);

            foreach (var page in new[] { "0", "-1", "abc", "1.5", "99999999999" })
                Assert.Equal(25, Rows(await admin.Client.GetStringAsync("/admin/blacklist?page=" + page, Token)));

            var past = WebUtility.HtmlDecode(await admin.Client.GetStringAsync("/admin/blacklist?page=9", Token));
            Assert.Contains("There's nothing on page 9. <a href=\"/admin/blacklist\">Go to the first page</a>.", past);

            // The search is kept on every page's link
            Assert.Contains("href=\"/admin/blacklist?q=Player&amp;page=2\">Next page</a>", await admin.Client.GetStringAsync("/admin/blacklist?q=Player", Token));
        }

        // A row whose number no account can be: no list or search shows it, so the page lists it apart, whole, and removes it
        // with its audit row, which keeps what it held
        [Fact]
        public async Task RowsWithNoAccountNumberAreListedAndRemoved()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345));
            await db.RootExecuteAsync($@"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username, reason, expire_date, notes) VALUES
                (5000000000, 'Cut Player', 'Typo', '2027-01-02 03:04:05', 'Typed in elsewhere'), (-1, NULL, NULL, '0000-00-00 00:00:00', NULL)");
            await using var factory = new DashboardFactory(db);
            var owner = await ArmorSteps.ReviewerAsync(factory, "198.51.100.44", TestUsers.Owner);

            var html = await owner.Client.GetStringAsync("/admin/blacklist", Token);
            var text = WebUtility.HtmlDecode(html);

            Assert.Contains("Rows with no Steam account number", text);
            Assert.Contains(BlacklistWords.NumbersIntro, text);
            Assert.Contains("<td>5000000000</td>", html);
            Assert.Contains("<td>-1</td>", html);
            Assert.Contains("Typed in elsewhere", text);
            Assert.Contains("1 entry, by name.", text);
            PageTests.AssertAccessible("the blacklist with rows no account can be", html);

            var token = SignInSteps.FormToken(html);
            var response = await CraftedAsync(owner, "/admin/blacklist?handler=RemoveNumber", token, ("number", "5000000000"), ("version", BlacklistActionTests.NumberVersion(html)));

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/admin/blacklist?done=removed&number=5000000000", SignInSteps.PathOf(response.Headers.Location));

            var after = await owner.Client.GetStringAsync("/admin/blacklist?done=removed&number=5000000000", Token);
            Assert.Contains(BlacklistWords.NumberRemoved, WebUtility.HtmlDecode(after));
            Assert.DoesNotContain("<td>5000000000</td>", after);
            Assert.Equal([-1m], await db.Blacklist().ListOutOfRangeSteamIds());

            // Only while no such row is left
            Assert.DoesNotContain(BlacklistWords.NumberRemoved, WebUtility.HtmlDecode(await owner.Client.GetStringAsync("/admin/blacklist?done=removed&number=-1", Token)));
            Assert.DoesNotContain(BlacklistWords.NumberRemoved, WebUtility.HtmlDecode(await owner.Client.GetStringAsync("/admin/blacklist?done=removed&number=12345", Token)));

            var audit = Assert.Single(await db.BlacklistAuditAsync());
            Assert.Equal("blacklist.remove", audit.action);
            Assert.Equal(AuditOutcome.Ok, audit.outcome);
            Assert.Equal(AuditRole.Owner, audit.actor_role);
            Assert.Equal("blacklist", audit.target_type);
            Assert.Equal("5000000000", audit.target_id);
            Assert.Equal("""{"Name":"Cut Player","Reason":"Typo","Expires":"2027-01-02 03:04:05","Notes":"Typed in elsewhere"}""", audit.before_json);
            Assert.Null(audit.after_json);
            Assert.Equal("The row's number wasn't a Steam account number", audit.detail);

            // A row whose end isn't a real date goes the same way
            var negative = await CraftedAsync(owner, "/admin/blacklist?handler=RemoveNumber", token, ("number", "-1"), ("version", BlacklistActionTests.NumberVersion(after, "-1")));
            Assert.Equal(HttpStatusCode.Redirect, negative.StatusCode);
            Assert.Empty(await db.Blacklist().ListOutOfRangeSteamIds());
            Assert.Equal("""{"Name":null,"Reason":null,"Expires":"unreadable","Notes":null}""", (await db.BlacklistAuditAsync())[^1].before_json);
            Assert.Equal("-1", (await db.BlacklistAuditAsync())[^1].target_id);
        }

        // A table holding only rows whose number no account can be isn't empty, so the page doesn't say it is right above
        // the box that lists them. Once they're gone, it is.
        [Fact]
        public async Task TableOfOnlyRowsWithNoAccountNumberIsntCalledEmpty()
        {
            var db = await fixture.ReadyAsync();
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username) VALUES (5000000000, 'Cut Player')");
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.50");

            var html = await admin.Client.GetStringAsync("/admin/blacklist", Token);
            var text = WebUtility.HtmlDecode(html);

            Assert.Contains("0 entries, by name.", text);
            Assert.Contains("No entry has a Steam account number. The rows below can't match anyone.", text);
            Assert.DoesNotContain("The blacklist is empty.", text);
            Assert.True(text.IndexOf("No entry has a Steam account number", StringComparison.Ordinal) < text.IndexOf("Rows with no Steam account number", StringComparison.Ordinal));
            Assert.Contains("<td>5000000000</td>", html);
            PageTests.AssertAccessible("the blacklist holding only rows no account can be", html);

            // A search still says when it finds nothing, as no search shows these rows
            Assert.Contains("No entry matches that search.", WebUtility.HtmlDecode(await admin.Client.GetStringAsync("/admin/blacklist?q=Cut", Token)));

            await db.RootExecuteAsync($"DELETE FROM {MariaDbFixture.BlacklistTable} WHERE steam_id = 5000000000");
            var empty = WebUtility.HtmlDecode(await admin.Client.GetStringAsync("/admin/blacklist", Token));
            Assert.Contains("The blacklist is empty.", empty);
            Assert.DoesNotContain("No entry has a Steam account number", empty);
        }

        // Changed or gone since the page showed it: refused, with its own audit row. Gone is what was asked, as when an
        // earlier try was saved. A form someone made up gets a 400 before the database.
        [Fact]
        public async Task RemovingANumberRefusesStaleAndMadeUpForms()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345));
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username) VALUES (5000000000, 'Cut Player')");
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.45");
            var html = await admin.Client.GetStringAsync("/admin/blacklist", Token);
            var token = SignInSteps.FormToken(html);
            var version = BlacklistActionTests.NumberVersion(html);

            foreach (var (number, sent) in new[] { ("12345", version), ("4294967295", version), ("0", version), ("5000000000.0", version), ("5e9", version), (" 5000000000", version),
                ("99999999999", version), ("abc", version), ("", version), ("5000000000", version.ToUpperInvariant()), ("5000000000", "") })
            {
                var response = await CraftedAsync(admin, "/admin/blacklist?handler=RemoveNumber", token, ("number", number), ("version", sent));
                Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{number} with {sent} answered {response.StatusCode}");
            }

            Assert.Empty(await db.BlacklistAuditAsync());

            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.BlacklistTable} SET username = 'Renamed Elsewhere' WHERE steam_id = 5000000000");
            var changed = await CraftedAsync(admin, "/admin/blacklist?handler=RemoveNumber", token, ("number", "5000000000"), ("version", version));
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            Assert.Contains(BlacklistWords.NumberChanged, await ArmorSteps.TextAsync(changed));
            PageTests.AssertAccessible("a refused removal of a row with no account number", await changed.Content.ReadAsStringAsync(Token));

            var fresh = BlacklistActionTests.NumberVersion(await changed.Content.ReadAsStringAsync(Token));
            await db.RootExecuteAsync($"DELETE FROM {MariaDbFixture.BlacklistTable} WHERE steam_id = 5000000000");
            var gone = await CraftedAsync(admin, "/admin/blacklist?handler=RemoveNumber", token, ("number", "5000000000"), ("version", fresh));
            Assert.Equal(HttpStatusCode.Conflict, gone.StatusCode);
            Assert.Contains(BlacklistWords.NumberAlreadyRemoved, await ArmorSteps.TextAsync(gone));
            Assert.Contains("class=\"notice notice-done\" role=\"status\"", await gone.Content.ReadAsStringAsync(Token));

            var refusals = await db.BlacklistAuditAsync();
            Assert.Equal([AuditOutcome.Refused, AuditOutcome.Refused], refusals.Select(row => row.outcome));
            Assert.Equal("""{"Name":"Renamed Elsewhere","Reason":null,"Expires":null,"Notes":null}""", refusals[0].before_json);
            Assert.Equal("The entry had changed since the page showed it", refusals[0].detail);
            Assert.Null(refusals[1].before_json);
            Assert.All(refusals, row => Assert.Equal("5000000000", row.target_id));
            Assert.NotNull(await db.EntryAsync(12345));
        }

        // What the website's table holds is anyone's text, so names and reasons are revealed and encoded
        [Fact]
        public async Task NamesAndReasonsAreRevealed()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345, "Night\U0000200BRaid", "<b>bold</b> @everyone"));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.46");

            var html = await admin.Client.GetStringAsync("/admin/blacklist", Token);

            Assert.Contains("Night<mark class=\"hidden-char\" title=\"zero-width space\">[U&#x2B;200B]</mark>Raid", html);
            Assert.Contains("&lt;b&gt;bold&lt;/b&gt; <mark class=\"mention\" title=\"Discord mention\">@everyone</mark>", html);
            Assert.DoesNotContain("<b>bold", html);
        }
    }
}
