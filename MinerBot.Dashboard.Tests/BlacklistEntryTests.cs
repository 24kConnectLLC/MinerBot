using System.Net;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Blacklist;
using MinerBot.Dashboard.Tests.Support;
using static MinerBot.Dashboard.Tests.Support.BlacklistSteps;

namespace MinerBot.Dashboard.Tests
{
    // One account's page: every way of writing the account, the entry, its history on the dashboard and its forms
    [Collection("Database")]
    public class BlacklistEntryTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static readonly DateTime Noon = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task EntryShowsEveryWayOfWritingItsAccount()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345, "Night\U0000200BRaid", "<b>Cheating</b>", new DateTime(2001, 2, 3, 4, 5, 6), "Seen @here"));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.60");

            var page = await OpenAsync(admin, 12345);

            Assert.Contains($"<dd>{SteamId64}</dd>", page.Html);
            Assert.Contains("<dd>[U:1:12345]</dd>", page.Html);
            Assert.Contains("<dd>STEAM_0:1:6172</dd>", page.Html);
            Assert.Contains($"<a href=\"https://steamcommunity.com/profiles/{SteamId64}\" rel=\"noreferrer\">", page.Html);
            Assert.Contains("<title>Blacklist entry [U:1:12345] - MinerBot dashboard</title>", page.Html);

            // Everything typed into it is revealed, and the title holds none of it
            Assert.Contains("Night<mark class=\"hidden-char\" title=\"zero-width space\">[U&#x2B;200B]</mark>Raid", page.Html);
            Assert.Contains("&lt;b&gt;Cheating&lt;/b&gt;", page.Html);
            Assert.Contains("<mark class=\"mention\" title=\"Discord mention\">@here</mark>", page.Html);
            Assert.Contains("2001-02-03 04:05 UTC", page.Text);
            Assert.Contains(">Expired<", page.Html);

            // Each button says what it does, and the edit form starts with the entry's own values
            Assert.Contains("aria-describedby=\"edit-help\">Save</button>", page.Html);
            Assert.Contains("aria-describedby=\"remove-help\">Remove from the blacklist</button>", page.Html);
            Assert.Equal("Night\U0000200BRaid", page.Boxes["name"]);
            Assert.Equal("2001-02-03", page.Boxes["expires"]);
            Assert.Contains("max=\"9998-12-31\"", page.Html);
            Assert.Contains(BlacklistWords.ExpiresHint, page.Text);
            Assert.Contains("No changes made on the dashboard yet.", page.Text);
            PageTests.AssertAccessible("a blacklist entry", page.Html);
        }

        // Account 0 isn't a Steam account, and SteamIds.ToSteamId64 throws for it, so it has no SteamID64 or profile
        [Fact]
        public async Task AccountZeroHasNoSteamId64OrProfile()
        {
            var db = await fixture.ReadyAsync();
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username) VALUES (0, 'Zero Player')");
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.61");

            var page = await OpenAsync(admin, 0);

            Assert.Contains("<dd>[U:1:0]</dd>", page.Html);
            Assert.Contains("Account number 0 isn't a Steam account", page.Text);
            Assert.DoesNotContain("steamcommunity.com", page.Html);
            Assert.DoesNotContain("STEAM_0", page.Html);
            Assert.DoesNotContain("<dt>SteamID64</dt>", page.Html);
            Assert.DoesNotContain("<dt>Profile</dt>", page.Html);
            Assert.Contains("handler=Remove", page.Html);
        }

        // The table's account numbers are the 32-bit ones, and an account with no row and no history has no page
        [Theory]
        [InlineData("/admin/blacklist/4294967296")]
        [InlineData("/admin/blacklist/-1")]
        [InlineData("/admin/blacklist/99999")]
        [InlineData("/admin/blacklist/9223372036854775808")]
        [InlineData("/admin/blacklist/abc")]
        [InlineData("/admin/blacklist/1.5")]
        public async Task AccountWithNoPageIs404(string path)
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.62");

            Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.GetAsync(path, Token)).StatusCode);
        }

        // An action's message shows only while the entry is as the action left it, so a reload after someone else's change,
        // or a link someone made, can't say something happened that didn't
        [Fact]
        public async Task DoneNoticesShowOnlyWhileTheyHold()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345));
            await db.Audit().AddAudit(new AuditEntry
            {
                at = Noon,
                actor_id = TestUsers.Admin.Id,
                actor_name = "Test Admin",
                actor_role = AuditRole.Admin,
                action = "blacklist.remove",
                outcome = AuditOutcome.Ok,
                target_type = "blacklist",
                target_id = "54321"
            });
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.63");

            async Task<string> TextAsync(string path) => WebUtility.HtmlDecode(await admin.Client.GetStringAsync(path, Token));

            Assert.Contains(BlacklistWords.Added, await TextAsync("/admin/blacklist/12345?done=added"));
            Assert.Contains(BlacklistWords.Saved, await TextAsync("/admin/blacklist/12345?done=saved"));
            Assert.DoesNotContain(BlacklistWords.Removed, await TextAsync("/admin/blacklist/12345?done=removed"));
            Assert.DoesNotContain("role=\"status\"", await admin.Client.GetStringAsync("/admin/blacklist/12345?done=%3Cb%3Eadded%3C%2Fb%3E", Token));

            // No row, but history: the page says it isn't on the blacklist, and offers to add it again
            var gone = await TextAsync("/admin/blacklist/54321?done=removed");
            Assert.Contains(BlacklistWords.Removed, gone);
            Assert.Contains("This account isn't on the blacklist.", gone);
            Assert.Contains("Add it to the blacklist", gone);
            Assert.DoesNotContain(BlacklistWords.Added, await TextAsync("/admin/blacklist/54321?done=added"));
            Assert.DoesNotContain(BlacklistWords.Saved, await TextAsync("/admin/blacklist/54321?done=saved"));
            PageTests.AssertAccessible("an account no longer on the blacklist", await admin.Client.GetStringAsync("/admin/blacklist/54321", Token));
        }

        // The entry's audit rows, newest first, the latest 25, with the rest on the audit page. Never an address, even for
        // the Owner, who sees them on the audit page.
        [Fact]
        public async Task HistoryIsNewestFirstWithNoAddresses()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345));
            var audit = db.Audit();

            for (var i = 0; i < 30; i++)
            {
                await audit.AddAudit(new AuditEntry
                {
                    at = Noon.AddMinutes(i),
                    actor_id = TestUsers.Owner.Id,
                    actor_name = "Test Owner",
                    actor_role = AuditRole.Owner,
                    action = "blacklist.edit",
                    outcome = AuditOutcome.Ok,
                    target_type = "blacklist",
                    target_id = "12345",
                    detail = $"Change {i:00}",
                    ip = "192.0.2.77"
                });
            }

            // Another entry's row, and a row about armor with the same ID, aren't this entry's
            await audit.AddAudit(new AuditEntry { at = Noon, actor_role = AuditRole.Owner, action = "blacklist.edit", outcome = AuditOutcome.Ok, target_type = "blacklist", target_id = "54321", detail = "Other entry" });
            await audit.AddAudit(new AuditEntry { at = Noon, actor_role = AuditRole.Owner, action = "armor.approve", outcome = AuditOutcome.Ok, target_type = "armor", target_id = "12345", detail = "Armor row" });

            await using var factory = new DashboardFactory(db);
            var owner = await ArmorSteps.ReviewerAsync(factory, "198.51.100.64", TestUsers.Owner);
            var page = await OpenAsync(owner, 12345);

            Assert.Equal(Pages.Admin.Blacklist.BlacklistEntryModel.HistoryLimit, AuditRowCount(page.Html));
            Assert.True(page.Text.IndexOf("Change 29", StringComparison.Ordinal) < page.Text.IndexOf("Change 28", StringComparison.Ordinal));
            Assert.Contains("Change 05", page.Text);
            Assert.DoesNotContain("Change 04", page.Text);
            Assert.DoesNotContain("Other entry", page.Text);
            Assert.DoesNotContain("Armor row", page.Text);
            Assert.Contains("href=\"/admin/audit?type=blacklist&amp;id=12345\">All 30 of this entry's rows on the audit log</a>", page.Html);

            Assert.DoesNotContain("192.0.2.77", page.Html);
            Assert.DoesNotContain("198.51.100.64", page.Html);
            Assert.DoesNotContain("<th scope=\"col\">Address</th>", page.Html);
        }

        // A refused field comes back with what was typed, a message by it, and no change or audit row. The top of the page
        // says nothing was saved, since the messages by the fields come after the entry and its history.
        [Fact]
        public async Task RefusedFieldComesBackWithItsMessage()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.65");

            var response = await EditAsync(admin, 12345, await OpenAsync(admin, 12345), ("reason", new string('r', 61)), ("expires", "1999-01-01"));
            var html = await response.Content.ReadAsStringAsync(Token);
            var text = WebUtility.HtmlDecode(html);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Matches("<div class=\"notice notice-problem\" role=\"alert\">\\s*<p>" + System.Text.RegularExpressions.Regex.Escape(BlacklistWords.NotSaved) + "</p>", text);
            Assert.True(text.IndexOf(BlacklistWords.NotSaved, StringComparison.Ordinal) < text.IndexOf("<h2 id=\"account\">", StringComparison.Ordinal));
            Assert.Contains("<p id=\"reason-problem\" class=\"field-problem\">The reason holds at most 60 characters.</p>", html);
            Assert.Contains("<p id=\"expires-problem\" class=\"field-problem\">" + BlacklistWords.BadDate + "</p>", WebUtility.HtmlDecode(html));
            Assert.Contains("aria-describedby=\"reason-problem reason-hint\" aria-invalid=\"true\"", html);
            Assert.Equal(new string('r', 61), BoxValue(html, "reason"));
            Assert.Equal("1999-01-01", BoxValue(html, "expires"));
            Assert.Contains("max=\"9998-12-31\"", html);
            PageTests.AssertAccessible("an edit refused for its fields", html);

            Assert.Equal("Testing", (await db.EntryAsync(12345))?.reason);
            Assert.Empty(await db.BlacklistAuditAsync());
        }
    }
}
