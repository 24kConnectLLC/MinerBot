using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using DataAccessLibrary;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Blacklist;
using DataAccessLibrary.Blacklist.Models;
using DataAccessLibrary.Tests;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Blacklist;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Tests.Support;
using MySql.Data.MySqlClient;
using static MinerBot.Dashboard.Tests.Support.BlacklistSteps;

namespace MinerBot.Dashboard.Tests
{
    // Add, edit and remove: each one transaction that changes the entry only while it's as the editor saw it, with its
    // audit row, or a refusal that writes only its audit row
    [Collection("Database")]
    public class BlacklistActionTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static readonly DateTime Future = new(2099, 1, 2, 3, 4, 5);

        static void Redirected(HttpResponseMessage response, uint account, string done)
        {
            Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"The action answered {response.StatusCode}");
            Assert.Equal($"/admin/blacklist/{account}?done={done}", SignInSteps.PathOf(response.Headers.Location));
        }

        static async Task Answered(HttpResponseMessage response, HttpStatusCode status, string message)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Contains(message, await ArmorSteps.TextAsync(response));
        }

        static string Json(string? name, string? reason, string? expires, string? notes) =>
            $$"""{"Name":{{Text(name)}},"Reason":{{Text(reason)}},"Expires":{{Text(expires)}},"Notes":{{Text(notes)}}}""";

        static string Text(string? value) => value == null ? "null" : $"\"{value}\"";

        // The columns every change's row has: who, from where and when, as the request had them, and what it's about
        static void AssertRow(AuditEntry row, string action, TestUser user, string role, string ip, DateTime at)
        {
            Assert.Equal(action, row.action);
            Assert.Equal(AuditOutcome.Ok, row.outcome);
            Assert.Equal(user.Id, row.actor_id);
            Assert.Equal(user.DisplayName, row.actor_name);
            Assert.Equal(role, row.actor_role);
            Assert.Null(row.guild_id);
            Assert.Equal("blacklist", row.target_type);
            Assert.Equal(ip, row.ip);
            Assert.False(string.IsNullOrEmpty(row.request_id));
            Assert.Equal(at, DateTime.SpecifyKind(row.at, DateTimeKind.Utc));
        }

        [Fact]
        public async Task AddSavesTheEntryAndItsAuditRow()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await ArmorSteps.ReviewerAsync(factory, "198.51.100.10", TestUsers.Owner);
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var response = await AddAsync(owner, ("steamid", "[U:1:12345]"), ("name", " Test Player "), ("reason", "Cheating"), ("expires", "2027-01-02"),
                ("notes", "Seen in a lobby"));

            Redirected(response, 12345, "added");

            var entry = await db.EntryAsync(12345);
            Assert.Equivalent(Entry(12345, "Test Player", "Cheating", new DateTime(2027, 1, 2), "Seen in a lobby"), entry, strict: true);

            var audit = Assert.Single(await db.BlacklistAuditAsync());
            Assert.Equal("blacklist.add", audit.action);
            Assert.Equal(AuditOutcome.Ok, audit.outcome);
            Assert.Equal(TestUsers.Owner.Id, audit.actor_id);
            Assert.Equal("Test Owner", audit.actor_name);
            Assert.Equal(AuditRole.Owner, audit.actor_role);
            Assert.Equal("blacklist", audit.target_type);
            Assert.Equal("12345", audit.target_id);
            Assert.Null(audit.before_json);
            Assert.Equal(Json("Test Player", "Cheating", "2027-01-02 00:00:00", "Seen in a lobby"), audit.after_json);
            Assert.Null(audit.detail);
            Assert.Equal("198.51.100.10", audit.ip);
            Assert.False(string.IsNullOrEmpty(audit.request_id));
            Assert.Equal(now, DateTime.SpecifyKind(audit.at, DateTimeKind.Utc));

            // The page it leads to says so, and lists the change
            var after = WebUtility.HtmlDecode(await owner.Client.GetStringAsync(SignInSteps.PathOf(response.Headers.Location), Token));
            Assert.Contains(BlacklistWords.Added, after);
            Assert.Contains("Added to the blacklist", after);
            Assert.Contains(now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC", after);

            // What the editor typed is in the table and the audit log, never in a log line
            AssertNotLogged(factory, "Test Player", "Cheating", "Seen in a lobby");
        }

        static void AssertNotLogged(DashboardFactory factory, params string[] typed)
        {
            foreach (var text in typed)
                Assert.False(factory.Logs.Text.Contains(text, StringComparison.Ordinal), $"A log line holds the typed {text}");
        }

        // Every way of writing one account adds that account. Empty fields are none, which the table keeps as NULL.
        [Theory]
        [InlineData("12345")]
        [InlineData(SteamId64)]
        [InlineData("https://steamcommunity.com/profiles/" + SteamId64)]
        [InlineData("steamcommunity.com/profiles/" + SteamId64 + "/")]
        [InlineData("[U:1:12345]")]
        [InlineData("U:1:12345")]
        [InlineData("STEAM_0:1:6172")]
        [InlineData("STEAM_1:1:6172")]
        [InlineData("  [U:1:12345]  ")]
        public async Task AddTakesEveryWayOfWritingASteamId(string steamId)
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.11");

            Redirected(await AddAsync(admin, ("steamid", steamId), ("name", ""), ("reason", ""), ("expires", ""), ("notes", "")), 12345, "added");

            Assert.Equivalent(Entry(12345, null, null, null, null), await db.EntryAsync(12345), strict: true);
            Assert.Equal(Json(null, null, null, null), Assert.Single(await db.BlacklistAuditAsync()).after_json);
        }

        // Each refusal is the form again, with what was typed kept and a fixed message, and nothing written, not even an
        // audit row: these are typing mistakes, as PR 4's 400s are. A custom link's name is never repeated.
        [Theory]
        [InlineData("", "Enter a SteamID, an account number or a Steam profile link")]
        [InlineData("not an id", "That isn't a SteamID, an account number or a Steam profile link")]
        [InlineData("0", "Account number 0 isn't a Steam account")]
        [InlineData("[U:1:0]", "Account number 0 isn't a Steam account")]
        [InlineData("76561197960265728", "That SteamID64 is account 0, which isn't a Steam account")]
        [InlineData("[U:1:4294967296]", "That account number is out of range")]
        [InlineData("https://steamcommunity.com/id/sneakyname", BlacklistWords.CustomLink)]
        [InlineData("steamcommunity.com/id/sneakyname/", BlacklistWords.CustomLink)]
        public async Task AddRefusesWhatIsntASteamAccount(string steamId, string message)
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.12");

            var response = await AddAsync(admin, ("steamid", steamId), ("name", "Kept Name"), ("reason", "Kept Reason"), ("expires", "2027-01-02"), ("notes", "Kept Notes"));
            var html = await response.Content.ReadAsStringAsync(Token);

            await Answered(response, HttpStatusCode.BadRequest, message);
            Assert.Equal(steamId.Trim(), BoxValue(html, "steamid"));
            Assert.Equal("Kept Name", BoxValue(html, "name"));
            Assert.Equal("2027-01-02", BoxValue(html, "expires"));
            Assert.Contains("aria-invalid=\"true\"", html);
            PageTests.AssertAccessible("a refused add", html);

            // Only in the box, where it was typed, and in no log line. The message says where to get the account's number.
            if (steamId.Contains("sneakyname", StringComparison.Ordinal))
            {
                Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(html, "sneakyname"));
                Assert.Contains("MinerBot's /more steam_id command in Discord gives the SteamID64 of a custom link written as https://steamcommunity.com/id/ and the name.",
                    WebUtility.HtmlDecode(html));
                AssertNotLogged(factory, "sneakyname");
            }

            Assert.Equal(0, await db.EntryCountAsync());
            Assert.Empty(await db.BlacklistAuditAsync());
        }

        // The columns hold 60, 60 and 120 characters, counted as MariaDB counts them, and one more is refused here with a
        // message, never as the database's error, which would say it couldn't be reached
        [Theory]
        [InlineData("name", 61, "The name holds at most 60 characters.")]
        [InlineData("reason", 61, "The reason holds at most 60 characters.")]
        [InlineData("notes", 121, "The notes hold at most 120 characters.")]
        public async Task AddRefusesTextTheColumnsCantHold(string field, int length, string message)
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.13");
            var fields = new Dictionary<string, string> { ["steamid"] = "12345", ["name"] = "", ["reason"] = "", ["expires"] = "", ["notes"] = "" };

            fields[field] = string.Concat(Enumerable.Repeat("\U0001F600", length));
            await Answered(await AddAsync(admin, [.. fields.Select(pair => (pair.Key, pair.Value))]), HttpStatusCode.BadRequest, message);

            // One fewer fits, emoji and all
            fields[field] = string.Concat(Enumerable.Repeat("\U0001F600", length - 1));
            Redirected(await AddAsync(admin, [.. fields.Select(pair => (pair.Key, pair.Value))]), 12345, "added");
            Assert.Single(await db.BlacklistAuditAsync());
        }

        [Theory]
        [InlineData("name", "Line\nbreak", "The name can't hold a line break or another control character.")]
        [InlineData("reason", "Tab\there", "The reason can't hold a line break or another control character.")]
        [InlineData("expires", "2027-13-01", BlacklistWords.BadDate)]
        [InlineData("expires", "1999-12-31", BlacklistWords.BadDate)]
        [InlineData("expires", "9999-01-01", BlacklistWords.BadDate)]
        [InlineData("expires", "2027-02-30", BlacklistWords.BadDate)]
        [InlineData("expires", "02/01/2027", BlacklistWords.BadDate)]
        [InlineData("expires", " 2027-01-02", BlacklistWords.BadDate)]
        public async Task AddRefusesFieldsThatCantBeSaved(string field, string value, string message)
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.14");
            var fields = new Dictionary<string, string> { ["steamid"] = "12345", ["name"] = "Test Player", ["reason"] = "", ["expires"] = "", ["notes"] = "" };
            fields[field] = value;

            await Answered(await AddAsync(admin, [.. fields.Select(pair => (pair.Key, pair.Value))]), HttpStatusCode.BadRequest, message);
            Assert.Equal(0, await db.EntryCountAsync());
            Assert.Empty(await db.BlacklistAuditAsync());
        }

        // Found by the locked read: a refusal with its own audit row, which keeps the entry as it is, and a link to it. An
        // entry that already has exactly these details is most likely the editor's own earlier try, which isn't a problem.
        [Fact]
        public async Task AddOfAnAccountAlreadyListedIsRefused()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345, "Test Player", "Cheating", new DateTime(2027, 1, 2), "Made up"));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.15");

            var other = await AddAsync(admin, ("steamid", SteamId64), ("name", "Other Name"), ("reason", ""), ("expires", ""), ("notes", ""));
            await Answered(other, HttpStatusCode.Conflict, BlacklistWords.Exists);
            var html = await other.Content.ReadAsStringAsync(Token);
            Assert.Contains("class=\"notice notice-problem\" role=\"alert\"", html);

            // The sentence that says what to do is the link, and says it once
            Assert.Contains("<a href=\"/admin/blacklist/12345\">Open its entry to change it</a>.", html);
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(html, "Open its entry"));
            Assert.Equal("Other Name", BoxValue(html, "name"));
            PageTests.AssertAccessible("an add of an account already listed", html);

            var refusal = Assert.Single(await db.BlacklistAuditAsync());
            Assert.Equal(AuditOutcome.Refused, refusal.outcome);
            Assert.Equal("Already on the blacklist", refusal.detail);
            Assert.Equal(Json("Test Player", "Cheating", "2027-01-02 00:00:00", "Made up"), refusal.before_json);
            Assert.Null(refusal.after_json);
            Assert.Equal("Test Player", (await db.EntryAsync(12345))?.username);

            var same = await AddAsync(admin, ("steamid", "12345"), ("name", "Test Player"), ("reason", "Cheating"), ("expires", "2027-01-02"), ("notes", "Made up"));
            await Answered(same, HttpStatusCode.Conflict, BlacklistWords.AlreadyAdded);
            Assert.Contains("class=\"notice notice-done\" role=\"status\"", await same.Content.ReadAsStringAsync(Token));
            Assert.Equal(2, (await db.BlacklistAuditAsync()).Count);
        }

        [Fact]
        public async Task EditSavesOnlyWhatChanged()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345, "Test Player", "Testing", Future, "Made up"));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.16");
            var page = await OpenAsync(admin, 12345);

            Assert.Equal("Test Player", page.Boxes["name"]);
            Assert.Equal("2099-01-02", page.Boxes["expires"]);

            var response = await EditAsync(admin, 12345, page, ("reason", "Cheating"), ("notes", ""));
            Redirected(response, 12345, "saved");

            // The day the box held is the stored end's own day, so its time stays
            Assert.Equivalent(Entry(12345, "Test Player", "Cheating", Future, null), await db.EntryAsync(12345), strict: true);

            var audit = Assert.Single(await db.BlacklistAuditAsync());
            AssertRow(audit, "blacklist.edit", TestUsers.Admin, AuditRole.Admin, "198.51.100.16", factory.Clock.GetUtcNow().UtcDateTime);
            Assert.Equal("12345", audit.target_id);
            Assert.Equal("""{"Reason":"Testing","Notes":"Made up"}""", audit.before_json);
            Assert.Equal("""{"Reason":"Cheating","Notes":null}""", audit.after_json);
            Assert.Null(audit.detail);

            Assert.Contains(BlacklistWords.Saved, WebUtility.HtmlDecode(await admin.Client.GetStringAsync(SignInSteps.PathOf(response.Headers.Location), Token)));

            // Another day is stored at 00:00 that day, UTC, and an empty box is no end
            Redirected(await EditAsync(admin, 12345, await OpenAsync(admin, 12345), ("expires", "2030-06-07")), 12345, "saved");
            Assert.Equal(new DateTime(2030, 6, 7), (await db.EntryAsync(12345))?.expire_date);
            Assert.Equal("""{"Expires":"2099-01-02 03:04:05"}""", (await db.BlacklistAuditAsync())[^1].before_json);
            Assert.Equal("""{"Expires":"2030-06-07 00:00:00"}""", (await db.BlacklistAuditAsync())[^1].after_json);

            Redirected(await EditAsync(admin, 12345, await OpenAsync(admin, 12345), ("expires", "")), 12345, "saved");
            Assert.Null((await db.EntryAsync(12345))?.expire_date);
            Assert.Equal("""{"Expires":null}""", (await db.BlacklistAuditAsync())[^1].after_json);

            // The name and the reason each edit sent are in the table and the audit log, never in a log line
            AssertNotLogged(factory, "Test Player", "Cheating");
        }

        // UpdatePlayerBan would say it applied, so an edit that changes nothing is caught first, and records nothing
        [Fact]
        public async Task EditThatChangesNothingWritesNothing()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345, "Test Player", "Testing", Future, "Made up"));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.17");

            var response = await EditAsync(admin, 12345, await OpenAsync(admin, 12345), ("name", "  Test Player  "));

            await Answered(response, HttpStatusCode.OK, BlacklistWords.Unchanged);
            Assert.Contains("class=\"notice notice-done\" role=\"status\"", await response.Content.ReadAsStringAsync(Token));
            Assert.Empty(await db.BlacklistAuditAsync());
            Assert.Equal(Future, (await db.EntryAsync(12345))?.expire_date);
        }

        // Text the website saved that doesn't come back from the page as it's stored: a trailing space, an empty reason, in
        // the second case a line break and a tab in the notes, and in the third a NUL and a C1 control, which the HTML
        // parser reads back as U+FFFD and U+2026, and a tab. The box drops the line break, and the form trims each box and
        // keeps empty as none.
        const string SpacedNotes = "Cheating ";
        const string StoredNotes = "line one\nline two\twith a tab";
        const string ControlNotes = "a\0b\u0085c\tz";

        static Task StoreTextTheBoxCantShowAsync(MariaDbFixture db, string notes) => db.RootExecuteAsync(
            $"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username, reason, expire_date, notes) VALUES (12345, @name, @reason, @expires, @notes)",
            new { name = "Spaced Player ", reason = "", expires = Future, notes });

        // Saving the form as the page filled it changes nothing, however the website stored the text, so nothing is written
        // or recorded
        [Theory]
        [InlineData(SpacedNotes, SpacedNotes)]
        [InlineData(StoredNotes, "line oneline two\twith a tab")]
        [InlineData(ControlNotes, "a\uFFFDb\u2026c\tz")]
        public async Task SaveWithNothingChangedKeepsTextTheBoxCantShow(string notes, string box)
        {
            var db = await fixture.ReadyAsync();
            await StoreTextTheBoxCantShowAsync(db, notes);
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.32");
            var page = await OpenAsync(admin, 12345);

            Assert.Equal(box, page.Boxes["notes"]);

            await Answered(await EditAsync(admin, 12345, page), HttpStatusCode.OK, BlacklistWords.Unchanged);
            Assert.Empty(await db.BlacklistAuditAsync());
            Assert.Equivalent(Entry(12345, "Spaced Player ", "", Future, notes), await db.EntryAsync(12345), strict: true);
        }

        // Changing the name alone leaves the other texts as they're stored, records only the name, and isn't refused for a
        // tab the editor didn't type
        [Theory]
        [InlineData(SpacedNotes)]
        [InlineData(StoredNotes)]
        [InlineData(ControlNotes)]
        public async Task NameOnlyEditLeavesTheOtherTextsAsStored(string notes)
        {
            var db = await fixture.ReadyAsync();
            await StoreTextTheBoxCantShowAsync(db, notes);
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.33");

            Redirected(await EditAsync(admin, 12345, await OpenAsync(admin, 12345), ("name", "Renamed Player")), 12345, "saved");
            Assert.Equivalent(Entry(12345, "Renamed Player", "", Future, notes), await db.EntryAsync(12345), strict: true);

            var audit = Assert.Single(await db.BlacklistAuditAsync());
            Assert.Equal("""{"Name":"Spaced Player "}""", audit.before_json);
            Assert.Equal("""{"Name":"Renamed Player"}""", audit.after_json);

            // A tab in a text the editor did change is still theirs to take out
            var refused = await EditAsync(admin, 12345, await OpenAsync(admin, 12345), ("notes", "line one\tchanged"));
            await Answered(refused, HttpStatusCode.BadRequest, "The notes can't hold a line break or another control character.");
            Assert.Equal(notes, (await db.EntryAsync(12345))?.notes);
        }

        static BlacklistModel WithText(BlacklistModel entry, string box, string? text)
        {
            switch (box)
            {
                case "name": entry.username = text; break;
                case "reason": entry.reason = text; break;
                default: entry.notes = text; break;
            }

            return entry;
        }

        // Taking a space off either end of a box is a change, though the box then trims to the same text as one left alone:
        // the trimmed text is saved, the other fields stay as stored, and only that field is recorded
        [Theory]
        [InlineData("name", " Bob", "Bob", "Name")]
        [InlineData("name", "Spaced Player ", "Spaced Player", "Name")]
        [InlineData("reason", " Cheating", "Cheating", "Reason")]
        [InlineData("notes", "Seen in a lobby ", "Seen in a lobby", "Notes")]
        public async Task SpaceTakenOffTheEndsIsSaved(string box, string stored, string sent, string key)
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(WithText(Entry(12345, "Test Player", "Testing", Future, "Made up"), box, stored));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.48");
            var page = await OpenAsync(admin, 12345);

            // The box shows the text as it's stored, spaces and all
            Assert.Equal(stored, page.Boxes[box]);

            Redirected(await EditAsync(admin, 12345, page, (box, sent)), 12345, "saved");
            Assert.Equivalent(WithText(Entry(12345, "Test Player", "Testing", Future, "Made up"), box, sent), await db.EntryAsync(12345), strict: true);

            var audit = Assert.Single(await db.BlacklistAuditAsync());
            Assert.Equal($$"""{"{{key}}":"{{stored}}"}""", audit.before_json);
            Assert.Equal($$"""{"{{key}}":"{{sent}}"}""", audit.after_json);
        }

        // A tab the website saved passes only in a box left alone. Taking a space off the end of that box makes the text the
        // editor's, tab and all, so it's refused as a field with its message, not let through and then refused as a change
        // made since the page opened.
        [Fact]
        public async Task SpaceTakenOffATabbedTextMakesItTheEditors()
        {
            var db = await fixture.ReadyAsync();
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username, notes) VALUES (12345, 'Test Player', @notes)", new { notes = "Pasted\tnote " });
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.51");
            var page = await OpenAsync(admin, 12345);

            Assert.Equal("Pasted\tnote ", page.Boxes["notes"]);

            await Answered(await EditAsync(admin, 12345, page, ("notes", "Pasted\tnote")), HttpStatusCode.BadRequest, "The notes can't hold a line break or another control character.");
            Assert.Equal("Pasted\tnote ", (await db.EntryAsync(12345))?.notes);
            Assert.Empty(await db.BlacklistAuditAsync());
        }

        // A box left as the page filled it keeps the stored text, whatever spaces it holds: a leading space, an empty text,
        // or one of only spaces or other blank characters, which the box shows and sends back as they are. So saving
        // changes nothing, and records nothing.
        [Theory]
        [InlineData(" Bob")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\u000B")]
        [InlineData("   ")]
        public async Task UntouchedBoxKeepsItsSpaces(string stored)
        {
            var db = await fixture.ReadyAsync();
            await db.RootExecuteAsync(
                $"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username, reason, expire_date, notes) VALUES (12345, @text, @text, @expires, @text)",
                new { text = stored, expires = Future });
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.49");
            var page = await OpenAsync(admin, 12345);

            Assert.Equal(new[] { stored, stored, stored }, new[] { page.Boxes["name"], page.Boxes["reason"], page.Boxes["notes"] });

            await Answered(await EditAsync(admin, 12345, page), HttpStatusCode.OK, BlacklistWords.Unchanged);
            Assert.Empty(await db.BlacklistAuditAsync());
            Assert.Equivalent(Entry(12345, stored, stored, Future, stored), await db.EntryAsync(12345), strict: true);
        }

        // The form lets a text it would refuse through as the entry's own, as the handler read the entry before the
        // transaction. A locked row that no longer holds it was changed in between, even back to what the page showed, so
        // the text isn't written.
        [Fact]
        public async Task TextLetThroughAsTheEntrysOwnIsWrittenOnlyWhileItIs()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345));
            var faults = new Faults();
            await using var factory = Faulty(db, faults);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.39");
            var page = await OpenAsync(admin, 12345);

            // The handler's read finds a tab in the notes, which someone stored and took out again before the locked read
            faults.HandlerSees = Entry(12345, notes: "Pasted\tnote");

            await Answered(await EditAsync(admin, 12345, page, ("notes", "Pasted\tnote")), HttpStatusCode.Conflict, BlacklistWords.Changed);
            Assert.Equal("Made up", (await db.EntryAsync(12345))?.notes);
            Assert.Equal([AuditOutcome.Refused], (await db.BlacklistAuditAsync()).Select(row => row.outcome));
        }

        // The same for an end outside the date box's years, which the form lets through only as the entry's own end, as the
        // handler read it. A locked row whose end isn't that day was changed in between, so the day isn't written.
        [Fact]
        public async Task DayLetThroughAsTheEntrysOwnIsWrittenOnlyWhileItIs()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345, "Test Player", "Cheating", Future, "Made up"));
            var faults = new Faults();
            await using var factory = Faulty(db, faults);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.56");
            var page = await OpenAsync(admin, 12345);

            // The handler's read finds an end of 9999-12-31, which someone stored and changed back before the locked read,
            // and a form someone made up sends that day
            faults.HandlerSees = Entry(12345, "Test Player", "Cheating", new DateTime(9999, 12, 31, 23, 59, 59), "Made up");

            await Answered(await EditAsync(admin, 12345, page, ("expires", "9999-12-31")), HttpStatusCode.Conflict, BlacklistWords.Changed);
            Assert.Equivalent(Entry(12345, "Test Player", "Cheating", Future, "Made up"), await db.EntryAsync(12345), strict: true);
            Assert.Equal([AuditOutcome.Refused], (await db.BlacklistAuditAsync()).Select(row => row.outcome));
        }

        // An entry stored with an end outside the date box's years, such as 9999-12-31, which someone else wrote, can still
        // be saved with that day, which keeps the end as it is
        [Fact]
        public async Task EndOutsideTheBoxesYearsIsKept()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345, "Test Player", "Testing", new DateTime(9999, 12, 31, 23, 59, 59), "Made up"));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.18");
            var page = await OpenAsync(admin, 12345);

            Assert.Equal("9999-12-31", page.Boxes["expires"]);
            Assert.DoesNotContain("max=\"9998-12-31\"", page.Html);

            Redirected(await EditAsync(admin, 12345, page, ("name", "Renamed Player")), 12345, "saved");
            Assert.Equal(new DateTime(9999, 12, 31, 23, 59, 59), (await db.EntryAsync(12345))?.expire_date);

            // Another day outside them still isn't one the box allows, and the page saying so leaves the box without limits,
            // so the entry's own end can still be sent back
            var refused = await EditAsync(admin, 12345, await OpenAsync(admin, 12345), ("expires", "9999-01-01"));
            await Answered(refused, HttpStatusCode.BadRequest, BlacklistWords.BadDate);
            Assert.DoesNotContain("max=\"9998-12-31\"", await refused.Content.ReadAsStringAsync(Token));

            // So does a page refusing another field, whatever day the editor put in the box
            var tabbed = await EditAsync(admin, 12345, await OpenAsync(admin, 12345), ("expires", "2030-01-01"), ("notes", "Pasted\tnote"));
            await Answered(tabbed, HttpStatusCode.BadRequest, BlacklistWords.NotSaved);
            Assert.DoesNotContain("max=\"9998-12-31\"", await tabbed.Content.ReadAsStringAsync(Token));
        }

        // A stored end that isn't a real date, which only something else can write, is said so, and saving replaces it
        [Fact]
        public async Task UnreadableEndIsReplacedBySaving()
        {
            var db = await fixture.ReadyAsync();
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username, expire_date) VALUES (12345, 'Test Player', '0000-00-00 00:00:00')");
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.19");
            var page = await OpenAsync(admin, 12345);

            Assert.Contains(BlacklistWords.UnreadableDate, page.Text);
            Assert.Contains("Not a real date", page.Text);
            Assert.Equal("", page.Boxes["expires"]);

            Redirected(await EditAsync(admin, 12345, page), 12345, "saved");

            var entry = await db.EntryAsync(12345);
            Assert.False(entry?.expire_date_unreadable);
            Assert.Null(entry?.expire_date);

            var audit = Assert.Single(await db.BlacklistAuditAsync());
            Assert.Equal("""{"Expires":"unreadable"}""", audit.before_json);
            Assert.Equal("""{"Expires":null}""", audit.after_json);
        }

        // Someone else's change since the page opened, by the website or by hand: the edit and the removal are refused,
        // with the entry as it is now in their audit rows, and the page shown again has a version that works
        [Theory]
        [InlineData("username = 'Renamed Elsewhere'")]
        [InlineData("reason = NULL")]
        [InlineData("reason = ''")]
        [InlineData("expire_date = '2099-01-02 03:04:06'")]
        [InlineData("notes = 'Changed'")]
        public async Task EntryChangedSinceThePageOpenedIsAConflict(string change)
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345, "Test Player", "Testing", Future, "Made up"));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.20");
            var page = await OpenAsync(admin, 12345);

            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.BlacklistTable} SET {change} WHERE steam_id = 12345");
            var stored = await db.EntryAsync(12345);

            var edit = await EditAsync(admin, 12345, page, ("name", "My Name"));
            await Answered(edit, HttpStatusCode.Conflict, BlacklistWords.Changed);
            Assert.Contains("class=\"notice notice-problem\" role=\"alert\"", await edit.Content.ReadAsStringAsync(Token));

            await Answered(await RemoveAsync(admin, 12345, page), HttpStatusCode.Conflict, BlacklistWords.RemoveChanged);
            Assert.NotNull(await db.EntryAsync(12345));

            var refusals = await db.BlacklistAuditAsync();
            Assert.Equal([("blacklist.edit", AuditOutcome.Refused), ("blacklist.remove", AuditOutcome.Refused)], refusals.Select(row => (row.action, row.outcome)));
            Assert.All(refusals, row => Assert.Equal("The entry had changed since the page showed it", row.detail));
            Assert.All(refusals, row => Assert.Equal(BlacklistJson.Whole(stored!), row.before_json));
            Assert.All(refusals, row => Assert.Null(row.after_json));
            Assert.All(refusals, row => Assert.Equal(TestUsers.Admin.Id, row.actor_id));
            Assert.All(refusals, row => Assert.Equal("12345", row.target_id));

            // The page it shows is the entry as it is now, whose forms work
            var fresh = Read(await edit.Content.ReadAsStringAsync(Token));
            Assert.NotEqual(page.Version, fresh.Version);
            Redirected(await EditAsync(admin, 12345, fresh, ("name", "My Name")), 12345, "saved");
            AssertNotLogged(factory, "My Name");
        }

        // A refused field's page keeps what was sent in the boxes, with the version they came with, so saving from it after
        // someone else's change is a conflict, never a save over that change. Here the field is a pasted tab. A form that's
        // already out of date when it's sent is answered as the conflict at once (StaleFormWithATabIsAConflict), so the
        // change here comes after the refusal. RefusedFieldsPageAfterARaceKeepsTheVersionItsBoxesCameWith has one before
        // it, which only a race can make.
        [Fact]
        public async Task SaveAfterARefusedFieldStillFindsAChangeMadeSince()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345, "Test Player", "Cheating", Future, "Made up"));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.34");
            var page = await OpenAsync(admin, 12345);

            var refused = await EditAsync(admin, 12345, page, ("notes", "Pasted\tnote"));
            await Answered(refused, HttpStatusCode.BadRequest, BlacklistWords.NotSaved);
            Assert.Empty(await db.BlacklistAuditAsync());

            var again = Read(await refused.Content.ReadAsStringAsync(Token));
            Assert.Equal("Pasted\tnote", again.Boxes["notes"]);
            Assert.Equal(page.Version, again.Version);

            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.BlacklistTable} SET reason = 'Cheating (video)' WHERE steam_id = 12345");

            await Answered(await EditAsync(admin, 12345, again, ("notes", "Pasted note")), HttpStatusCode.Conflict, BlacklistWords.Changed);
            Assert.Equivalent(Entry(12345, "Test Player", "Cheating (video)", Future, "Made up"), await db.EntryAsync(12345), strict: true);
            Assert.Equal([AuditOutcome.Refused], (await db.BlacklistAuditAsync()).Select(row => row.outcome));
        }

        // A refused field's page keeps the version its boxes came with even when the entry changed between the handler's
        // read and the page, which only a race can do, since a form already out of date is answered as the change. Saving
        // from it is a conflict, never a save over that change, and the remove form has the version of the details above
        // it, which are the entry as it is now.
        [Fact]
        public async Task RefusedFieldsPageAfterARaceKeepsTheVersionItsBoxesCameWith()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345, "Test Player", "Cheating", Future, "Made up"));
            var faults = new Faults();
            await using var factory = Faulty(db, faults);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.35");
            var page = await OpenAsync(admin, 12345);
            var seen = await db.EntryAsync(12345);

            // The handler's read finds the entry as the page showed it, and the page is then made from the entry changed
            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.BlacklistTable} SET reason = 'Cheating (video)' WHERE steam_id = 12345");
            var current = BlacklistVersion.Of((await db.EntryAsync(12345))!);
            faults.HandlerSees = seen;

            var refused = await EditAsync(admin, 12345, page, ("notes", "Pasted\tnote"));
            await Answered(refused, HttpStatusCode.BadRequest, BlacklistWords.NotSaved);
            Assert.Empty(await db.BlacklistAuditAsync());

            var again = Read(await refused.Content.ReadAsStringAsync(Token));
            Assert.Equal((page.Version, current, "Cheating"), (again.Version, again.RemoveVersion, again.Boxes["reason"]));

            await Answered(await EditAsync(admin, 12345, again, ("notes", "Pasted note")), HttpStatusCode.Conflict, BlacklistWords.Changed);
            Assert.Equivalent(Entry(12345, "Test Player", "Cheating (video)", Future, "Made up"), await db.EntryAsync(12345), strict: true);
            Assert.Equal([AuditOutcome.Refused], (await db.BlacklistAuditAsync()).Select(row => row.outcome));
        }

        // An end outside the box's years leaves the box without limits. Once someone else changes it, the box's day is no
        // longer the entry's own, and the checks would blame the editor for a box they left alone, as they would for a tab
        // the website wrote in the notes and has since changed. The form is out of date, which is the answer, with its
        // refusal recorded and nothing written, and the page then shows the entry as it is, whose form saves.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task StaleFormWithADayTheBoxNoLongerAllowsIsAConflict(bool tabbedNotes)
        {
            var db = await fixture.ReadyAsync();
            await db.RootExecuteAsync(
                $"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username, reason, expire_date, notes) VALUES (12345, 'Test Player', 'Cheating', '9999-12-31 23:59:59', @notes)",
                new { notes = tabbedNotes ? "Pasted\tnote" : "Made up" });
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.66");
            var page = await OpenAsync(admin, 12345);

            var notes = tabbedNotes ? "Other note" : "Made up";
            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.BlacklistTable} SET expire_date = '2030-01-01 00:00:00', notes = @notes WHERE steam_id = 12345", new { notes });
            var current = BlacklistVersion.Of((await db.EntryAsync(12345))!);

            var edit = await EditAsync(admin, 12345, page, ("name", "Renamed Player"));
            await Answered(edit, HttpStatusCode.Conflict, BlacklistWords.Changed);
            var html = await edit.Content.ReadAsStringAsync(Token);
            Assert.DoesNotContain(BlacklistWords.BadDate, WebUtility.HtmlDecode(html));
            Assert.DoesNotContain("class=\"field-problem\"", html);
            Assert.Equivalent(Entry(12345, "Test Player", "Cheating", new DateTime(2030, 1, 1), notes), await db.EntryAsync(12345), strict: true);
            Assert.Equal([AuditOutcome.Refused], (await db.BlacklistAuditAsync()).Select(row => row.outcome));

            var fresh = Read(html);
            Assert.Equal((current, current, "2030-01-01"), (fresh.Version, fresh.RemoveVersion, fresh.Boxes["expires"]));
            Redirected(await EditAsync(admin, 12345, fresh, ("name", "Renamed Player")), 12345, "saved");
        }

        // A form sent after someone else's change is answered as that change before any field's check, and its refusal is
        // recorded: a tab the editor typed, and an entry removed while its page was open, whose notes hold a tab the box
        // sends back. Nothing is written. A day the date box no longer allows is answered the same way
        // (StaleFormWithADayTheBoxNoLongerAllowsIsAConflict).
        [Fact]
        public async Task StaleFormWithATabIsAConflict()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345, "Test Player", "Cheating", Future, "Made up"));
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username, notes) VALUES (23456, 'Other Player', @notes)", new { notes = "Pasted\tnote" });
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.52");
            var page = await OpenAsync(admin, 12345);
            var removed = await OpenAsync(admin, 23456);

            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.BlacklistTable} SET reason = 'Cheating (video)' WHERE steam_id = 12345");
            await db.RootExecuteAsync($"DELETE FROM {MariaDbFixture.BlacklistTable} WHERE steam_id = 23456");

            await Answered(await EditAsync(admin, 12345, page, ("notes", "Pasted\tnote")), HttpStatusCode.Conflict, BlacklistWords.Changed);
            Assert.Equivalent(Entry(12345, "Test Player", "Cheating (video)", Future, "Made up"), await db.EntryAsync(12345), strict: true);

            await Answered(await EditAsync(admin, 23456, removed), HttpStatusCode.Conflict, BlacklistWords.GoneSinceOpened);
            Assert.Null(await db.EntryAsync(23456));

            Assert.Equal([("12345", AuditOutcome.Refused), ("23456", AuditOutcome.Refused)], (await db.BlacklistAuditAsync()).Select(row => (row.target_id, row.outcome)));
        }

        // The website took the space off the end of a text holding a tab while the page was open, so the box the editor
        // left alone no longer holds the entry's text, and the checks would blame them for the tab. The form is out of date,
        // which is the answer, with its refusal recorded and nothing written.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task WebsiteRespacingATabbedTextIsAConflict(bool renamed)
        {
            var db = await fixture.ReadyAsync();
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username, notes) VALUES (12345, 'Test Player', @notes)", new { notes = "Pasted\tnote " });
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.53");
            var page = await OpenAsync(admin, 12345);

            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.BlacklistTable} SET notes = @notes WHERE steam_id = 12345", new { notes = "Pasted\tnote" });

            // Left alone, the form would make the entry what it now is, which the page says. Renamed, it's a change made
            // over someone else's.
            var edit = renamed ? await EditAsync(admin, 12345, page, ("name", "My Name")) : await EditAsync(admin, 12345, page);
            await Answered(edit, HttpStatusCode.Conflict, renamed ? BlacklistWords.Changed : BlacklistWords.AlreadySaved);

            var entry = await db.EntryAsync(12345);
            Assert.Equal(("Test Player", "Pasted\tnote"), (entry?.username, entry?.notes));
            Assert.Equal([AuditOutcome.Refused], (await db.BlacklistAuditAsync()).Select(row => row.outcome));
        }

        // Clearing a box that showed only spaces, or another blank character such as a vertical tab, which the details show
        // as its code, is a change the editor made: the field is saved as none, and recorded
        [Theory]
        [InlineData("   ")]
        [InlineData("\u000B")]
        [InlineData("   ")]
        [InlineData(" \t ")]
        public async Task ClearingABlankLookingBoxSavesIt(string stored)
        {
            var db = await fixture.ReadyAsync();
            await db.RootExecuteAsync(
                $"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username, reason, expire_date, notes) VALUES (12345, 'Test Player', @text, @expires, @text)",
                new { text = stored, expires = Future });
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.54");
            var page = await OpenAsync(admin, 12345);

            Assert.Equal(new[] { stored, stored }, new[] { page.Boxes["reason"], page.Boxes["notes"] });

            Redirected(await EditAsync(admin, 12345, page, ("reason", ""), ("notes", "")), 12345, "saved");
            Assert.Equivalent(Entry(12345, "Test Player", null, Future, null), await db.EntryAsync(12345), strict: true);

            var audit = Assert.Single(await db.BlacklistAuditAsync());
            Assert.Equal(BlacklistJson.Changed(Entry(12345, "Test Player", stored, Future, stored), Entry(12345, "Test Player", null, Future, null)),
                (audit.before_json, audit.after_json));
        }

        // A refused field's page puts every box back as it was sent, spaces and all, so saving from it leaves a box the
        // editor never touched as it's stored
        [Fact]
        public async Task RefusedFieldsPageKeepsABoxOfOnlySpaces()
        {
            var db = await fixture.ReadyAsync();
            await db.RootExecuteAsync(
                $"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username, reason, expire_date, notes) VALUES (12345, 'Test Player', '   ', @expires, 'Made up')",
                new { expires = Future });
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.55");
            var page = await OpenAsync(admin, 12345);

            var refused = await EditAsync(admin, 12345, page, ("notes", "Pasted\tnote"));
            await Answered(refused, HttpStatusCode.BadRequest, BlacklistWords.NotSaved);

            var again = Read(await refused.Content.ReadAsStringAsync(Token));
            Assert.Equal("   ", again.Boxes["reason"]);

            Redirected(await EditAsync(admin, 12345, again, ("notes", "Pasted note")), 12345, "saved");
            Assert.Equivalent(Entry(12345, "Test Player", "   ", Future, "Pasted note"), await db.EntryAsync(12345), strict: true);

            var audit = Assert.Single(await db.BlacklistAuditAsync());
            Assert.Equal(("""{"Notes":"Made up"}""", """{"Notes":"Pasted note"}"""), (audit.before_json, audit.after_json));
        }

        // The locked read is what makes an edit see a change that's still being saved: the edit waits for the row, then
        // finds it changed. Read without the transaction, it would find the row as it was, wait only at its write, and then
        // overwrite the change.
        [Fact]
        public async Task EditRacingAnUncommittedChangeIsRefused()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.36");
            var page = await OpenAsync(admin, 12345);

            await using var holder = new MySqlConnection(db.RootConnection);
            await holder.OpenAsync(Token).WaitAsync(TimeSpan.FromSeconds(30), Token);
            await RawAsync(holder, "START TRANSACTION");
            await RawAsync(holder, $"UPDATE {MariaDbFixture.BlacklistTable} SET username = 'Renamed Elsewhere' WHERE steam_id = 12345");

            var edit = EditAsync(admin, 12345, page, ("name", "My Name"));

            Assert.Equal(1, await db.LockWaitsAsync("ihvph_blacklist", 1, TimeSpan.FromSeconds(10)));
            await RawAsync(holder, "COMMIT");

            await Answered(await edit.WaitAsync(TimeSpan.FromSeconds(30), Token), HttpStatusCode.Conflict, BlacklistWords.Changed);
            Assert.Equal("Renamed Elsewhere", (await db.EntryAsync(12345))?.username);
            Assert.Equal([AuditOutcome.Refused], (await db.BlacklistAuditAsync()).Select(row => row.outcome));
        }

        static async Task RawAsync(MySqlConnection connection, string sql)
        {
            await using var command = new MySqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(Token).WaitAsync(TimeSpan.FromSeconds(30), Token);
        }

        // Every change reads its row inside its transaction with the transaction, which locks the row until the commit.
        // Without it, the read is an ordinary one on a connection of its own, which neither waits for the row nor holds it.
        [Fact]
        public async Task EveryChangeReadsItsRowLocked()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345), Entry(23456));
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username) VALUES (5000000000, 'Cut Player')");
            var faults = new Faults();
            await using var factory = Faulty(db, faults);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.37");

            Redirected(await AddAsync(admin, ("steamid", "54321"), ("name", "New Player")), 54321, "added");
            Redirected(await EditAsync(admin, 12345, await OpenAsync(admin, 12345), ("name", "Renamed Player")), 12345, "saved");
            Redirected(await RemoveAsync(admin, 23456, await OpenAsync(admin, 23456)), 23456, "removed");

            var list = await admin.Client.GetStringAsync("/admin/blacklist", Token);
            var number = await CraftedAsync(admin, "/admin/blacklist?handler=RemoveNumber", SignInSteps.FormToken(list), ("number", "5000000000"), ("version", NumberVersion(list)));
            Assert.Equal(HttpStatusCode.Redirect, number.StatusCode);

            Assert.Empty(faults.UnlockedReads);
            Assert.Equal(["GetBannedPlayer 54321", "GetBannedPlayer 12345", "GetBannedPlayer 23456", "GetOutOfRangeBan 5000000000"], faults.LockedReads);
        }

        // Removed since the page opened: an edit has nothing to save, and a removal has nothing left to do, which isn't a
        // problem, as when its own earlier try was saved
        [Fact]
        public async Task EntryRemovedSinceThePageOpened()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.21");
            var page = await OpenAsync(admin, 12345);

            await db.RootExecuteAsync($"DELETE FROM {MariaDbFixture.BlacklistTable} WHERE steam_id = 12345");

            var edit = await EditAsync(admin, 12345, page, ("name", "My Name"));
            await Answered(edit, HttpStatusCode.Conflict, BlacklistWords.GoneSinceOpened);
            Assert.Contains("This account isn't on the blacklist", await ArmorSteps.TextAsync(edit));

            var remove = await RemoveAsync(admin, 12345, page);
            await Answered(remove, HttpStatusCode.Conflict, BlacklistWords.AlreadyRemoved);
            Assert.Contains("class=\"notice notice-done\" role=\"status\"", await remove.Content.ReadAsStringAsync(Token));

            Assert.Equal(0, await db.EntryCountAsync());
            var refusals = await db.BlacklistAuditAsync();
            Assert.Equal(2, refusals.Count);
            Assert.All(refusals, row => Assert.Equal("The entry had been removed since the page showed it", row.detail));
            Assert.All(refusals, row => Assert.Null(row.before_json));
        }

        // The same for an entry whose end someone else wrote outside the date box's years, whose box has no limits and
        // sends that day back. With the entry gone there's no end of its own to let the day through, and no entry to show
        // its message with, so the form is refused as removed, and recorded, like any other: after a removal by hand, or on
        // the dashboard, which leaves the account's page its history.
        [Theory]
        [InlineData("9999-12-31 23:59:59", false, false)]
        [InlineData("9999-12-31 23:59:59", true, false)]
        [InlineData("9999-12-31 23:59:59", false, true)]
        [InlineData("1999-01-01 00:00:00", true, true)]
        public async Task EntryWithAnEndOutsideTheBoxesYearsRemovedSinceThePageOpened(string end, bool renamed, bool onTheDashboard)
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345, "Test Player", "Cheating", DateTime.ParseExact(end, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "Made up"));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.57");
            var page = await OpenAsync(admin, 12345);

            Assert.Equal(end[..10], page.Boxes["expires"]);
            Assert.DoesNotContain("max=\"9998-12-31\"", page.Html);

            if (onTheDashboard)
                Redirected(await RemoveAsync(admin, 12345, await OpenAsync(admin, 12345)), 12345, "removed");
            else
                await db.RootExecuteAsync($"DELETE FROM {MariaDbFixture.BlacklistTable} WHERE steam_id = 12345");

            var edit = renamed ? await EditAsync(admin, 12345, page, ("name", "My Name")) : await EditAsync(admin, 12345, page);
            await Answered(edit, HttpStatusCode.Conflict, BlacklistWords.GoneSinceOpened);
            Assert.Equal(0, await db.EntryCountAsync());

            var rows = await db.BlacklistAuditAsync();
            string[] actions = onTheDashboard ? ["blacklist.remove", "blacklist.edit"] : ["blacklist.edit"];
            Assert.Equal(actions, rows.Select(row => row.action));
            Assert.Equal((AuditOutcome.Refused, "The entry had been removed since the page showed it"), (rows[^1].outcome, rows[^1].detail));
        }

        // Removed after the handler's own read, before the page with a field's message could be shown again: the same
        // conflict and refusal as a removal before it, for a text and for a day the checks refused. Removed on the
        // dashboard, the account's page still has its history to show, but no entry to show the message with.
        [Theory]
        [InlineData("notes", "Pasted\tnote", false)]
        [InlineData("expires", "9999-01-01", false)]
        [InlineData("notes", "Pasted\tnote", true)]
        [InlineData("expires", "9999-01-01", true)]
        public async Task EntryRemovedBeforeARefusedFieldsPageIsAConflict(string field, string value, bool onTheDashboard)
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345, "Test Player", "Cheating", Future, "Made up"));
            var faults = new Faults();
            await using var factory = Faulty(db, faults);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.58");
            var page = await OpenAsync(admin, 12345);
            var seen = await db.EntryAsync(12345);

            // The handler's read finds the entry as the page showed it, and then it's gone
            if (onTheDashboard)
                Redirected(await RemoveAsync(admin, 12345, await OpenAsync(admin, 12345)), 12345, "removed");
            else
                await db.RootExecuteAsync($"DELETE FROM {MariaDbFixture.BlacklistTable} WHERE steam_id = 12345");

            faults.HandlerSees = seen;

            await Answered(await EditAsync(admin, 12345, page, (field, value)), HttpStatusCode.Conflict, BlacklistWords.GoneSinceOpened);
            Assert.Equal(0, await db.EntryCountAsync());

            var rows = await db.BlacklistAuditAsync();
            string[] actions = onTheDashboard ? ["blacklist.remove", "blacklist.edit"] : ["blacklist.edit"];
            Assert.Equal(actions, rows.Select(row => row.action));
            Assert.Equal((AuditOutcome.Refused, "The entry had been removed since the page showed it"), (rows[^1].outcome, rows[^1].detail));
        }

        // A day the checks refused never reaches the row. Here the handler found no entry, or one with another end, so the
        // entry's own far end wasn't let through, and the locked read then finds the row as the page showed it, added or
        // changed back in between. Writing would clear the end, since the fields hold none in that day's place.
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task DayTheChecksRefusedIsNeverWritten(bool renamed, bool seenChanged)
        {
            var db = await fixture.ReadyAsync();
            var farEnd = new DateTime(9999, 12, 31, 23, 59, 59);
            await db.AddEntriesAsync(Entry(12345, "Test Player", "Cheating", farEnd, "Made up"));
            var faults = new Faults();
            await using var factory = Faulty(db, faults);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.59");
            var page = await OpenAsync(admin, 12345);

            if (seenChanged)
                faults.HandlerSees = Entry(12345, "Test Player", "Cheating", new DateTime(2030, 1, 1), "Made up");
            else
                faults.HandlerSeesNone = 1;

            var edit = renamed ? await EditAsync(admin, 12345, page, ("name", "My Name")) : await EditAsync(admin, 12345, page);
            await Answered(edit, HttpStatusCode.Conflict, BlacklistWords.Changed);
            Assert.Equivalent(Entry(12345, "Test Player", "Cheating", farEnd, "Made up"), await db.EntryAsync(12345), strict: true);
            Assert.Equal([AuditOutcome.Refused], (await db.BlacklistAuditAsync()).Select(row => row.outcome));
        }

        // The same with the row added back at another version, with no end and the same texts. The form's day was
        // 9999-12-31, so the entry doesn't already have what it sent.
        [Fact]
        public async Task RefusedDayIsNeverAlreadySaved()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345, "Test Player", "Cheating", new DateTime(9999, 12, 31, 23, 59, 59), "Made up"));
            var faults = new Faults();
            await using var factory = Faulty(db, faults);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.60");
            var page = await OpenAsync(admin, 12345);

            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.BlacklistTable} SET expire_date = NULL WHERE steam_id = 12345");
            faults.HandlerSeesNone = 1;

            var edit = await EditAsync(admin, 12345, page);
            await Answered(edit, HttpStatusCode.Conflict, BlacklistWords.Changed);
            Assert.DoesNotContain(BlacklistWords.AlreadySaved, await ArmorSteps.TextAsync(edit));
            Assert.Equivalent(Entry(12345, "Test Player", "Cheating", null, "Made up"), await db.EntryAsync(12345), strict: true);
            Assert.Equal([AuditOutcome.Refused], (await db.BlacklistAuditAsync()).Select(row => row.outcome));
        }

        // A refused day never reaches the row either when the handler found the entry as the page showed it, but the page
        // with the day's message then found none to show it with. The locked read finds the row as the page showed it, so
        // only the refused day stands between the form and a write that would clear the end.
        [Fact]
        public async Task DayRefusedWithNoPageToShowItIsNeverWritten()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345, "Test Player", "Cheating", Future, "Made up"));
            var faults = new Faults();
            await using var factory = Faulty(db, faults);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.67");
            var page = await OpenAsync(admin, 12345);

            faults.HandlerSees = await db.EntryAsync(12345);
            faults.HandlerSeesNone = 1;

            await Answered(await EditAsync(admin, 12345, page, ("expires", "9999-01-01")), HttpStatusCode.Conflict, BlacklistWords.Changed);
            Assert.Equivalent(Entry(12345, "Test Player", "Cheating", Future, "Made up"), await db.EntryAsync(12345), strict: true);
            Assert.Equal([AuditOutcome.Refused], (await db.BlacklistAuditAsync()).Select(row => row.outcome));
        }

        // A text the checks refused never reaches the row when the handler sends its form on: one from before the entry
        // changed, or one whose entry was gone by the time the page with the field's message would be shown. The locked read
        // finds the row as the page showed it, so only Writable stands between the typed tab and the row.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TextTheChecksRefusedIsNeverWritten(bool seenChanged)
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345, "Test Player", "Cheating", Future, "Made up"));
            var faults = new Faults();
            await using var factory = Faulty(db, faults);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.68");
            var page = await OpenAsync(admin, 12345);

            if (seenChanged)
            {
                faults.HandlerSees = Entry(12345, "Test Player", "Cheating (video)", Future, "Made up");
            }
            else
            {
                faults.HandlerSees = await db.EntryAsync(12345);
                faults.HandlerSeesNone = 1;
            }

            await Answered(await EditAsync(admin, 12345, page, ("notes", "Typed\ttab")), HttpStatusCode.Conflict, BlacklistWords.Changed);
            Assert.Equivalent(Entry(12345, "Test Player", "Cheating", Future, "Made up"), await db.EntryAsync(12345), strict: true);
            Assert.Equal([AuditOutcome.Refused], (await db.BlacklistAuditAsync()).Select(row => row.outcome));
        }

        [Fact]
        public async Task RemoveTakesTheEntryOffAndKeepsItsHistory()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345, "Test Player", "Cheating", Future, null));
            await using var factory = new DashboardFactory(db);
            var owner = await ArmorSteps.ReviewerAsync(factory, "198.51.100.22", TestUsers.Owner);

            var response = await RemoveAsync(owner, 12345, await OpenAsync(owner, 12345));

            Redirected(response, 12345, "removed");
            Assert.Null(await db.EntryAsync(12345));

            var audit = Assert.Single(await db.BlacklistAuditAsync());
            AssertRow(audit, "blacklist.remove", TestUsers.Owner, AuditRole.Owner, "198.51.100.22", factory.Clock.GetUtcNow().UtcDateTime);
            Assert.Equal("12345", audit.target_id);
            Assert.Equal(Json("Test Player", "Cheating", "2099-01-02 03:04:05", null), audit.before_json);
            Assert.Null(audit.after_json);
            Assert.Null(audit.detail);

            // The page still opens, with its history, and offers to add the account again
            var after = await owner.Client.GetStringAsync(SignInSteps.PathOf(response.Headers.Location), Token);
            Assert.Contains(BlacklistWords.Removed, WebUtility.HtmlDecode(after));
            Assert.Contains("This account isn't on the blacklist", WebUtility.HtmlDecode(after));
            Assert.Contains("href=\"/admin/blacklist/add?steamid=%5BU%3A1%3A12345%5D\"", after);
            Assert.DoesNotContain("handler=Edit", after);

            // The add page takes the account into its box
            Assert.Equal("[U:1:12345]", BoxValue(await owner.Client.GetStringAsync("/admin/blacklist/add?steamid=%5BU%3A1%3A12345%5D", Token), "steamid"));
        }

        // Account 0 can't be added, but a row the website holds for it can be changed and removed like any other
        [Fact]
        public async Task AccountZeroCanBeChangedAndRemoved()
        {
            var db = await fixture.ReadyAsync();
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username) VALUES (0, 'Zero Player')");
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.23");

            Redirected(await EditAsync(admin, 0, await OpenAsync(admin, 0), ("name", "Renamed Zero")), 0, "saved");
            Assert.Equal("Renamed Zero", (await db.EntryAsync(0))?.username);

            var page = await OpenAsync(admin, 0);
            Assert.Contains("It can be changed or removed here, but not added again.", page.Text);

            Redirected(await RemoveAsync(admin, 0, page), 0, "removed");
            Assert.Null(await db.EntryAsync(0));
            Assert.Equal(["0", "0"], (await db.BlacklistAuditAsync()).Select(row => row.target_id));

            // Its page keeps its history, with nothing left to change or remove, and says it can't be added
            var after = WebUtility.HtmlDecode(await admin.Client.GetStringAsync("/admin/blacklist/0?done=removed", Token));
            Assert.Contains(BlacklistWords.Removed, after);
            Assert.Contains("It can't be added again.", after);
            Assert.DoesNotContain("It can be changed or removed here", after);
        }

        // Checked before the database, which throws for anything it can't save: a form someone made up gets a 400, an
        // account the table can't hold a 404, never a 500, and nothing is written
        [Fact]
        public async Task MadeUpFormsAreRefusedBeforeTheDatabase()
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345));
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.24");
            var page = await OpenAsync(admin, 12345);
            var version = page.Version;

            var cases = new (string What, string Path, HttpStatusCode Status, (string, string)[] Fields)[]
            {
                ("no version", "/admin/blacklist/12345?handler=Edit", HttpStatusCode.BadRequest, [("name", "x")]),
                ("a version in capitals", "/admin/blacklist/12345?handler=Edit", HttpStatusCode.BadRequest, [("version", version.ToUpperInvariant())]),
                ("a short version", "/admin/blacklist/12345?handler=Remove", HttpStatusCode.BadRequest, [("version", "abc")]),
                ("a name over the limit", "/admin/blacklist/12345?handler=Edit", HttpStatusCode.BadRequest, [("version", version), ("name", new string('n', 61))]),
                ("a made-up date", "/admin/blacklist/12345?handler=Edit", HttpStatusCode.BadRequest, [("version", version), ("expires", "tomorrow")]),
                ("no handler", "/admin/blacklist/12345", HttpStatusCode.BadRequest, [("version", version)]),
                ("a handler that isn't there", "/admin/blacklist/12345?handler=Explode", HttpStatusCode.BadRequest, [("version", version)]),
                ("a list handler that isn't there", "/admin/blacklist?handler=Explode", HttpStatusCode.BadRequest, [("version", version)]),
                ("a POST to the list", "/admin/blacklist", HttpStatusCode.BadRequest, [("version", version)]),
                ("an account past 32 bits", "/admin/blacklist/4294967296?handler=Remove", HttpStatusCode.NotFound, [("version", version)]),
                ("a negative account", "/admin/blacklist/-1?handler=Remove", HttpStatusCode.NotFound, [("version", version)]),
                ("an account that isn't a number", "/admin/blacklist/abc?handler=Remove", HttpStatusCode.NotFound, [("version", version)])
            };

            foreach (var (what, path, status, fields) in cases)
            {
                var response = await CraftedAsync(admin, path, page.Token, fields);
                Assert.True(response.StatusCode == status, $"{what} answered {response.StatusCode}");
            }

            // Another method, with the token and from the dashboard's own origin
            foreach (var path in new[] { "/admin/blacklist/12345?handler=Remove", "/admin/blacklist/add", "/admin/audit" })
            {
                var put = await SignInSteps.PostAsync(admin, path, page.Token, origin: DashboardFactory.PublicOrigin, method: HttpMethod.Put, fields: [new("version", version)]);
                Assert.True(put.StatusCode == HttpStatusCode.BadRequest, $"A PUT to {path} answered {put.StatusCode}");
            }

            Assert.Equal("Test Player", (await db.EntryAsync(12345))?.username);
            Assert.Empty(await db.BlacklistAuditAsync());
            Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Level >= LogLevel.Error);
        }

        // The same as sign-out and armor: the form's token, and the dashboard's own origin. murderminershub.com counts as the
        // same site to browsers, so its pages carry the session cookie.
        [Theory]
        [InlineData("add")]
        [InlineData("edit")]
        [InlineData("remove")]
        [InlineData("number")]
        public async Task ActionsNeedTheTokenAndTheDashboardsOrigin(string action)
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345));
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username) VALUES (5000000000, 'Cut Player')");
            await using var factory = new DashboardFactory(db);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.25");
            var page = await OpenAsync(admin, 12345);

            var (path, fields) = action switch
            {
                "add" => ("/admin/blacklist/add", new KeyValuePair<string, string>[] { new("steamid", "54321"), new("name", "New Player") }),
                "edit" => ("/admin/blacklist/12345?handler=Edit", [new("version", page.Version), new("name", "Renamed Player")]),
                "remove" => ("/admin/blacklist/12345?handler=Remove", [new("version", page.Version)]),
                _ => ("/admin/blacklist?handler=RemoveNumber", [new("number", "5000000000"), new("version", NumberVersion(await admin.Client.GetStringAsync("/admin/blacklist", Token)))])
            };

            Assert.Equal(HttpStatusCode.BadRequest, (await SignInSteps.PostAsync(admin, path, null, origin: DashboardFactory.PublicOrigin, fields: fields)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await SignInSteps.PostAsync(admin, path, "made-up", origin: DashboardFactory.PublicOrigin, fields: fields)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SignInSteps.PostAsync(admin, path, page.Token, origin: "https://murderminershub.com", fields: fields)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SignInSteps.PostAsync(admin, path, page.Token, fetchSite: "same-site", fields: fields)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SignInSteps.PostAsync(admin, path, page.Token, fields: fields)).StatusCode);

            // Without a session, whatever the form holds, it's sign-in
            var anonymous = await SignInSteps.PostAsync(factory.Visitor("198.51.100.26"), path, page.Token, origin: DashboardFactory.PublicOrigin, fields: fields);
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.StartsWith("/signin?returnUrl=", SignInSteps.PathOf(anonymous.Headers.Location));

            Assert.Equal(2, await db.EntryCountAsync());
            Assert.Equal("Test Player", (await db.EntryAsync(12345))?.username);
            Assert.Empty(await db.BlacklistAuditAsync());

            // The same form, sent properly, works
            var sent = await SignInSteps.PostAsync(admin, path, page.Token, origin: DashboardFactory.PublicOrigin, fields: fields);
            Assert.Equal(HttpStatusCode.Redirect, sent.StatusCode);
            Assert.Single(await db.BlacklistAuditAsync());
        }

        // The version of a row with no account number, as the list's form for it sends it back
        internal static string NumberVersion(string html, string number = "5000000000")
        {
            var match = System.Text.RegularExpressions.Regex.Match(html, $"name=\"number\" value=\"{number}\" />\\s*<input type=\"hidden\" name=\"version\" value=\"([0-9a-f]{{64}})\"");

            Assert.True(match.Success, $"The page has no form for the row numbered {number}");
            return match.Groups[1].Value;
        }

        // A write that fails inside the transaction undoes the entry and its audit row with it, before anything could be
        // committed, so the error page says nothing was changed
        [Theory]
        [InlineData("blacklist")]
        [InlineData("audit")]
        public async Task FailedWriteUndoesTheWholeAction(string failing)
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345));
            var faults = new Faults { FailWrites = failing == "blacklist", FailAudit = failing == "audit" ? AuditOutcome.Ok : null };
            await using var factory = Faulty(db, faults);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.27");
            var page = await OpenAsync(admin, 12345);

            foreach (var response in new[]
            {
                await AddAsync(admin, ("steamid", "54321"), ("name", "New Player")),
                await EditAsync(admin, 12345, page, ("name", "Renamed Player")),
                await RemoveAsync(admin, 12345, page)
            })
            {
                await Answered(response, HttpStatusCode.ServiceUnavailable, ErrorResponses.Unavailable);
                Assert.DoesNotContain(BlacklistWords.Unsure, await ArmorSteps.TextAsync(response));
                Assert.DoesNotContain(BlacklistWords.AddUnsure, await ArmorSteps.TextAsync(response));
            }

            Assert.Null(await db.EntryAsync(54321));
            Assert.Equal("Test Player", (await db.EntryAsync(12345))?.username);
            Assert.Empty(await db.BlacklistAuditAsync());
        }

        // A limit that passes during the commit can't stop it, so the change can land while the page says it can't tell.
        // A row says so in the audit log, outside the transaction. Trying again is safe: the entry is no longer as the form
        // expects, so nothing more is written, and the page says the change was already made.
        [Theory]
        [InlineData("add")]
        [InlineData("edit")]
        [InlineData("remove")]
        [InlineData("number")]
        public async Task TimeoutAfterTheCommitSaysItMightHaveSaved(string action)
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345));
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username) VALUES (5000000000, 'Cut Player')");
            var faults = new Faults { TimeOutAfterCommit = true };
            await using var factory = Faulty(db, faults);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.28");
            var page = await OpenAsync(admin, 12345);
            var numberVersion = NumberVersion(await admin.Client.GetStringAsync("/admin/blacklist", Token));

            Task<HttpResponseMessage> Act() => action switch
            {
                "add" => AddAsync(admin, ("steamid", "54321"), ("name", "New Player"), ("reason", ""), ("expires", ""), ("notes", "")),
                "edit" => EditAsync(admin, 12345, page, ("name", "Renamed Player")),
                "number" => CraftedAsync(admin, "/admin/blacklist?handler=RemoveNumber", page.Token, ("number", "5000000000"), ("version", numberVersion)),
                _ => RemoveAsync(admin, 12345, page)
            };

            var unsure = await Act();
            await Answered(unsure, HttpStatusCode.ServiceUnavailable, action switch
            {
                "add" => BlacklistWords.AddUnsure,
                "number" => BlacklistWords.NumberUnsure,
                _ => BlacklistWords.Unsure
            });
            var html = await unsure.Content.ReadAsStringAsync(Token);

            // An add may not have made an entry, whose page would then be missing, so it links the list's search for the
            // account, which finds the entry if there is one
            if (action == "add")
                Assert.Contains("<a href=\"/admin/blacklist?q=%5BU%3A1%3A54321%5D\">Search the blacklist for the account</a>", html);
            Assert.Contains(action == "number" ? "<title>Not sure it was removed - MinerBot dashboard</title>" : "<title>Not sure it was saved - MinerBot dashboard</title>", html);
            PageTests.AssertAccessible("a blacklist change that may not have landed", html);
            Assert.DoesNotContain(ErrorResponses.Unavailable, WebUtility.HtmlDecode(html));
            Assert.Contains(factory.Logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("may or may not have been saved"));

            // It landed, and the audit log has its row and the one saying it may not have
            var rows = await db.BlacklistAuditAsync();
            Assert.Equal([AuditOutcome.Ok, AuditOutcome.Unknown], rows.Select(row => row.outcome));
            Assert.Equal(action == "number" ? "blacklist.remove" : "blacklist." + action, rows[1].action);
            Assert.Equal(UnconfirmedChanges.Detail, rows[1].detail);
            Assert.Equal(rows[0].before_json, rows[1].before_json);
            Assert.Equal(rows[0].after_json, rows[1].after_json);
            Assert.Equal(rows[0].target_id, rows[1].target_id);
            Assert.Equal(rows[0].request_id, rows[1].request_id);
            Assert.Equal("198.51.100.28", rows[1].ip);

            faults.TimeOutAfterCommit = false;
            var again = await Act();
            var notice = action switch
            {
                "add" => BlacklistWords.AlreadyAdded,
                "edit" => BlacklistWords.AlreadySaved,
                "number" => BlacklistWords.NumberAlreadyRemoved,
                _ => BlacklistWords.AlreadyRemoved
            };

            await Answered(again, HttpStatusCode.Conflict, notice);
            Assert.Contains("class=\"notice notice-done\" role=\"status\"", await again.Content.ReadAsStringAsync(Token));
            Assert.Equal(AuditOutcome.Refused, (await db.BlacklistAuditAsync())[^1].outcome);
        }

        // The row saying a change may not have landed is best effort: when it can't be written either, the page still says
        // the change may or may not have been saved, rather than that nothing was
        [Fact]
        public async Task UnsureRowThatCantBeWrittenStillShowsUnsure()
        {
            var db = await fixture.ReadyAsync();
            var faults = new Faults { TimeOutAfterCommit = true, FailAudit = AuditOutcome.Unknown };
            await using var factory = Faulty(db, faults);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.29");

            var unsure = await AddAsync(admin, ("steamid", "54321"), ("name", "New Player"));

            await Answered(unsure, HttpStatusCode.ServiceUnavailable, BlacklistWords.AddUnsure);
            Assert.NotNull(await db.EntryAsync(54321));
            Assert.Equal([AuditOutcome.Ok], (await db.BlacklistAuditAsync()).Select(row => row.outcome));
            Assert.Contains(factory.Logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("couldn't be written either"));
        }

        // A refusal commits only its audit row, and an edit that changes nothing commits nothing, so a commit that times out
        // after one leaves the entry as the work found it. The page says what the work decided, not that it can't tell, and
        // no row says a change may have landed.
        [Theory]
        [InlineData("edit")]
        [InlineData("unchanged")]
        [InlineData("add")]
        [InlineData("remove")]
        [InlineData("number")]
        public async Task TimeoutAfterARefusalOrNoChangeKeepsItsAnswer(string action)
        {
            var db = await fixture.ReadyAsync();
            await db.AddEntriesAsync(Entry(12345));
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username) VALUES (5000000000, 'Cut Player')");
            var faults = new Faults();
            await using var factory = Faulty(db, faults);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.38");
            var page = await OpenAsync(admin, 12345);
            var numberVersion = NumberVersion(await admin.Client.GetStringAsync("/admin/blacklist", Token));

            // Someone else's change since the pages opened, which the refusals find
            if (action != "unchanged")
                await db.RootExecuteAsync($"UPDATE {MariaDbFixture.BlacklistTable} SET username = 'Renamed Elsewhere' WHERE steam_id IN (12345, 5000000000)");

            faults.TimeOutAfterCommit = true;

            var (response, status, notice) = action switch
            {
                "edit" => (await EditAsync(admin, 12345, page, ("name", "My Name")), HttpStatusCode.Conflict, BlacklistWords.Changed),
                "unchanged" => (await EditAsync(admin, 12345, page), HttpStatusCode.OK, BlacklistWords.Unchanged),
                "add" => (await AddAsync(admin, ("steamid", "12345"), ("name", "My Name")), HttpStatusCode.Conflict, BlacklistWords.Exists),
                "remove" => (await RemoveAsync(admin, 12345, page), HttpStatusCode.Conflict, BlacklistWords.RemoveChanged),
                _ => (await CraftedAsync(admin, "/admin/blacklist?handler=RemoveNumber", page.Token, ("number", "5000000000"), ("version", numberVersion)),
                    HttpStatusCode.Conflict, BlacklistWords.NumberChanged)
            };

            await Answered(response, status, notice);
            Assert.DoesNotContain("can't tell whether", await ArmorSteps.TextAsync(response));

            string[] outcomes = action == "unchanged" ? [] : [AuditOutcome.Refused];
            Assert.Equal(outcomes, (await db.BlacklistAuditAsync()).Select(row => row.outcome));
            Assert.Equal(action == "unchanged" ? "Test Player" : "Renamed Elsewhere", (await db.EntryAsync(12345))?.username);
            Assert.Contains(factory.Logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("which changes nothing"));
        }

        // A transaction that loses a deadlock runs again from the start, and its rows are written once
        [Fact]
        public async Task DeadlockedActionRunsAgainAndWritesOnce()
        {
            var db = await fixture.ReadyAsync();
            var faults = new Faults { DeadlockFirstRun = true };
            await using var factory = Faulty(db, faults);
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.30");

            Redirected(await AddAsync(admin, ("steamid", "54321"), ("name", "New Player")), 54321, "added");

            Assert.Equal(2, faults.Runs);
            Assert.Equal(1, await db.EntryCountAsync());
            Assert.Single(await db.BlacklistAuditAsync());
        }

        // The website's database down: the pages fail closed, an action that couldn't read says nothing changed, and
        // nothing of the connection reaches the logs
        [Fact]
        public async Task WebsiteDatabaseDownIsUnavailable()
        {
            var db = await fixture.ReadyAsync();
            var dead = new DeadDatabase();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions { SiteConnection = dead.SiteConnection });
            var admin = await ArmorSteps.ReviewerAsync(factory, "198.51.100.31");
            var token = await SignInSteps.FormTokenAsync(admin, "/admin/blacklist/add");

            foreach (var path in new[] { "/admin/blacklist", "/admin/blacklist/12345" })
                await Answered(await admin.Client.GetAsync(path, Token), HttpStatusCode.ServiceUnavailable, ErrorResponses.Unavailable);

            await Answered(await CraftedAsync(admin, "/admin/blacklist/add", token, ("steamid", "12345")), HttpStatusCode.ServiceUnavailable, ErrorResponses.Unavailable);
            await Answered(await CraftedAsync(admin, "/admin/blacklist/12345?handler=Remove", token, ("version", new string('a', 64))),
                HttpStatusCode.ServiceUnavailable, ErrorResponses.Unavailable);

            foreach (var secret in new[] { dead.Login, dead.Password, dead.SiteDatabase })
                Assert.DoesNotContain(secret, factory.Logs.Text);
        }

        static TimeoutException Dropped() => new("The database didn't answer within 15 seconds");

        // What the blacklist's and the audit log's repositories should do wrong. One for the whole app, since each request
        // gets repositories of their own.
        sealed class Faults
        {
            public bool DeadlockFirstRun { get; set; }
            public bool TimeOutAfterCommit { get; set; }

            // An add, an edit or a removal fails as a dropped connection would, after its statement ran
            public bool FailWrites { get; set; }

            // An audit row with this outcome fails the same way, after it's written for ok and without being written for
            // unknown
            public string? FailAudit { get; set; }

            // How many times a transaction's work ran
            public int Runs;

            // The reads of one row a transaction's work made: with the transaction, which locks the row, or without it. Set
            // inside the work, which its own calls see and nothing outside it does, such as the pages' reads.
            public readonly AsyncLocal<bool> InWork = new();
            public readonly ConcurrentQueue<string> LockedReads = new();
            public readonly ConcurrentQueue<string> UnlockedReads = new();

            public void Read(string call, IFormattable key, DataTransaction? transaction)
            {
                if (InWork.Value)
                    (transaction == null ? UnlockedReads : LockedReads).Enqueue(call + " " + key.ToString(null, CultureInfo.InvariantCulture));
            }

            // What the next read of an entry outside a transaction's work finds instead of the row, once, such as the edit
            // handler's read before its checks
            public BlacklistModel? HandlerSees;

            // 1 makes that next read find no entry instead, once, whatever the row. With both set, HandlerSees answers the
            // first read and this one the read after it.
            public int HandlerSeesNone;
        }

        static DashboardFactory Faulty(MariaDbFixture db, Faults faults) => new(db, new DashboardFactoryOptions
        {
            Services = services =>
            {
                services.Replace(ServiceDescriptor.Transient<IBlacklistData>(provider => new Blacklist(ActivatorUtilities.CreateInstance<BlacklistData>(provider), faults)));
                services.Replace(ServiceDescriptor.Transient<IAuditLogData>(provider => new Audit(ActivatorUtilities.CreateInstance<AuditLogData>(provider), faults)));
            }
        });

        // The real repository, whose transactions can lose a deadlock on their first run or time out once they've
        // committed, and whose writes can fail after their statement
        sealed class Blacklist(BlacklistData real, Faults faults) : IBlacklistData
        {
            public async Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work)
            {
                var result = await real.RunInTransaction(async transaction =>
                {
                    faults.InWork.Value = true;
                    var done = await work(transaction);

                    // InnoDB has rolled the whole transaction back when it reports a deadlock, which the rollback here stands for
                    if (Interlocked.Increment(ref faults.Runs) == 1 && faults.DeadlockFirstRun)
                        throw MySqlErrors.Deadlock();

                    return done;
                });

                if (faults.TimeOutAfterCommit)
                    throw Dropped();

                return result;
            }

            public async Task InsertPlayerBan(BlacklistModel player, DataTransaction? transaction = null)
            {
                await real.InsertPlayerBan(player, transaction);
                ThrowIfFailing();
            }

            public async Task<bool> UpdatePlayerBan(BlacklistModel player, DataTransaction? transaction = null)
            {
                var updated = await real.UpdatePlayerBan(player, transaction);
                ThrowIfFailing();
                return updated;
            }

            public async Task<bool> RemovePlayerBan(uint steamID, DataTransaction? transaction = null)
            {
                var removed = await real.RemovePlayerBan(steamID, transaction);
                ThrowIfFailing();
                return removed;
            }

            void ThrowIfFailing()
            {
                if (faults.FailWrites)
                    throw Dropped();
            }

            public Task<BlacklistModel?> GetBannedPlayer(uint steamID, DataTransaction? transaction = null)
            {
                faults.Read(nameof(GetBannedPlayer), steamID, transaction);

                if (transaction == null && !faults.InWork.Value && Interlocked.Exchange(ref faults.HandlerSees, null) is { } seen)
                    return Task.FromResult<BlacklistModel?>(seen);

                if (transaction == null && !faults.InWork.Value && Interlocked.Exchange(ref faults.HandlerSeesNone, 0) == 1)
                    return Task.FromResult<BlacklistModel?>(null);

                return real.GetBannedPlayer(steamID, transaction);
            }

            public Task<OutOfRangeBan?> GetOutOfRangeBan(decimal steamId, DataTransaction? transaction = null)
            {
                faults.Read(nameof(GetOutOfRangeBan), steamId, transaction);
                return real.GetOutOfRangeBan(steamId, transaction);
            }

            public Task<IEnumerable<BlacklistModel>> GetBlacklist() => real.GetBlacklist();
            public Task<IReadOnlyList<OutOfRangeBan>> ListOutOfRangeBans() => real.ListOutOfRangeBans();
            public Task<IReadOnlyList<decimal>> ListOutOfRangeSteamIds() => real.ListOutOfRangeSteamIds();
            public Task<bool> RemoveOutOfRangeSteamId(decimal steamId, DataTransaction? transaction = null) => real.RemoveOutOfRangeSteamId(steamId, transaction);
            public Task<Page<BlacklistModel>> SearchBlacklist(uint? accountNumber, string? username, int page, int pageSize) => real.SearchBlacklist(accountNumber, username, page, pageSize);
        }

        sealed class Audit(AuditLogData real, Faults faults) : IAuditLogData
        {
            public async Task AddAudit(AuditEntry entry, DataTransaction? transaction = null)
            {
                if (faults.FailAudit == AuditOutcome.Unknown && entry.outcome == AuditOutcome.Unknown)
                    throw Dropped();

                await real.AddAudit(entry, transaction);

                if (faults.FailAudit == entry.outcome && entry.action.StartsWith("blacklist.", StringComparison.Ordinal))
                    throw Dropped();
            }

            public Task<IReadOnlyList<AuditEntry>> LatestChanges(string targetType, IReadOnlyCollection<string> targetIds) => real.LatestChanges(targetType, targetIds);
            public Task<Page<AuditEntry>> ListAudit(AuditQuery query) => real.ListAudit(query);
        }
    }
}
