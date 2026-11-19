using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Armor;

namespace MinerBot.Dashboard.Tests
{
    // The pages work without scripts, pass the Content-Security-Policy as they are, and speak plain English. The status
    // pages load the one script there is, which only keeps their status box current.
    [Collection("Database")]
    public partial class PageTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static readonly string[] Reasons = ["unlisted", "mfa", "scope", "cancelled", "failed"];

        // Fails on markup the policy would block, such as an inline script or style, or on a page that needs a script. The
        // only script tag a page may have is refresh.js's, from the dashboard's own files, with its version, and deferred.
        internal static void AssertAccessible(string what, string html)
        {
            Assert.True(html.Contains("<html lang=\"en\">", StringComparison.Ordinal), $"{what} has no lang");
            Assert.True(Regex.Count(html, "<h1[ >]") == 1, $"{what} hasn't exactly one h1");
            Assert.True(html.Contains("<main id=\"main\"", StringComparison.Ordinal), $"{what} has no main");
            Assert.True(html.Contains("href=\"#main\"", StringComparison.Ordinal), $"{what} has no skip link");
            Assert.True(NonEmptyTitle().IsMatch(html), $"{what} has no title");
            Assert.True(html.Contains("<meta name=\"color-scheme\" content=\"light dark\"", StringComparison.Ordinal), $"{what} has no color-scheme");
            Assert.True(html.Contains("/css/site.css?v=", StringComparison.Ordinal), $"{what} has no versioned stylesheet");
            Assert.True(RefreshScript().Count(html) <= 1, $"{what} loads refresh.js more than once");

            foreach (var blocked in new[] { "<script", " style=", "<style" })
                Assert.False(RefreshScript().Replace(html, "").Contains(blocked, StringComparison.OrdinalIgnoreCase), $"{what} has {blocked}");
        }

        // Whether a page loads refresh.js, the one way AssertAccessible allows
        internal static bool LoadsRefreshScript(string html) => RefreshScript().IsMatch(html);

        // A table's wrapper with nothing in it the keyboard can reach takes the focus itself, as a group named by an element
        // of the page, so a keyboard can scroll a table too wide for a phone: Safari doesn't make a scrolling box focusable on
        // its own, as Chrome and Firefox do. Fails on a page with no such wrapper, so it can't pass on nothing.
        internal static void AssertTablesTakeTheKeyboard(string what, string html)
        {
            var checkedWrappers = 0;

            foreach (Match start in TableWrap().Matches(html))
            {
                // The wrapper ends at the div that closes it, counting the divs inside it, such as a server's name's
                var depth = 0;
                var end = html.Length;

                foreach (Match tag in DivTag().Matches(html, start.Index))
                {
                    depth += tag.Value.StartsWith("</", StringComparison.Ordinal) ? -1 : 1;

                    if (depth == 0)
                    {
                        end = tag.Index;
                        break;
                    }
                }

                if (Focusable().IsMatch(html[(start.Index + start.Length)..end]))
                    continue;

                checkedWrappers++;
                var group = FocusableGroup().Match(start.Value);
                Assert.True(group.Success, $"{what} has a table no keyboard can scroll: {start.Value}");
                Assert.True(Regex.Count(html, $"\\sid=\"{Regex.Escape(group.Groups[1].Value)}\"") == 1, $"{what} names a table's wrapper by {group.Groups[1].Value}, which isn't one element's ID");
            }

            Assert.True(checkedWrappers > 0, $"{what} has no table without a link");
        }

        // A table's cell that holds only a day, or a time in UTC, keeps it whole: when another column wants the room, the
        // table's automatic layout would otherwise cut it at a hyphen, as 2026-11- over 15. Fails on a page with no such
        // cell, so it can't pass on nothing.
        internal static void AssertTimesStayWhole(string what, string html)
        {
            var cells = TimeCell().Matches(html);
            Assert.True(cells.Count > 0, $"{what} has no cell that holds only a time");

            foreach (Match cell in cells)
                Assert.True(cell.Groups[2].Value.Contains(" class=\"audit-when\"", StringComparison.Ordinal), $"{what} has a time that can break: {cell.Value}");
        }

