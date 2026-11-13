using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Armor;
using static MinerBot.Dashboard.Tests.Support.ArmorSteps;

namespace MinerBot.Dashboard.Tests
{
    // The armor list at /admin/armor: a tab for each status, oldest first while pending, a search by name or author, 25 to
    // a page, and markers from the approval rules. Made-up armor only.
    [Collection("Database")]
    public partial class ArmorListTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static async Task<string> PageAsync(TestBrowser browser, string path)
        {
            var response = await browser.Client.GetAsync(path, Token);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} answered {response.StatusCode}");
            return await response.Content.ReadAsStringAsync(Token);
        }

        // The armor a list page links to, in its order
        static List<long> Ids(string html) => ArmorLink().Matches(html).Select(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();

        // Fails for a tab that lists another status, a wrong order, or armor with no status left out of All
        [Fact]
        public async Task EachTabListsItsStatusPendingOldestFirst()
        {
            var db = await fixture.ReadyAsync();
            var armor = new[]
            {
                Submission(101), Submission(102), Submission(103), Submission(104, ArmorStatus.Approved), Submission(105, ArmorStatus.NotApproved), Submission(106, null)
            };

            // Sent on these days, so the order isn't the IDs'
            int[] days = [3, 1, 2, 4, 5, 6];
            for (var i = 0; i < armor.Length; i++)
                armor[i].CreatedAt = new DateTime(2026, 11, days[i], 12, 0, 0);

            await db.AddAsync(armor);
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.110");

            Assert.Equal([102, 103, 101], Ids(await PageAsync(admin, "/admin/armor")));
            Assert.Equal([102, 103, 101], Ids(await PageAsync(admin, "/admin/armor?status=pending")));
            Assert.Equal([104], Ids(await PageAsync(admin, "/admin/armor?status=approved")));
            Assert.Equal([105], Ids(await PageAsync(admin, "/admin/armor?status=not-approved")));

            var all = await PageAsync(admin, "/admin/armor?status=all");
            Assert.Equal([106, 105, 104, 101, 103, 102], Ids(all));
            Assert.Contains("No status", WebUtility.HtmlDecode(all));
            Assert.Contains("6 armor in all, newest first.", WebUtility.HtmlDecode(all));
            Assert.Contains("3 armor waiting for a review, oldest first.", WebUtility.HtmlDecode(await PageAsync(admin, "/admin/armor")));

            // The tab that's open says so, for screen readers too
            Assert.Contains("href=\"/admin/armor?status=all\" aria-current=\"page\">All</a>", all);
        }

        [Fact]
        public async Task SearchFindsANameOrAnAuthorInTheOpenTab()
        {
            var db = await fixture.ReadyAsync();
            var byAuthor = Submission(203, ArmorStatus.Pending, "Ember Guard");
            byAuthor.Author = "Frosty Maker";
            await db.AddAsync(Submission(201, ArmorStatus.Pending, "Frost Knight"), Submission(202, ArmorStatus.Approved, "Frost Queen"), byAuthor, Submission(204, ArmorStatus.Pending, "Night Raid"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.111");

            Assert.Equal([201, 203], Ids(await PageAsync(admin, "/admin/armor?q=frost")));
            Assert.Equal([202], Ids(await PageAsync(admin, "/admin/armor?status=approved&q=FROST")));
            Assert.Equal([203, 202, 201], Ids(await PageAsync(admin, "/admin/armor?status=all&q=%20frost%20")));

            var none = WebUtility.HtmlDecode(await PageAsync(admin, "/admin/armor?q=nothing"));
            Assert.Contains("No armor here matches that search.", none);

            // The box keeps the search, and the tabs and the clear link carry it on or drop it
            var searched = await PageAsync(admin, "/admin/armor?q=frost");
            Assert.Contains("value=\"frost\"", searched);
            Assert.Contains("href=\"/admin/armor?status=approved&amp;q=frost\"", searched);
            Assert.Contains("href=\"/admin/armor?status=pending\">Clear the search</a>", searched);
            Assert.Contains("2 armor waiting for a review that match the search, oldest first.", WebUtility.HtmlDecode(searched));

            // Each armor's link carries the tab and the search, for its page's Back to the list
            Assert.Contains("href=\"/admin/armor/201?list=pending&amp;q=frost\"", searched);
            Assert.Contains("href=\"/admin/armor/202?list=approved\"", await PageAsync(admin, "/admin/armor?status=approved"));
        }

        // 25 to a page, counted from 1. Anything else in the address is the first page, and a page past the end says so.
        [Fact]
        public async Task PagesHoldTwentyFive()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync([.. Enumerable.Range(1, 30).Select(id => Submission(id))]);
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.112");

            var first = await PageAsync(admin, "/admin/armor");
            Assert.Equal([.. Enumerable.Range(1, 25).Select(id => (long)id)], Ids(first));
            Assert.Contains("Page 1 of 2", first);
            Assert.Contains("href=\"/admin/armor?status=pending&amp;page=2\">Next page</a>", first);
            Assert.DoesNotContain("Previous page", first);

            var second = await PageAsync(admin, "/admin/armor?page=2");
            Assert.Equal([26, 27, 28, 29, 30], Ids(second));
            Assert.Contains("href=\"/admin/armor?status=pending\">Previous page</a>", second);
            Assert.DoesNotContain("Next page", second);

            foreach (var page in new[] { "0", "-1", "abc", "1.5", "99999999999999999999" })
                Assert.Equal(Ids(first), Ids(await PageAsync(admin, "/admin/armor?page=" + page)));

            var past = WebUtility.HtmlDecode(await PageAsync(admin, "/admin/armor?page=3"));
            Assert.Contains("There's nothing on page 3.", past);
            Assert.Empty(Ids(past));
        }

        // Checked before the data layer, which would throw for a status it doesn't know: a 404 or a 400, never a 500
        [Fact]
        public async Task UnknownTabIsNotFoundAndTooLongASearchIsRefused()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.113");

            Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.GetAsync("/admin/armor?status=Pending%20Approval", Token)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.GetAsync("/admin/armor?status=bogus", Token)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.GetAsync("/admin/armor?q=" + new string('a', ArmorRules.NameLimit + 1), Token)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync("/admin/armor?q=" + new string('a', ArmorRules.NameLimit), Token)).StatusCode);

            // Counted in characters, as the table counts them, so 60 emoji are still a search
            Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync("/admin/armor?q=" + Uri.EscapeDataString(string.Concat(Enumerable.Repeat("\U0001F600", 60))), Token)).StatusCode);
            Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
        }

        // Each row's markers come from the rules, in words as well as colour. The list has no name check, so it never says a
        // row has no problems.
        [Fact]
        public async Task MarkersShowWhatTheRulesFind()
        {
            var db = await fixture.ReadyAsync();
            var hidden = Submission(301, ArmorStatus.Pending, "Night​Raid");
            hidden.ArmorDescription = "Visit https://example.com";
            var offSite = Submission(302, ArmorStatus.Pending, "Test Armor 302");
            offSite.ArmorScreenshot = "http://127.0.0.1/x.png";
            await db.AddAsync(hidden, offSite, Submission(303));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.114");

            var html = await PageAsync(admin, "/admin/armor");
            var rows = html.Split("<tr>").Skip(2).ToList();

            Assert.Equal(3, rows.Count);
            Assert.Contains("<span class=\"marker marker-warn\"><span class=\"visually-hidden\">Needs a look: </span>Hidden characters</span>", rows[0]);
            Assert.Contains(">Link</span>", rows[0]);
            Assert.Contains("<span class=\"marker marker-block\"><span class=\"visually-hidden\">Blocks approval: </span>Screenshot not an upload</span>", rows[1]);
            Assert.DoesNotContain("marker", rows[2]);
            Assert.Contains("Whether another approved armor already has the name is checked on each armor's own page.", WebUtility.HtmlDecode(html));

            // What the two kinds mean, in words, not in colour and weight alone
            Assert.Contains("<span class=\"marker marker-block\">bold ones</span> stop an approval, and <span class=\"marker marker-warn\">the others</span> need a look first.", html);
        }

        // A name of spaces, no-break spaces or characters that show nothing would leave the row's only link empty, so the
        // armor couldn't be opened, or rejected, from the list. The rules' own blank test picks the words instead.
        [Fact]
        public async Task ArmorWithNoNameToShowStillHasALink()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(601, ArmorStatus.Pending, "   "), Submission(602, ArmorStatus.Pending, "\u00A0\u00A0"), Submission(603, ArmorStatus.Pending, "x"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.119");

            var html = await PageAsync(admin, "/admin/armor");

            Assert.Contains("href=\"/admin/armor/601?list=pending\">(no name)</a>", html);
            Assert.Contains("href=\"/admin/armor/602?list=pending\">(no name)</a>", html);
            Assert.Contains("href=\"/admin/armor/603?list=pending\"><bdi class=\"revealed\">x</bdi></a>", html);
        }

        // Armor whose status the table can't tell from one of the website's words is listed under that word's tab, as the
        // bot treats it, and the All tab says how it's stored. One that matches no word is listed only under All.
        [Fact]
        public async Task StatusSpeltDifferentlyIsListedUnderItsWord()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(701, "approved ", "Test Armor 701"), Submission(702, "Pending", "Test Armor 702"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.109");

            Assert.Equal([701], Ids(await PageAsync(admin, "/admin/armor?status=approved")));
            Assert.Empty(Ids(await PageAsync(admin, "/admin/armor?status=pending")));

            var all = Regex.Replace(await PageAsync(admin, "/admin/armor?status=all"), @"\s+", " ");
            Assert.Contains("Approved <span class=\"muted\">(stored as <bdi class=\"revealed\">approved </bdi>)</span>", all);
            Assert.Contains("<bdi class=\"revealed\">Pending</bdi> <span class=\"muted\">(not one the website uses)</span>", all);
        }

        // Names and authors are anyone's to choose, so they're encoded, isolated and show what they hide
        [Fact]
        public async Task SubmitterTextIsEncodedAndRevealed()
        {
            var db = await fixture.ReadyAsync();
            var armor = Submission(401, ArmorStatus.Pending, "<script>alert(1)</script>‮");
            armor.Author = "<b>Test</b>​";
            await db.AddAsync(armor);
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.115");

            var html = await PageAsync(admin, "/admin/armor");

            Assert.DoesNotContain("<script>", html);
            Assert.DoesNotContain("<b>Test", html);
            Assert.Contains("<bdi class=\"revealed\">&lt;script&gt;alert(1)&lt;/script&gt;<mark class=\"hidden-char\" title=\"right-to-left override\">[U&#x2B;202E]</mark></bdi>", html);
            Assert.Contains("<bdi class=\"revealed\">&lt;b&gt;Test&lt;/b&gt;<mark class=\"hidden-char\" title=\"zero-width space\">[U&#x2B;200B]</mark></bdi>", html);
        }

        // The folder's Admin policy: Owners and Admins get in, and anyone else goes to sign in
        [Fact]
        public async Task OwnersAndAdminsGetInAndEveryoneElseSignsIn()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(501));
            await using var factory = new DashboardFactory(db);

            var anonymous = factory.Visitor("203.0.113.116");
            foreach (var (path, back) in new[] { ("/admin/armor", "%2Fadmin%2Farmor"), ("/admin/armor/501", "%2Fadmin%2Farmor%2F501") })
            {
                var response = await anonymous.Client.GetAsync(path, Token);
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                Assert.Equal("/signin?returnUrl=" + back, SignInSteps.PathOf(response.Headers.Location));
            }

            var owner = await ReviewerAsync(factory, "203.0.113.117", TestUsers.Owner);
            var admin = await ReviewerAsync(factory, "203.0.113.118", TestUsers.Admin);

            foreach (var reviewer in new[] { owner, admin })
            {
                Assert.Equal(HttpStatusCode.OK, (await reviewer.Client.GetAsync("/admin/armor", Token)).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await reviewer.Client.GetAsync("/admin/armor/501", Token)).StatusCode);
            }

            // The layout's links, for both roles, in the nav's order
            foreach (var reviewer in new[] { owner, admin })
            {
                var nav = System.Text.RegularExpressions.Regex.Match(await reviewer.Client.GetStringAsync("/admin", Token),
                    "<nav aria-label=\"Dashboard\" class=\"site-nav\">(.*?)</nav>", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;

                Assert.Equal(["<a href=\"/admin\">Overview</a>", "<a href=\"/admin/armor\">Armor</a>", "<a href=\"/admin/blacklist\">Blacklist</a>",
                    "<a href=\"/admin/audit\">Audit log</a>"], nav.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        [GeneratedRegex("href=\"/admin/armor/([0-9]+)[?\"]")]
        private static partial Regex ArmorLink();
    }
}
