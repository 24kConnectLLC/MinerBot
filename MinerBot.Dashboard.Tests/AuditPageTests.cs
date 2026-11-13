using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // The audit log's page: every row newest first, its filters, each row's text revealed, links made only from IDs that
    // parsed, and the visitors' addresses for the Owner alone
    [Collection("Database")]
    public partial class AuditPageTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static readonly DateTime Noon = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

        // Made up, and none of them a test user who signs in, so a person filter finds only these rows
        const ulong First = 1000000000000000011;
        const ulong Second = 1000000000000000012;
        const ulong Guild = 1000000000000000005;

        static AuditEntry Row(string detail, DateTime at, string action = "armor.approve", ulong? actor = First, string role = AuditRole.Admin,
            string outcome = AuditOutcome.Ok, string? targetType = "armor", string? targetId = "31", ulong? guild = null, string? ip = "192.0.2.10") => new()
        {
            at = at,
            actor_id = actor,
            actor_name = actor == null ? null : "Seeded Person",
            actor_role = role,
            action = action,
            outcome = outcome,
            guild_id = guild,
            target_type = targetType,
            target_id = targetId,
            detail = detail,
            ip = ip
        };

        static async Task AddAsync(MariaDbFixture db, params AuditEntry[] rows)
        {
            foreach (var row in rows)
                await db.Audit().AddAudit(row);
        }

        // The seeded rows a page shows, by their details, in its order
        static IReadOnlyList<string> Shown(string html) => SeededDetail().Matches(WebUtility.HtmlDecode(html)).Select(match => match.Value).ToList();

        [Fact]
        public async Task OwnerSeesAddressesAndAnAdminNone()
        {
            var db = await fixture.ReadyAsync();
            await AddAsync(db, Row("Row A", Noon, ip: "192.0.2.10"), Row("Row B", Noon.AddMinutes(1), "blacklist.add", targetType: "blacklist", targetId: "12345", ip: "2001:db8::77"));
            await db.AddEntriesAsync(BlacklistSteps.Entry(12345));
            await using var factory = new DashboardFactory(db);
            var owner = await ArmorSteps.ReviewerAsync(factory, "198.51.100.80", TestUsers.Owner);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.81", TestUsers.Admin);

            var ownerPage = await owner.Client.GetStringAsync("/admin/audit", Token);
            Assert.Contains("<th scope=\"col\">Address</th>", ownerPage);
            Assert.Contains("192.0.2.10", ownerPage);
            Assert.Contains("2001:db8::77", ownerPage);
            Assert.Contains("198.51.100.81", ownerPage);
            PageTests.AssertAccessible("the Owner's audit log", ownerPage);

            // Nowhere in an Admin's pages, not their own address, not the Owner's, and not in an entry's history
            foreach (var path in new[] { "/admin/audit", "/admin/audit?action=armor.approve", "/admin/audit?type=blacklist&id=12345", "/admin/audit?person=" + TestUsers.Owner.IdText,
                "/admin/blacklist/12345" })
            {
                var html = await admin.Client.GetStringAsync(path, Token);

                foreach (var address in new[] { "192.0.2.10", "2001:db8::77", "198.51.100.80", "198.51.100.81" })
                    Assert.False(html.Contains(address, StringComparison.Ordinal), $"An Admin's {path} shows {address}");

                Assert.DoesNotContain("<th scope=\"col\">Address</th>", html);
            }

            Assert.Contains("Each row's visitor address is shown to the Owner alone.", WebUtility.HtmlDecode(await admin.Client.GetStringAsync("/admin/audit", Token)));
            PageTests.AssertAccessible("an Admin's audit log", await admin.Client.GetStringAsync("/admin/audit", Token));
        }

        // What the log holds, as the code writes it, and what it leaves out, so nobody looks in it for a refusal it never has
        [Fact]
        public async Task PageSaysWhatTheLogHolds()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.94");
            var text = WebUtility.HtmlDecode(await admin.Client.GetStringAsync("/admin/audit", Token));

            Assert.Contains("Every change made on the dashboard, every sign-in and sign-out, and the changes and sign-ins it refused, newest first.", text);
            Assert.Contains("A form sent back to correct a field, a form that had expired or was incomplete, an action on armor that had since been removed, a cancelled or failed sign-in, and a request turned away before it reached its page, such as one from another site or one over the rate limit, aren't recorded, but the request that ends the sessions of someone taken off the lists is.", text);
            Assert.DoesNotContain("every refusal", text);
        }

        [Fact]
        public async Task EachFilterNarrowsTheRows()
        {
            var db = await fixture.ReadyAsync();
            await AddAsync(db,
                Row("Row A", Noon, "auth.signin", First, AuditRole.Owner, targetType: "session", targetId: null),
                Row("Row B", Noon.AddMinutes(1), "armor.approve", Second),
                Row("Row C", Noon.AddDays(1), "blacklist.add", Second, targetType: "blacklist", targetId: "12345"),
                Row("Row D", Noon.AddDays(2), "blacklist.edit", First, targetType: "blacklist", targetId: "12345", guild: Guild),
                Row("Row E", Noon.AddDays(-1), "auth.refused", null, AuditRole.Visitor, AuditOutcome.Refused, "session", null));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.82");

            async Task<IReadOnlyList<string>> ShownAsync(string query) => Shown(await admin.Client.GetStringAsync("/admin/audit" + query, Token));

            Assert.Equal(["Row D", "Row C", "Row B", "Row A", "Row E"], await ShownAsync(""));
            Assert.Equal(["Row C", "Row B"], await ShownAsync($"?person={Second}"));
            Assert.Equal(["Row D", "Row A"], await ShownAsync($"?person={First}"));
            Assert.Equal(["Row C"], await ShownAsync("?action=blacklist.add"));
            Assert.Equal(["Row E"], await ShownAsync("?action=auth.refused"));
            Assert.Equal(["Row D"], await ShownAsync($"?server={Guild}"));
            Assert.Equal(["Row D", "Row C"], await ShownAsync("?type=blacklist&id=12345"));
            Assert.Equal(["Row D", "Row C"], await ShownAsync("?type=blacklist"));
            Assert.Equal(["Row B"], await ShownAsync("?type=armor&id=31"));
            Assert.Equal(["Row C"], await ShownAsync($"?person={Second}&type=blacklist"));
            Assert.Equal(["Row B", "Row A"], await ShownAsync("?from=2026-11-05&to=2026-11-05"));
            Assert.Equal(["Row D", "Row C"], await ShownAsync("?from=2026-11-06&to=2026-11-07"));
            Assert.Equal(["Row B", "Row A", "Row E"], await ShownAsync("?to=2026-11-05"));
            Assert.Empty(await ShownAsync("?action=armor.reject"));

            var filtered = await admin.Client.GetStringAsync($"/admin/audit?action=blacklist.add&person={Second}", Token);
            Assert.Contains("<a href=\"/admin/audit\">Clear the filters</a>", filtered);
            Assert.Contains("<option value=\"blacklist.add\" selected>Added to the blacklist</option>", filtered);
            Assert.Equal(Second.ToString(), BlacklistSteps.BoxValue(filtered, "person"));
            Assert.DoesNotContain("Clear the filters", await admin.Client.GetStringAsync("/admin/audit", Token));
            Assert.Contains("No row matches these filters.", await admin.Client.GetStringAsync("/admin/audit?action=armor.reject", Token));

            // The filter's select offers every word the dashboard writes, each by its label
            foreach (var word in AuditWords.Actions)
                Assert.Contains($"<option value=\"{word.Word}\">{word.Label}</option>", WebUtility.HtmlDecode(await admin.Client.GetStringAsync("/admin/audit", Token)).Replace(" selected", ""));

            // Each row's Discord ID links to that person's rows, and the type and ID say what they're about
            Assert.Contains($"<a href=\"/admin/audit?person={Second}\">Discord ID {Second}</a>", filtered);
            Assert.Contains("Only the rows about <a href=\"/admin/blacklist/12345\">Blacklist entry [U:1:12345]</a>",
                Regex.Replace(await admin.Client.GetStringAsync("/admin/audit?type=blacklist&id=12345", Token), @"\s+", " "));
        }

        // From's day from its start and To's to its end, in UTC, so days next to each other share no row
        [Fact]
        public async Task DaysAreUtcAndBothIncluded()
        {
            var db = await fixture.ReadyAsync();
            var day = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
            await AddAsync(db,
                Row("Row A", day.AddMilliseconds(-1)),
                Row("Row B", day),
                Row("Row C", day.AddDays(1).AddMilliseconds(-1)),
                Row("Row D", day.AddDays(1)));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.83");

            Assert.Equal(["Row C", "Row B"], Shown(await admin.Client.GetStringAsync("/admin/audit?from=2026-11-05&to=2026-11-05", Token)));
            Assert.Equal(["Row A"], Shown(await admin.Client.GetStringAsync("/admin/audit?to=2026-11-04", Token)));
            Assert.Contains("2026-11-05 23:59:59 UTC", await admin.Client.GetStringAsync("/admin/audit?from=2026-11-05&to=2026-11-05", Token));
        }

        // Not an error: the page says why nothing matches, and asks the database nothing
        [Fact]
        public async Task StartAfterEndSaysSoWithNoRows()
        {
            var db = await fixture.ReadyAsync();
            await AddAsync(db, Row("Row A", Noon));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.84");

            var response = await admin.Client.GetAsync("/admin/audit?from=2026-11-06&to=2026-11-05", Token);
            var html = await response.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("The From day is after the To day, so no row can match. Change one of them.", WebUtility.HtmlDecode(html));
            Assert.Empty(Shown(html));
            Assert.Equal("2026-11-06", BlacklistSteps.BoxValue(html, "from"));
            PageTests.AssertAccessible("an audit log whose days are the wrong way round", html);
        }

        // An ID box's pattern lets through 20 digits too big for any Discord ID, such as one with a digit too many. That's
        // no one's ID, so it's a filter no row matches: the box keeps it, and the page says so, rather than that the form
        // expired. A row with the biggest ID there is stays out too.
        [Theory]
        [InlineData("person", "18446744073709551616")]
        [InlineData("person", "99999999999999999999")]
        [InlineData("server", "18446744073709551616")]
        [InlineData("server", "20000000000000000000")]
        public async Task IdTooBigForAnyoneMatchesNoRow(string box, string id)
        {
            var db = await fixture.ReadyAsync();
            await AddAsync(db, Row("Row A", Noon, actor: ulong.MaxValue, guild: ulong.MaxValue), Row("Row B", Noon.AddMinutes(1)));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.93");

            var response = await admin.Client.GetAsync($"/admin/audit?{box}={id}", Token);
            var html = await response.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("No row matches these filters.", WebUtility.HtmlDecode(html));
            Assert.Empty(Shown(html));
            Assert.Equal(id, BlacklistSteps.BoxValue(html, box));
            Assert.Contains("<a href=\"/admin/audit\">Clear the filters</a>", html);
            PageTests.AssertAccessible("an audit log filtered by an ID too big for anyone", html);

            // The biggest ID there is still finds its row
            Assert.Equal(["Row A"], Shown(await admin.Client.GetStringAsync($"/admin/audit?{box}={ulong.MaxValue}", Token)));
        }

        // The boxes' patterns, the select and the date boxes' limits send nothing else, but for an ID too big for anyone
        // (above), so anything else is an address someone made up. 21 digits is past the ID boxes' pattern and maxlength.
        [Theory]
        [InlineData("person=abc")]
        [InlineData("person=1.5")]
        [InlineData("person=-1")]
        [InlineData("person=%201")]
        [InlineData("person=1%0A")]
        [InlineData("person=100000000000000000000")]
        [InlineData("server=100000000000000000000")]
        [InlineData("server=abc")]
        [InlineData("action=armor.approved")]
        [InlineData("action=ARMOR.APPROVE")]
        [InlineData("action=test.anything")]
        [InlineData("type=guild")]
        [InlineData("type=Armor")]
        [InlineData("id=abc")]
        [InlineData("id=1%0A")]
        [InlineData("id=1234567890123456789012345678901234567890")]
        [InlineData("from=2026-13-01")]
        [InlineData("from=2026-11-5")]
        [InlineData("from=1999-12-31")]
        [InlineData("to=9999-01-01")]
        [InlineData("to=05/11/2026")]
        public async Task MadeUpFiltersAreRefused(string query)
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.85");

            Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.GetAsync("/admin/audit?" + query, Token)).StatusCode);
        }

        // The limits themselves pass: the longest ID, a negative number no account can be, and the boxes' first and last days
        [Theory]
        [InlineData("person=18446744073709551615")]
        [InlineData("id=-123456789012345678901234567890123456789")]
        [InlineData("type=session")]
        [InlineData("from=2000-01-01&to=9998-12-31")]
        [InlineData("page=abc")]
        public async Task FiltersAtTheirLimitsPass(string query)
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.86");

            Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync("/admin/audit?" + query, Token)).StatusCode);
        }

        // 25 to a page, and every link to another page keeps every filter
        [Fact]
        public async Task PagingKeepsEveryFilter()
        {
            var db = await fixture.ReadyAsync();
            await AddAsync(db, [.. Enumerable.Range(0, 30).Select(i => Row($"Row {(char)('A' + i % 26)}{i:00}", Noon.AddMinutes(i), "armor.reject", Second))]);
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.87");
            const string filters = "?person=1000000000000000012&action=armor.reject&from=2026-11-05&to=2026-11-05";

            var first = await admin.Client.GetStringAsync("/admin/audit" + filters, Token);
            Assert.Equal(25, BlacklistSteps.AuditRowCount(first));
            Assert.Contains("30 rows that match the filters, newest first.", WebUtility.HtmlDecode(first));
            Assert.Contains("href=\"/admin/audit?person=1000000000000000012&amp;action=armor.reject&amp;from=2026-11-05&amp;to=2026-11-05&amp;page=2\">Next page</a>", first);

            var second = await admin.Client.GetStringAsync("/admin/audit" + filters + "&page=2", Token);
            Assert.Equal(5, BlacklistSteps.AuditRowCount(second));
            Assert.Contains("href=\"/admin/audit?person=1000000000000000012&amp;action=armor.reject&amp;from=2026-11-05&amp;to=2026-11-05\">Previous page</a>", second);

            Assert.Equal(25, BlacklistSteps.AuditRowCount(await admin.Client.GetStringAsync("/admin/audit" + filters + "&page=0", Token)));
            Assert.Contains("There's nothing on page 7. <a href=\"/admin/audit?person=1000000000000000012&action=armor.reject&from=2026-11-05&to=2026-11-05\">Go to the first page</a>.",
                WebUtility.HtmlDecode(await admin.Client.GetStringAsync("/admin/audit" + filters + "&page=7", Token)));
        }

        // Names, JSON values and details are whoever wrote them, through RevealedText. The JSON is parsed, since System.Text.Json
        // writes every character outside ASCII as an escape; JSON that doesn't parse, or isn't an object, is shown as it is.
        [Fact]
        public async Task NamesJsonAndDetailsAreRevealed()
        {
            var db = await fixture.ReadyAsync();
            var named = Row("Row A", Noon);
            named.actor_name = "Night\U0000200BRaid";
            named.detail = "Row A <b>bold</b>";
            named.before_json = """{"Name":"\u0627\u0644\u0644\u0627\u0639\u0628","Reason":null,"Notes":"","Ticked":["HiddenCharacters:Name","Made:Up"],"Status":"evil\u202Etxt"}""";
            named.after_json = """["a","b"]""";

            var broken = Row("Row B", Noon.AddMinutes(1));
            broken.before_json = """{"Name":""";
            broken.after_json = """{"Notes":"<script>x</script>"}""";

            var cut = Row("Row C", Noon.AddMinutes(2));
            cut.after_json = $$"""{"Notes":"{{new string('n', 400)}}"}""";

            await AddAsync(db, named, broken, cut);
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.88");

            var html = await admin.Client.GetStringAsync("/admin/audit", Token);

            Assert.Contains("Night<mark class=\"hidden-char\" title=\"zero-width space\">[U&#x2B;200B]</mark>Raid", html);
            Assert.Contains("Row A &lt;b&gt;bold&lt;/b&gt;", html);
            Assert.DoesNotContain("<b>bold", html);

            // Parsed, so the name reads in its own script, a null is none, an empty text is empty, a list is joined, and a
            // ticked warning is in the armor pages' words
            var text = WebUtility.HtmlDecode(html);
            Assert.Contains("\u0627\u0644\u0644\u0627\u0639\u0628", text);
            Assert.DoesNotContain("\\u0627", text);
            Assert.Matches("<dt><bdi class=\"revealed\">Reason</bdi></dt>\\s*<dd>none</dd>", html);
            Assert.Matches("<dt><bdi class=\"revealed\">Notes</bdi></dt>\\s*<dd>empty</dd>", html);
            Assert.Contains("Hidden characters (name), Made:Up", text);
            Assert.Contains("evil<mark class=\"hidden-char\" title=\"right-to-left override\">[U&#x2B;202E]</mark>txt", html);

            // As it's stored, revealed
            Assert.Contains("[&quot;a&quot;,&quot;b&quot;]", html);
            Assert.Contains("{&quot;Name&quot;:", html);
            Assert.Contains("&lt;script&gt;x&lt;/script&gt;", html);
            Assert.DoesNotContain("<script", html);

            // Cut short, never whole
            Assert.Contains(new string('n', AuditRows.ValueLimit) + "…", text);
            Assert.DoesNotContain(new string('n', AuditRows.ValueLimit + 1), text);
            PageTests.AssertAccessible("an audit log with revealed text", html);
        }

        // A target links to its page only when its ID parsed as one of the dashboard's: an armor's long, or an account
        // number. Anything else is text, so a row someone wrote by hand can't make the page link anywhere.
        [Fact]
        public async Task TargetLinksAreMadeOnlyFromParsedIds()
        {
            var db = await fixture.ReadyAsync();
            await AddAsync(db,
                Row("Row A", Noon, targetType: "armor", targetId: "31"),
                Row("Row B", Noon, targetType: "armor", targetId: "31abc"),
                Row("Row C", Noon, "blacklist.add", targetType: "blacklist", targetId: "12345"),
                Row("Row D", Noon, "blacklist.remove", targetType: "blacklist", targetId: "5000000000"),
                Row("Row E", Noon, "blacklist.remove", targetType: "blacklist", targetId: "-1"),
                Row("Row F", Noon, "blacklist.remove", targetType: "blacklist", targetId: "javascript:alert(1)"),
                Row("Row G", Noon, "auth.signin", targetType: "session", targetId: null),
                Row("Row H", Noon, "guild.settings.save", targetType: "guild", targetId: "/admin/x"));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.89");

            var html = await admin.Client.GetStringAsync("/admin/audit", Token);

            Assert.Contains("<a href=\"/admin/armor/31\">Armor 31</a>", html);
            Assert.Contains("<a href=\"/admin/blacklist/12345\">Blacklist entry [U:1:12345]</a>", html);
            Assert.Contains("Armor 31abc", html);
            Assert.Contains("Blacklist entry 5000000000", html);
            Assert.Contains("Blacklist entry -1", html);
            Assert.Contains("Blacklist entry javascript:alert(1)", html);
            Assert.Contains("guild /admin/x", html);
            Assert.Contains("guild.settings.save", html);

            var targets = Regex.Matches(html, "href=\"(/admin/(?:armor|blacklist)/[^\"]*)\"").Select(match => match.Groups[1].Value).Distinct().Order(StringComparer.Ordinal);
            Assert.Equal(["/admin/armor/31", "/admin/blacklist/12345"], targets);
            Assert.DoesNotContain("href=\"javascript", html);
            Assert.DoesNotContain("href=\"/admin/x", html);
        }

        // A sign-in refused for its permissions, before Discord said who it was, has no person, and no role: no list was
        // checked for them. Someone Discord named who is on none of the lists is said to be.
        [Fact]
        public async Task RowWithNoPersonSaysDiscordDidntNameThem()
        {
            var db = await fixture.ReadyAsync();
            await AddAsync(db,
                Row("Row A", Noon, "auth.refused", null, AuditRole.Visitor, AuditOutcome.Refused, "session", null),
                Row("Row B", Noon.AddDays(1), "auth.refused", First, AuditRole.Visitor, AuditOutcome.Refused, "session", null));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.90");

            var text = WebUtility.HtmlDecode(await admin.Client.GetStringAsync("/admin/audit?action=auth.refused&to=2026-11-05", Token));

            Assert.Equal(["Row A"], Shown(text));
            Assert.Contains("Someone Discord didn't name", text);
            Assert.DoesNotContain("On no list", text);
            Assert.Contains("Refused or ended a session", text);
            Assert.Contains("Refused", text);

            var named = WebUtility.HtmlDecode(await admin.Client.GetStringAsync($"/admin/audit?action=auth.refused&person={First}", Token));
            Assert.Equal(["Row B"], Shown(named));
            Assert.Contains("On no list", named);
        }

        // An escaped half of a character, which JsonDocument takes but then can't read as text, in a value, a key or a ticked
        // warning: shown as it's stored, as any JSON that can't be read is, on the audit log and in an entry's history,
        // never as an error page. The dashboard can't write one, since System.Text.Json writes a lone half as U+FFFD, but
        // another writer can.
        [Fact]
        public async Task JsonWithHalfACharacterIsShownAsItIs()
        {
            var db = await fixture.ReadyAsync();
            var value = Row("Row A", Noon, "blacklist.edit", targetType: "blacklist", targetId: "12345");
            value.after_json = """{"Name":"abc\ud83d"}""";
            var key = Row("Row B", Noon.AddMinutes(1));
            key.before_json = """{"\ud800":"x"}""";
            var ticked = Row("Row C", Noon.AddMinutes(2));
            ticked.after_json = """{"Ticked":["\ud800"]}""";
            await AddAsync(db, value, key, ticked);
            await db.AddEntriesAsync(BlacklistSteps.Entry(12345));
            await using var factory = new DashboardFactory(db);
            var owner = await ArmorSteps.ReviewerAsync(factory, "198.51.100.91", TestUsers.Owner);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.92");

            foreach (var (who, browser) in new[] { ("the Owner", owner), ("an Admin", admin) })
            {
                var answers = new List<(string Path, HttpStatusCode Status, string Text)>();

                foreach (var path in new[] { "/admin/audit", "/admin/blacklist/12345" })
                {
                    var response = await browser.Client.GetAsync(path, Token);
                    answers.Add((path, response.StatusCode, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token))));
                }

                Assert.True(answers.All(answer => answer.Status == HttpStatusCode.OK),
                    $"For {who}, " + string.Join(", ", answers.Select(answer => $"{answer.Path} answered {answer.Status}")));
                Assert.All(answers, answer => Assert.Contains("""<p class="address"><bdi class="revealed">{"Name":"abc\ud83d"}</bdi></p>""", answer.Text));
            }

            var audit = WebUtility.HtmlDecode(await owner.Client.GetStringAsync("/admin/audit", Token));
            Assert.Contains("""<bdi class="revealed">{"\ud800":"x"}</bdi>""", audit);
            Assert.Contains("""<bdi class="revealed">{"Ticked":["\ud800"]}</bdi>""", audit);
        }

        [GeneratedRegex("Row [A-Z][0-9]*")]
        private static partial Regex SeededDetail();
    }
}