        [Fact]
        public async Task EveryPageIsAccessibleAndScriptFree()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var anonymous = factory.Visitor();

            foreach (var page in new[] { "/", "/signout", "/denied", "/error", "/nothing-here" }.Concat(Reasons.Select(reason => "/denied?reason=" + reason)))
                AssertAccessible(page, await (await anonymous.Client.GetAsync(page, Token)).Content.ReadAsStringAsync(Token));

            // Armor with everything the review page draws: warnings to tick, a blocking problem, hidden characters, a
            // mention, a link, a backtick, no screenshot and a stored save string, and history
            var busy = ArmorSteps.Submission(2, ArmorStatus.Pending, "Night​Raid‮");
            busy.ArmorDescription = "Ping @everyone at https://example.com ```";
            busy.ArmorScreenshot = null;
            busy.ColorArray = "AQIDBAUGBwgJCgsMDQ==";
            var blocked = ArmorSteps.Submission(3, ArmorStatus.Pending, "Night`Raid");
            blocked.ArmorScreenshot = "http://127.0.0.1/x.png";

            // Approved armor the rules would stop now, a status spelt differently from the website's word, and one the
            // website doesn't use
            var approvedButBlocked = ArmorSteps.Submission(4, ArmorStatus.Approved, "Test Armor 4");
            approvedButBlocked.ArmorScreenshot = "https://murderminershub.com/x.png";
            await db.AddAsync(ArmorSteps.Submission(1), busy, blocked, approvedButBlocked, ArmorSteps.Submission(5, "approved", "Test Armor 5"),
                ArmorSteps.Submission(6, "Pending", "Test Armor 6"));

            var owner = factory.Visitor("203.0.113.70");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var reject = await ArmorSteps.PostAsync(owner, 1, "Reject", await ArmorSteps.OpenAsync(owner, 1), true, ("reason", "Not real"));
            Assert.Equal(HttpStatusCode.Redirect, reject.StatusCode);

            foreach (var page in new[] { "/", "/admin", "/signout", "/admin/armor", "/admin/armor?status=all", "/admin/armor?q=nothing", "/admin/armor?page=9",
                "/admin/armor/1", "/admin/armor/1?done=rejected", "/admin/armor/1?list=all&q=test", "/admin/armor/2", "/admin/armor/3", "/admin/armor/4",
                "/admin/armor/5", "/admin/armor/6" })
                AssertAccessible("owner " + page, await owner.Client.GetStringAsync(page, Token));

            // The page a refused action answers with. ArmorActionTests checks the one for a save that may not have landed.
            var refused = await ArmorSteps.PostAsync(owner, 2, "Approve", await ArmorSteps.OpenAsync(owner, 2), false);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            AssertAccessible("a refused approval", await refused.Content.ReadAsStringAsync(Token));
        }

        // Every state of the blacklist's pages and the audit log: lists empty and full, an entry with everything its page
        // draws, account 0, one no longer listed, the rows no account number can be, each refusal's page, and the audit log
        // with each kind of row, filtered and not. BlacklistActionTests checks the page for a save that may not have landed.
        [Fact]
        public async Task BlacklistAndAuditPagesAreAccessibleAndScriptFree()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = factory.Visitor("203.0.113.73");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            foreach (var page in new[] { "/admin/blacklist", "/admin/blacklist?q=nothing", "/admin/audit" })
                AssertAccessible("empty " + page, await owner.Client.GetStringAsync(page, Token));

            await db.AddEntriesAsync(
                BlacklistSteps.Entry(12345, "Night\U0000200BRaid\U0000202E", "Ping @everyone at https://example.com ```", new DateTime(2001, 1, 1), "Notes"),
                BlacklistSteps.Entry(23456, null, null, new DateTime(2099, 1, 1, 3, 4, 5), null));
            await db.RootExecuteAsync($@"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username, expire_date) VALUES
                (0, 'Zero Player', '0000-00-00 00:00:00'), (5000000000, 'Cut Player', '2027-01-02 00:00:00'), (-1, NULL, NULL)");

            // A change, a refusal and an entry removed, so the lists and histories have each kind of row
            var page12345 = await BlacklistSteps.OpenAsync(owner, 12345);
            Assert.Equal(HttpStatusCode.Redirect, (await BlacklistSteps.EditAsync(owner, 12345, page12345, ("reason", "Edited"))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await BlacklistSteps.EditAsync(owner, 12345, page12345, ("reason", "Stale"))).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await BlacklistSteps.RemoveAsync(owner, 23456, await BlacklistSteps.OpenAsync(owner, 23456))).StatusCode);

            foreach (var page in new[] { "/admin", "/admin/blacklist", "/admin/blacklist?q=12345", "/admin/blacklist?q=https%3A%2F%2Fsteamcommunity.com%2Fid%2Fsomeone",
                "/admin/blacklist?page=9", "/admin/blacklist?done=removed&number=4294967296",
                "/admin/blacklist/add", "/admin/blacklist/add?steamid=%5BU%3A1%3A23456%5D", "/admin/blacklist/12345", "/admin/blacklist/12345?done=saved",
                "/admin/blacklist/0", "/admin/blacklist/23456?done=removed", "/admin/audit", "/admin/audit?type=blacklist&id=12345", "/admin/audit?action=auth.signin",
                "/admin/audit?from=2026-11-06&to=2026-11-05", "/admin/audit?page=9" })
                AssertAccessible("owner " + page, await owner.Client.GetStringAsync(page, Token));

            // The pages a refused action answers with: a field refused, an account already listed, a stale edit, and a stale
            // removal of a row no account number can be
            var refusals = new (string What, Func<Task<HttpResponseMessage>> Send, HttpStatusCode Status)[]
            {
                ("an add refused for its SteamID", () => BlacklistSteps.AddAsync(owner, ("steamid", "https://steamcommunity.com/id/someone"), ("name", "x")), HttpStatusCode.BadRequest),
                ("an add refused for its fields", () => BlacklistSteps.AddAsync(owner, ("steamid", "54321"), ("name", new string('n', 61)), ("expires", "1999-01-01")), HttpStatusCode.BadRequest),
                ("an add of an account already listed", () => BlacklistSteps.AddAsync(owner, ("steamid", "12345")), HttpStatusCode.Conflict),
                ("a stale edit", () => BlacklistSteps.EditAsync(owner, 12345, page12345, ("reason", "Stale again")), HttpStatusCode.Conflict),
                ("an edit refused for its fields", async () => await BlacklistSteps.EditAsync(owner, 12345, await BlacklistSteps.OpenAsync(owner, 12345), ("notes", "a\tb")), HttpStatusCode.BadRequest),
                ("a stale removal of a row with no account number", () => BlacklistSteps.CraftedAsync(owner, "/admin/blacklist?handler=RemoveNumber", page12345.Token,
                    ("number", "5000000000"), ("version", new string('a', 64))), HttpStatusCode.Conflict)
            };

            // One at a time, as one browser sends them
            foreach (var (what, send, status) in refusals)
            {
                var answered = await send();
                Assert.True(answered.StatusCode == status, $"{what} answered {answered.StatusCode}");
                AssertAccessible(what, await answered.Content.ReadAsStringAsync(Token));
            }

            // An Admin's audit log has no address column
            var admin = factory.Visitor("203.0.113.74");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            AssertAccessible("admin /admin/audit", await admin.Client.GetStringAsync("/admin/audit", Token));
        }

        // A link, not a form: browsers apply the form-action policy to the redirect after a form, and discord.com isn't 'self'
        [Fact]
        public async Task HomeSignInIsALink()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var html = await factory.Visitor().Client.GetStringAsync("/", Token);

            Assert.Contains("href=\"/signin?returnUrl=%2Fadmin\"", html);
            Assert.Contains("Sign in with Discord", html);
            Assert.DoesNotContain("<form", html);
            Assert.Contains("MinerBot is a Discord bot for Murder Miners", html);
            Assert.Contains("two-factor", html);
        }

        [Fact]
        public async Task DeniedExplainsEachReason()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var visitor = factory.Visitor();
            var sentences = new Dictionary<string, string>
            {
                ["unlisted"] = "Your Discord account isn't on the dashboard's list",
                ["mfa"] = "Turn it on in Discord under User Settings, My Account",
                ["scope"] = "Discord gave the dashboard different permissions",
                ["cancelled"] = "You cancelled the sign-in at Discord",
                ["failed"] = "The sign-in with Discord didn't finish"
            };

            foreach (var (reason, sentence) in sentences)
            {
                var html = WebUtility.HtmlDecode(await visitor.Client.GetStringAsync("/denied?reason=" + reason, Token));

                Assert.Contains(sentence, html);
                Assert.All(sentences.Where(other => other.Key != reason), other => Assert.DoesNotContain(other.Value, html));
            }

            // Only the people on the list can use it, so signing in again is offered to everyone else
            Assert.DoesNotContain("Sign in again", await visitor.Client.GetStringAsync("/denied?reason=unlisted", Token));
            Assert.Contains("Sign in again", await visitor.Client.GetStringAsync("/denied?reason=mfa", Token));

            // A reason the page doesn't know is never shown as it came
            var crafted = await visitor.Client.GetStringAsync("/denied?reason=" + Uri.EscapeDataString("<b>x</b>"), Token);
            Assert.Contains("You can't open that page.", WebUtility.HtmlDecode(crafted));
            Assert.DoesNotContain("<b>x</b>", WebUtility.HtmlDecode(crafted));
            Assert.DoesNotContain("<b>x</b>", crafted);
        }

        [Fact]
        public async Task OverviewShowsWhoIsSignedIn()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var owner = factory.Visitor("203.0.113.71");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var ownerPage = WebUtility.HtmlDecode(await owner.Client.GetStringAsync("/admin", Token));
            Assert.Contains($"You're signed in as Test Owner, Discord ID {TestUsers.Owner.IdText}, with the Owner role.", ownerPage);

            // What it's for, in its sections' order
            var headings = Regex.Matches(ownerPage, "<h2 id=\"[a-z]+\">([^<]+)</h2>").Select(match => match.Groups[1].Value);
            Assert.Equal(["What needs you", "Status", "The last notification run", "Pending armor", "The latest changes"], headings);

            var admin = factory.Visitor("203.0.113.72");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            var adminPage = WebUtility.HtmlDecode(await admin.Client.GetStringAsync("/admin", Token));
            Assert.Contains($"You're signed in as Test Admin, Discord ID {TestUsers.Admin.IdText}, with the Admin role.", adminPage);
            Assert.Contains("Signed in as Test Admin (Admin)", adminPage);
        }

        // Discord names are anyone's to choose, so they're only ever shown encoded
        [Fact]
        public async Task DisplayNameIsEncoded()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var admin = factory.Visitor();
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin with { GlobalName = "<script>alert(1)</script>" });

            var html = await admin.Client.GetStringAsync("/admin", Token);

            // The Overview loads refresh.js, which AssertAccessible allows, and nothing else
            Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
            AssertAccessible("the Overview with a made-up name", html);
        }

        // The status pages' script, as JavaScript, which nosniff needs before a browser runs it, in ASCII, since it's served
        // without a charset, and sending the headers the dashboard checks. Only the pages whose status box refreshes load it.
        [Fact]
        public async Task RefreshScriptIsServedAndOnlyWhereItsUsed()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyBotAsync());

            var response = await factory.Visitor().Client.GetAsync("/js/refresh.js", Token);
            var js = await response.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
            Assert.All(js, c => Assert.True(c < 128, $"refresh.js holds {(int)c}, which isn't ASCII"));
            Assert.Contains($"'{Auth.BackgroundRefresh.Header}': '{Auth.BackgroundRefresh.Value}'", js);
            Assert.Contains($"response.headers.get('{Auth.BackgroundRefresh.FragmentHeader}') !== fragment", js);
            Assert.Contains("redirect: 'manual'", js);
            Assert.Contains("cache: 'no-store'", js);
            Assert.DoesNotContain("innerHTML", js.Replace("box.innerHTML = html;", ""));

            // Every 30 seconds, only while the page is visible, and only from the dashboard's own address
            Assert.Contains("var every = 30000;", js);
            Assert.Contains("if (stopped || document.visibilityState !== 'visible') {", js);
            Assert.Contains("if (address.origin !== window.location.origin || !fragment) {", js);

            // Coming back to a page hidden for longer than the wait refreshes at once and starts the 30 seconds again, since
            // the interval keeps its own count while the page is hidden and its next tick could follow a moment later
            var visible = VisibleHandler().Match(js);
            Assert.True(visible.Success, "refresh.js has no visible() handler");
            Assert.Contains("refresh();", visible.Value);
            Assert.Contains("window.clearInterval(timer);", visible.Value);
            Assert.Contains("timer = window.setInterval(refresh, every);", visible.Value);

            var owner = factory.Visitor("203.0.113.75");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            foreach (var page in new[] { "/admin", "/admin/bot" })
                Assert.True(LoadsRefreshScript(await owner.Client.GetStringAsync(page, Token)), $"{page} doesn't load refresh.js");

            foreach (var page in new[] { "/admin/servers", "/admin/armor", "/admin/blacklist", "/admin/audit", "/" })
                Assert.False(LoadsRefreshScript(await owner.Client.GetStringAsync(page, Token)), $"{page} loads refresh.js");
        }

        [Fact]
        public async Task StylesheetFollowsTheDevice()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var response = await factory.Visitor().Client.GetAsync("/css/site.css", Token);
            var css = await response.Content.ReadAsStringAsync(Token);

            Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("prefers-color-scheme: dark", css);
            Assert.Contains("color-scheme: light dark", css);
            Assert.Contains(":focus-visible", css);
        }

        [Fact]
        public async Task SignOutPageWhenSignedOut()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var html = WebUtility.HtmlDecode(await factory.Visitor().Client.GetStringAsync("/signout", Token));

            Assert.Contains("You're not signed in.", html);
            Assert.DoesNotContain("<form", html);
        }

        [GeneratedRegex("<title>[^<]*\\S[^<]*</title>")]
        private static partial Regex NonEmptyTitle();

        [GeneratedRegex("<script src=\"/js/refresh\\.js\\?v=[A-Za-z0-9_-]+\" defer></script>")]
        private static partial Regex RefreshScript();

        // refresh.js's visibilitychange handler, up to the brace that closes it at its own indent
        [GeneratedRegex("function visible\\(\\) \\{.*?\\n        \\}", RegexOptions.Singleline)]
        private static partial Regex VisibleHandler();

        [GeneratedRegex("<div class=\"table-wrap\"[^>]*>")]
        private static partial Regex TableWrap();

        [GeneratedRegex("</?div\\b[^>]*>")]
        private static partial Regex DivTag();

        // What takes the keyboard's focus without a tabindex
        [GeneratedRegex("<(a|button|input|select|textarea|summary)\\b")]
        private static partial Regex Focusable();

        [GeneratedRegex("^<div class=\"table-wrap\" tabindex=\"0\" role=\"group\" aria-labelledby=\"([^\"]+)\">$")]
        private static partial Regex FocusableGroup();

        // A th or a td with its attributes, holding only a day, or a day and a time in UTC, as TimeWords writes them
        [GeneratedRegex("<(td|th)((?: [a-z-]+=\"[^\"]*\")*)>\\s*\\d{4}-\\d{2}-\\d{2}(?: \\d{2}:\\d{2}:\\d{2} UTC)?\\s*</\\1>")]
        private static partial Regex TimeCell();
    }
}
