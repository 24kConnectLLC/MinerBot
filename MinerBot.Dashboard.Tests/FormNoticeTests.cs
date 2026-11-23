using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using DataAccessLibrary;
using DataAccessLibrary.Blacklist;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Blacklist;
using MinerBot.Dashboard.Hosting;
using MinerBot.Dashboard.Pages.Admin;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Status;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Tests
{
    // A form that wasn't sent on is answered with a redirect to its page's own address and ?notice=, whose GET draws it from
    // a cookie of its own, protected under the session's key: reloading it sends nothing, the typed word is never in it, it
    // opens only for its own session, at its own address and within 30 minutes, it survives a restart, and it fits in one
    // cookie. The controls page's forms show it here; each page's own tests show its forms' notices.
    [Collection("Database")]
    public partial class FormNoticeTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        const string NotConfirmed = "Type pause in the box to confirm. Nothing was sent.";

        static async Task<TestBrowser> SignedInAsync(DashboardFactory factory, TestUser user, string address)
        {
            var browser = factory.Visitor(address);
            await SignInSteps.SignedInAsync(factory, browser, user);
            return browser;
        }

        // A pause refused for its typed word, with its reason kept, as the live check presses one: its notice's address
        static async Task<string> RefusedPauseAsync(TestBrowser browser, string reason = "Testing the notice") =>
            await RequestSteps.NoticeOfAsync(await RequestSteps.PostControlAsync(browser, await RequestSteps.ControlsAsync(browser), ControlWords.Pause,
                ("reason", reason), ("confirm", "paws")));

        // A page's text without its markup, so a sentence reads as one string
        static string Text(string html) => Regex.Replace(Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(html), "<[^>]+>", " "), "\\s+", " "), " ([.,:])(?= |$)", "$1");

        static Task<string> HtmlAsync(HttpResponseMessage response) => response.Content.ReadAsStringAsync(Token);

        // The page drawn as it is now, with the line that says the notice has gone, and no alert
        static async Task AssertGoneAsync(string what, HttpResponseMessage response)
        {
            var html = await HtmlAsync(response);

            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{what} answered {response.StatusCode}");
            Assert.Contains(RequestWords.NoticeGone, Text(html));
            Assert.DoesNotContain("role=\"alert\"", html);
            PageTests.AssertAccessible(what, html);
        }

        // A restart refused for the bot's limit, reloaded twice and again once the limit has passed: each reload draws the
        // same words with a new key, and sends nothing, writes no audit row and leaves the cookie as it is. A press on the
        // form it drew then sends. Fails if the answer is the page drawn on the POST, or a reload sends the form again.
        [Fact]
        public async Task ReloadingARefusalSendsNothing()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.160");
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            await RequestSteps.TakenAsync(db, factory, RequestKinds.BotRestart, now.AddMinutes(-2).AddSeconds(-1));

            var refused = await RequestSteps.PostControlAsync(owner, await RequestSteps.ControlsAsync(owner), ControlWords.Restart, ("confirm", "restart"));
            var address = await RequestSteps.NoticeOfAsync(refused);
            var audit = (await db.AuditAsync()).Count;
            var words = $"The bot takes this at most once every 10 minutes, and last took one at {RequestWords.Clock(now.AddMinutes(-2))}, so nothing was sent. "
                + $"Try again after {RequestWords.Clock(now.AddMinutes(8))}.";
            var keys = new List<string>();
            var html = "";

            foreach (var later in new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMinutes(8) + TimeSpan.FromSeconds(1) })
            {
                factory.Clock.Advance(later);
                var reload = await owner.Client.GetAsync(address, Token);
                html = await HtmlAsync(reload);

                Assert.Equal(HttpStatusCode.Conflict, reload.StatusCode);
                Assert.Contains(words, Text(html));
                Assert.False(RequestSteps.TouchesNotice(reload), "A reload wrote the notice's cookie");
                keys.Add(RequestSteps.ReadControls(html).Keys[ControlWords.Restart]);
            }

            Assert.Single(await db.SentAsync());
            Assert.Equal(audit, (await db.AuditAsync()).Count);
            Assert.Equal(3, keys.Distinct().Count());

            var sent = await RequestSteps.PostControlAsync(owner, RequestSteps.ReadControls(html), ControlWords.Restart, ("confirm", "restart"));
            var row = (await db.SentAsync())[^1];
            Assert.Equal(2, (await db.SentAsync()).Count);
            Assert.Equal((HttpStatusCode.Redirect, $"/admin/bot/controls?request={row.id}#result"), (sent.StatusCode, SignInSteps.PathOf(sent.Headers.Location)));
        }

        // A pause refused with the right word typed, and one refused for a wrong word, each keep the reason, which the GET
        // puts back in its box, and never the typed word, whose box is empty: the notice, opened as the app opens it, holds
        // the reason alone. A leave keeps no box at all: refused for a name that reads otherwise, and for a sign-in that's
        // too old, its notice never holds the server's name as typed, and its box is empty. Fails if the typed word, or the
        // typed name, goes in the notice.
        [Fact]
        public async Task TheNoticeKeepsTheTextNeverTheWord()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.161");
            var page = await RequestSteps.ControlsAsync(owner);

            foreach (var (reason, word, says) in new[]
            {
                ("Wait​ing", "pause", "The bot would refuse this, so nothing was sent: the reason has a character that shows nothing or changes the text's direction."),
                ("Testing the notice", "paws-word-7f3a", NotConfirmed)
            })
            {
                var answer = await RequestSteps.AnsweredAsync(owner, await RequestSteps.PostControlAsync(owner, page, ControlWords.Pause, ("reason", reason), ("confirm", word)));
                var html = await HtmlAsync(answer);

                Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
                Assert.Contains(says, Text(html));
                Assert.Equal(reason, BlacklistSteps.BoxValue(html, "reason"));
                Assert.Contains("name=\"confirm\"", html);
                Assert.DoesNotContain($"value=\"{word}\"", html);

                var notice = RequestSteps.OpenNotice(factory, owner);
                Assert.Equal((ControlWords.Pause, 400, false, (string?)null), (notice.Form, notice.Status, notice.Unsure, notice.Key));
                Assert.Equal(["text"], notice.Kept!.Keys);
                Assert.Equal(reason, notice.Kept["text"]);
                Assert.DoesNotContain(word, Encoding.UTF8.GetString(RequestSteps.NoticeJson(factory, owner)), StringComparison.Ordinal);
            }

            await StatusSteps.ServersAsync(db);
            var leave = $"/admin/servers/{StatusSteps.ServerId}/leave";

            foreach (var (typed, later, status) in new[]
            {
                ("Typed Server 7f3a", TimeSpan.Zero, HttpStatusCode.BadRequest),
                ("Typed Server 9c1e", TimeSpan.FromMinutes(16), HttpStatusCode.Forbidden)
            })
            {
                var drawn = await owner.Client.GetStringAsync(leave, Token);
                factory.Clock.Advance(later);
                var answer = await RequestSteps.AnsweredAsync(owner, await SignInSteps.PostAsync(owner, leave, SignInSteps.FormToken(drawn), origin: DashboardFactory.PublicOrigin,
                    fields: [new("formkey", LeaveKey().Match(drawn).Groups[1].Value), new("confirm", typed)]));
                var html = await HtmlAsync(answer);

                Assert.Equal(status, answer.StatusCode);
                Assert.DoesNotContain(typed, html);

                var notice = RequestSteps.OpenNotice(factory, owner);
                Assert.Equal(("leave", (int)status), (notice.Form, notice.Status));
                Assert.Null(notice.Kept);
                Assert.DoesNotContain(typed, Encoding.UTF8.GetString(RequestSteps.NoticeJson(factory, owner)), StringComparison.Ordinal);
            }
        }

        // The leave form's key, the form's first field
        [GeneratedRegex("""action="/admin/servers/[0-9]+/leave"[^>]*>\s*<input type="hidden" name="formkey" value="([0-9a-f]{32})" />""")]
        private static partial Regex LeaveKey();

        // Text shaped like a date or a GUID, text outside ASCII, a character outside the Basic Multilingual Plane included,
        // and text as long as its box takes, each box its own, come back exactly as typed: a pause's reason and a status text.
        // Fails if a stored text is read back as anything else, as TempData's serializer reads a date or a GUID, or a box is
        // kept only within another box's limit.
        [Fact]
        public async Task TypedTextComesBackAsTyped()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.162");
            var page = await RequestSteps.ControlsAsync(owner);

            foreach (var reason in new[] { "2027-01-02", "0f8fad5b-d9cb-469f-a165-70867728950e", "Zoë, 日本語 and \U0001F3AE", new string('r', RequestRules.ReasonLimit) })
            {
                var answer = await RequestSteps.AnsweredAsync(owner, await RequestSteps.PostControlAsync(owner, page, ControlWords.Pause, ("reason", reason), ("confirm", "paws")));
                var html = await HtmlAsync(answer);

                Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
                Assert.Equal(reason, BlacklistSteps.BoxValue(html, "reason"));
                Assert.Equal(reason, RequestSteps.OpenNotice(factory, owner).Kept!["text"]);
            }

            // A status text refused because its form's key belongs to another kind's request
            foreach (var text in new[] { "2026-11-22", new string('s', RequestRules.StatusTextLimit) })
            {
                var taken = RequestFields.NewKey();
                await RequestSteps.AddAsync(db, factory, RequestKinds.CachesClear, factory.Clock.GetUtcNow().UtcDateTime, key: taken);
                var status = await RequestSteps.AnsweredAsync(owner, await RequestSteps.PostControlAsync(owner, ControlWords.SetStatus, page.Token, taken, ("text", text)));
                var statusHtml = await HtmlAsync(status);

                Assert.Equal(HttpStatusCode.Conflict, status.StatusCode);
                Assert.Contains(RequestWords.KeyTaken, Text(statusHtml));
                Assert.Equal(text, BlacklistSteps.BoxValue(statusHtml, "text"));
            }
        }

        // The notice's cookie is a __Host- cookie, like the session's, so murderminershub.com can neither set nor replace it:
        // Secure, HttpOnly, SameSite=Lax, Path=/ and no Domain, ending after 30 minutes, with nothing typed in its value.
        // Fails if any of these is lost.
        [Fact]
        public async Task TheNoticeCookieIsAHostCookie()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.163");

            var refused = await RequestSteps.PostControlAsync(owner, await RequestSteps.ControlsAsync(owner), ControlWords.Pause,
                ("reason", "Testing the cookie"), ("confirm", "paws"));
            await RequestSteps.NoticeOfAsync(refused);
            var parts = RequestSteps.NoticeCookie(refused)!.Split(';').Select(part => part.Trim()).ToList();

            Assert.Equal(DashboardAuth.NoticeCookie, parts[0].Split('=', 2)[0]);
            Assert.StartsWith("__Host-", DashboardAuth.NoticeCookie, StringComparison.Ordinal);
            Assert.Matches("^[A-Za-z0-9_-]+$", parts[0].Split('=', 2)[1]);
            Assert.Equal(["httponly", "max-age=1800", "path=/", "samesite=lax", "secure"], parts.Skip(1).Select(part => part.ToLowerInvariant()).Order(StringComparer.Ordinal));
            Assert.Equal(TimeSpan.FromMinutes(30), FormNotices.Lifetime);
            Assert.DoesNotContain("Testing", string.Join(";", parts));
        }

        // A notice keeps to one cookie well within nginx's 8 KB for a request's Cookie header: a made-up reason of 60 KB isn't
        // kept, and the blacklist add's every box at its limit in a script of 3-byte characters, each with a field's message,
        // keeps every message and every box but the longest. Text the JSON still escapes, such as apostrophes or emoji, costs
        // more, and the budget, which measures the JSON itself, drops more of its boxes. Fails if the budget is gone, or if the
        // JSON writes each 3-byte character as a 6-byte escape, when more boxes have to go.
        [Fact]
        public async Task TheNoticeFitsOneCookie()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.164");

            var refused = await RequestSteps.PostControlAsync(owner, await RequestSteps.ControlsAsync(owner), ControlWords.Pause,
                ("reason", new string('a', 60 * 1024)), ("confirm", "pause"));
            var answer = await RequestSteps.AnsweredAsync(owner, refused);
            var html = await HtmlAsync(answer);

            Assert.True(Encoding.UTF8.GetByteCount(RequestSteps.NoticeCookie(refused)!) < 3000, "The made-up reason's notice is too big");
            Assert.Null(RequestSteps.OpenNotice(factory, owner).Kept);

            // A box is kept up to its maxlength in UTF-16 units, all a browser sends, and never past it
            Assert.Equal(new string('a', RequestRules.ReasonLimit), FormNotices.Text(new string('a', RequestRules.ReasonLimit), RequestRules.ReasonLimit));
            Assert.Null(FormNotices.Text(new string('a', RequestRules.ReasonLimit + 1), RequestRules.ReasonLimit));
            Assert.Null(FormNotices.Text(null, RequestRules.ReasonLimit));
            Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
            Assert.Contains("The bot would refuse this, so nothing was sent:", Text(html));
            Assert.Contains("name=\"reason\"", html);
            Assert.Equal("", BlacklistSteps.BoxValue(html, "reason") ?? "");

            // The blacklist add's fields, each at its box's limit in a script whose every character is 3 bytes in UTF-8, each
            // with the longest message the page can give for it, beside the lookup a form carries
            static string Box(int limit) => string.Concat(Enumerable.Repeat("語", limit));

            var boxes = new Dictionary<string, string>
            {
                ["steamid"] = Box(BlacklistForm.SteamIdLimit),
                ["name"] = Box(BlacklistData.UsernameLimit),
                ["reason"] = Box(BlacklistData.ReasonLimit),
                ["expires"] = Box(64),
                ["notes"] = Box(BlacklistData.NotesLimit)
            };
            var kept = new Dictionary<string, string>(boxes);

            foreach (var (field, message) in LongestProblems())
                kept[FormNotices.ProblemPrefix + field] = message;

            var protection = new EphemeralDataProtectionProvider();
            var notices = new FormNotices(protection, new FakeClock());
            var key = SessionKeys.New();
            var page = NoticePage.At("/admin/blacklist/add", key);
            var largest = new FormNotice { Form = "add", Status = 400, State = "fields", Lookup = ulong.MaxValue, Kept = kept };

            notices.Keep(page, largest);

            var json = protection.CreateProtector(FormNotices.Purpose).CreateProtector(key).Unprotect(Base64Url.DecodeFromChars(page.NoticeValue!));
            var stored = JsonSerializer.Deserialize<FormNotice>(json)!;
            var whole = JsonSerializer.SerializeToUtf8Bytes(stored with { Kept = kept }, Written);

            Assert.True(whole.Length > FormNotices.Budget, $"The whole notice is {whole.Length} bytes, within the budget");
            Assert.True(json.Length <= FormNotices.Budget, $"The notice kept is {json.Length} bytes");
            Assert.True(Encoding.UTF8.GetByteCount(page.SetNotice!) < 3000, $"The notice's Set-Cookie is {page.SetNotice!.Length} bytes");
            Assert.Equal(kept.Keys.Where(name => name != "notes").Order(StringComparer.Ordinal), stored.Kept!.Keys.Order(StringComparer.Ordinal));
            Assert.All(stored.Kept, entry => Assert.Equal(kept[entry.Key], entry.Value));
            Assert.Equal("notes", boxes.MaxBy(box => Encoding.UTF8.GetByteCount(box.Value)).Key);

            // The add page itself, sent every box at its limit in that script, each with a problem, as a pasted tab in a text
            // box gives: one cookie, and its GET draws every field's message, with each box as typed, or empty once dropped,
            // the longest first
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.177");
            var typed = new Dictionary<string, string>
            {
                ["steamid"] = Box(BlacklistForm.SteamIdLimit),
                ["name"] = Box(30) + "\t" + Box(BlacklistData.UsernameLimit - 31),
                ["reason"] = Box(30) + "\t" + Box(BlacklistData.ReasonLimit - 31),
                ["expires"] = Box(64),
                ["notes"] = Box(60) + "\t" + Box(BlacklistData.NotesLimit - 61)
            };
            var refusedAdd = await BlacklistSteps.AddAsync(admin, [.. typed.Select(box => (box.Key, box.Value))]);
            var setNotice = RequestSteps.NoticeCookie(refusedAdd)!;
            var added = await RequestSteps.AnsweredAsync(admin, refusedAdd);
            var addHtml = await HtmlAsync(added);
            var dropped = typed.Keys.Where(box => BlacklistSteps.BoxValue(addHtml, box) != typed[box]).ToList();

            Assert.True(Encoding.UTF8.GetByteCount(setNotice) < 3000, $"The add's notice's Set-Cookie is {setNotice.Length} bytes");
            Assert.True(RequestSteps.NoticeJson(factory, admin).Length <= FormNotices.Budget, "The add's notice is over the budget");
            Assert.Equal(HttpStatusCode.BadRequest, added.StatusCode);
            Assert.All(typed.Keys, box => Assert.Contains($"id=\"{box}-problem\"", addHtml));
            Assert.All(dropped, box => Assert.Equal("", BlacklistSteps.BoxValue(addHtml, box)));
            Assert.True(dropped.Count == 0 || dropped.SequenceEqual(["notes"]), $"The boxes dropped are {string.Join(", ", dropped)}");
            Assert.Equal(0, await db.EntryCountAsync());
        }

        // The notice's JSON as FormNotices writes it
        static readonly JsonSerializerOptions Written = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

        // The longest message the blacklist add can give for each of its fields
        static Dictionary<BlacklistField, string> LongestProblems() => new()
        {
            [BlacklistField.SteamId] = new[] { BlacklistWords.CustomLinkOff, BlacklistWords.SteamIdHint, BlacklistWords.LookupNoProfile,
                BlacklistWords.Problem(BlacklistField.SteamId, TextProblem.TooLong) }.MaxBy(message => message.Length)!,
            [BlacklistField.Name] = BlacklistWords.Problem(BlacklistField.Name, TextProblem.ControlCharacter),
            [BlacklistField.Reason] = BlacklistWords.Problem(BlacklistField.Reason, TextProblem.ControlCharacter),
            [BlacklistField.Expires] = BlacklistWords.BadDate,
            [BlacklistField.Notes] = BlacklistWords.Problem(BlacklistField.Notes, TextProblem.ControlCharacter)
        };

        // Over the budget, the box that takes the most room in the JSON goes first, which isn't always the longest in UTF-8:
        // the JSON still writes < > & ' " + and ` as 6-byte escapes. The blacklist add's boxes, each with a field's message, a
        // Steam ID box of 100 '<' and a name and a reason of quotes, beside notes of 120 plain characters: without the notes,
        // the longest in UTF-8, the notice is still over the budget, while without the Steam ID box alone it fits, so only
        // that box goes, and the notes are kept as typed. Fails if the boxes are measured in UTF-8, when the notes go too.
        [Fact]
        public void TheBoxThatTakesTheMostRoomGoesFirst()
        {
            var boxes = new Dictionary<string, string>
            {
                ["steamid"] = new string('<', BlacklistForm.SteamIdLimit),
                ["name"] = new string('"', BlacklistData.UsernameLimit),
                ["reason"] = new string('"', BlacklistData.ReasonLimit),
                ["expires"] = "2027-01-02",
                ["notes"] = new string('a', BlacklistData.NotesLimit)
            };
            var kept = new Dictionary<string, string>(boxes);

            foreach (var (field, message) in LongestProblems())
                kept[FormNotices.ProblemPrefix + field] = message;

            var protection = new EphemeralDataProtectionProvider();
            var notices = new FormNotices(protection, new FakeClock());
            var key = SessionKeys.New();
            var page = NoticePage.At("/admin/blacklist/add", key);

            notices.Keep(page, new FormNotice { Form = "add", Status = 400, State = "fields", Lookup = ulong.MaxValue, Kept = kept });

            var json = protection.CreateProtector(FormNotices.Purpose).CreateProtector(key).Unprotect(Base64Url.DecodeFromChars(page.NoticeValue!));
            var stored = JsonSerializer.Deserialize<FormNotice>(json)!;

            // The notice as Keep would write it with every message and every box but this one
            int Without(string box) => JsonSerializer.SerializeToUtf8Bytes(stored with { Kept = kept.Where(entry => entry.Key != box).ToDictionary() }, Written).Length;

            Assert.Equal("notes", boxes.MaxBy(box => Encoding.UTF8.GetByteCount(box.Value)).Key);
            Assert.True(Without("notes") > FormNotices.Budget, $"Without the notes the notice is {Without("notes")} bytes, within the budget");
            Assert.True(Without("steamid") <= FormNotices.Budget, $"Without the Steam ID box the notice is {Without("steamid")} bytes, over the budget");
            Assert.True(json.Length <= FormNotices.Budget, $"The notice kept is {json.Length} bytes");
            Assert.Equal(kept.Keys.Where(name => name != "steamid").Order(StringComparer.Ordinal), stored.Kept!.Keys.Order(StringComparer.Ordinal));
            Assert.All(stored.Kept, entry => Assert.Equal(kept[entry.Key], entry.Value));
        }

        // A notice opens only for the session that made it: another person's browser given the cookie, and the same browser
        // once signed in again, get the page as it is now, with the line that says the notice has gone, and the cookie is
        // deleted. Fails if a notice opens without its session's key.
        [Fact]
        public async Task ANoticeIsOnlyItsSessions()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.165");
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.166");
            var address = await RefusedPauseAsync(owner);

            Assert.Equal(HttpStatusCode.BadRequest, (await owner.Client.GetAsync(address, Token)).StatusCode);

            admin.Jar.Add(DashboardFactory.PublicAddress, new Cookie(DashboardAuth.NoticeCookie, owner.Cookie(DashboardAuth.NoticeCookie)) { Secure = true });
            var theirs = await admin.Client.GetAsync(address, Token);
            await AssertGoneAsync("another person's notice", theirs);
            Assert.True(RequestSteps.DeletesNotice(theirs));
            Assert.Null(admin.Cookie(DashboardAuth.NoticeCookie));

            await SignInSteps.SignInAgainAsync(factory, owner, TestUsers.Owner, "/admin/bot/controls");
            var again = await owner.Client.GetAsync(address, Token);
            await AssertGoneAsync("a notice from before a sign-in again", again);
            Assert.True(RequestSteps.DeletesNotice(again));
            Assert.Null(owner.Cookie(DashboardAuth.NoticeCookie));
        }

        // A notice shows only at its own address: a made-up ID, or its ID at another address, gets the page as it is now with
        // the line that says it has gone, and the cookie is left alone, as another tab's notice would be. Without ?notice=,
        // or with one that isn't an ID, the page has no such line. Another server's or armor's page with the same ID, and its
        // own page for a form it doesn't have, show nothing either. Fails if Find leaves out the address, the ID or the form.
        [Fact]
        public async Task ANoticeShowsOnlyAtItsAddress()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.167");
            var address = await RefusedPauseAsync(owner);
            var id = RequestSteps.NoticeId(address);

            foreach (var other in new[] { "/admin/bot/controls?notice=" + RequestFields.NewKey(), "/admin/bot/controls/?notice=" + id })
            {
                var response = await owner.Client.GetAsync(other, Token);
                await AssertGoneAsync(other, response);
                Assert.False(RequestSteps.TouchesNotice(response), $"{other} wrote the notice's cookie");
            }

            foreach (var plain in new[] { "/admin/bot/controls", "/admin/bot/controls?notice=" + id.ToUpperInvariant(), "/admin/bot/controls?notice=made-up" })
            {
                var response = await owner.Client.GetAsync(plain, Token);
                var html = await HtmlAsync(response);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.DoesNotContain(RequestWords.NoticeGone, Text(html));
                Assert.DoesNotContain("role=\"alert\"", html);
                Assert.False(RequestSteps.TouchesNotice(response), $"{plain} wrote the notice's cookie");
            }

            Assert.Equal(HttpStatusCode.BadRequest, (await owner.Client.GetAsync(address, Token)).StatusCode);

            // On the pages themselves: a refresh refused on the page of a server MinerBot isn't in, and Announce now refused
            // on armor that isn't approved, each looked for on another server's or armor's page
            await StatusSteps.ServersAsync(db);
            await db.AddAsync(ArmorSteps.Submission(1), ArmorSteps.Submission(2, name: "Test Armor 2"));

            foreach (var (refused, elsewhere) in new[]
            {
                ($"/admin/servers/{StatusSteps.SettingsOnlyId}", $"/admin/servers/{StatusSteps.ServerId}"),
                ("/admin/armor/1", "/admin/armor/2")
            })
            {
                var token = SignInSteps.FormToken(await owner.Client.GetStringAsync(refused, Token));
                var handler = refused.StartsWith("/admin/servers/", StringComparison.Ordinal) ? "Refresh" : "Announce";
                var notice = await RequestSteps.NoticeOfAsync(await SignInSteps.PostAsync(owner, $"{refused}?handler={handler}", token, origin: DashboardFactory.PublicOrigin,
                    fields: [new("formkey", RequestFields.NewKey())]));
                var response = await owner.Client.GetAsync($"{elsewhere}?notice={RequestSteps.NoticeId(notice)}", Token);

                await AssertGoneAsync($"{refused}'s notice at {elsewhere}", response);
                Assert.False(RequestSteps.TouchesNotice(response), $"{elsewhere} wrote the notice's cookie");
                Assert.Equal(HttpStatusCode.Conflict, (await owner.Client.GetAsync(notice, Token)).StatusCode);
            }

            // A server's page and another's, by their paths alone
            var notices = new FormNotices(new EphemeralDataProtectionProvider(), new FakeClock());
            var key = SessionKeys.New();
            var keeper = NoticePage.At("/admin/servers/1", key);
            var kept = Assert.IsType<RedirectResult>(notices.Keep(keeper, new FormNotice { Form = "TestMessage", Status = 409, Words = "Not now." }));
            var serverId = RequestSteps.NoticeId(kept.Url);
            string[] forms = ["TestMessage", "Refresh"];

            Assert.Equal("/admin/servers/1?notice=" + serverId, kept.Url);

            foreach (var (path, pageForms) in new[] { ("/admin/servers/2", forms), ("/admin/servers/1", (string[])["Refresh"]) })
            {
                var page = NoticePage.At(path, key, keeper.NoticeValue);
                Assert.Null(notices.Find(page, serverId, pageForms));
                Assert.True(page.Gone, $"{path} doesn't say the notice has gone");
                Assert.False(page.TouchesNotice, $"{path} wrote the notice's cookie");
            }

            var own = NoticePage.At("/Admin/Servers/1", key, keeper.NoticeValue);
            Assert.Equal("Not now.", notices.Find(own, serverId, forms)?.Words);
            Assert.False(own.Gone || own.TouchesNotice);
        }

        // A notice lasts 30 minutes on the dashboard's clock, which the session's own use keeps going: past that, the page as
        // it is now, with the line that says it has gone, and the cookie deleted. A browser drops the cookie at its own 30
        // minutes, so it most often loads the address without one: the same page and line, and nothing written. Fails if Find
        // doesn't check the notice's age, or says nothing has gone when the cookie is.
        [Fact]
        public async Task ANoticeExpires()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.168");
            var address = await RefusedPauseAsync(owner);

            factory.Clock.Advance(TimeSpan.FromMinutes(29));
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.Client.GetAsync(address, Token)).StatusCode);

            factory.Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
            var expired = await owner.Client.GetAsync(address, Token);

            await AssertGoneAsync("a notice past its 30 minutes", expired);
            Assert.True(RequestSteps.DeletesNotice(expired));

            // The test's cookie jar keeps real time, so the cookie is still sent above; the dashboard deleted it, and the
            // address is loaded again without it, as a browser loads it once the cookie's Max-Age has passed
            Assert.Null(owner.Cookie(DashboardAuth.NoticeCookie));
            var dropped = await owner.Client.GetAsync(address, Token);

            await AssertGoneAsync("a notice address without its cookie", dropped);
            Assert.False(RequestSteps.TouchesNotice(dropped), "A notice address without its cookie wrote the cookie");
        }

        // The rule itself, failing closed as FreshSignIn does: shown until just before 30 minutes, never at 30, and never when
        // it's dated ahead of the clock, as after the clock is set back. Each that fails is deleted, and says it has gone.
        // Fails if either end of the check is gone.
        [Fact]
        public void ANoticeFailsClosedOnItsTime()
        {
            var protection = new EphemeralDataProtectionProvider();
            var clock = new FakeClock();
            var notices = new FormNotices(protection, clock);
            var key = SessionKeys.New();

            (string Id, string Cookie) Keep(FormNotices by)
            {
                var keeper = NoticePage.At("/admin/bot/controls", key);
                var address = Assert.IsType<RedirectResult>(by.Keep(keeper, new FormNotice { Form = ControlWords.Pause, Status = 400, Words = NotConfirmed })).Url;
                return (RequestSteps.NoticeId(address), keeper.NoticeValue!);
            }

            NoticePage Look(FormNotices by, (string Id, string Cookie) notice, out FormNotice? found)
            {
                var page = NoticePage.At("/admin/bot/controls", key, notice.Cookie);
                found = by.Find(page, notice.Id, ControlsModelForms);
                return page;
            }

            var kept = Keep(notices);
            clock.Advance(FormNotices.Lifetime - TimeSpan.FromTicks(1));
            var within = Look(notices, kept, out var shown);
            Assert.Equal(NotConfirmed, shown?.Words);
            Assert.False(within.Gone || within.TouchesNotice);

            clock.Advance(TimeSpan.FromTicks(1));
            var expired = Look(notices, kept, out var none);
            Assert.Null(none);
            Assert.True(expired.Gone && expired.DeletesNotice);

            // Kept on a clock 30 minutes ahead of the one that looks for it
            var ahead = Keep(notices);
            var behind = Look(new FormNotices(protection, new FakeClock()), ahead, out var early);
            Assert.Null(early);
            Assert.True(behind.Gone && behind.DeletesNotice);
        }

        static readonly string[] ControlsModelForms = [.. ControlsModel.Handlers.Keys];

        // The notice is protected with Data Protection's persisted keys, so the dashboard restarting in between, or the
        // deploy's swap, still draws it: two factories share a keys folder, and the browser's cookies go from one to the
        // other. Fails if the notice is kept in memory, or under keys that don't last.
        [Fact]
        public async Task ANoticeSurvivesARestart()
        {
            var db = await fixture.ReadyAsync();
            var keys = DashboardFactory.NewKeysFolder();

            try
            {
                string address;
                CookieCollection cookies;

                await using (var before = new DashboardFactory(db, new DashboardFactoryOptions { KeysFolder = keys }))
                {
                    var owner = await SignedInAsync(before, TestUsers.Owner, "198.51.100.169");
                    address = await RefusedPauseAsync(owner, "Kept across a restart");
                    cookies = owner.Jar.GetCookies(DashboardFactory.PublicAddress);
                }

                await using var after = new DashboardFactory(db, new DashboardFactoryOptions { KeysFolder = keys });
                var browser = after.Visitor("198.51.100.169");

                foreach (Cookie cookie in cookies)
                    browser.Jar.Add(DashboardFactory.PublicAddress, new Cookie(cookie.Name, cookie.Value) { Secure = true });

                var answer = await browser.Client.GetAsync(address, Token);
                var html = await HtmlAsync(answer);

                Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
                Assert.Contains(NotConfirmed, Text(html));
                Assert.Equal("Kept across a restart", BlacklistSteps.BoxValue(html, "reason"));
            }
            finally
            {
                DashboardFactory.DeleteFolder(keys);
            }
        }

        // A sign-out deletes the notice's cookie, with the attributes it was set with, so no later session finds it. Fails
        // if SignOut leaves it.
        [Fact]
        public async Task SignOutForgetsTheNotice()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.170");
            await RefusedPauseAsync(owner);
            Assert.NotNull(owner.Cookie(DashboardAuth.NoticeCookie));

            var signedOut = await SignInSteps.SignOutAsync(owner);

            Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
            var deleted = Assert.Single(RequestSteps.SetCookies(signedOut), cookie => cookie.StartsWith(DashboardAuth.NoticeCookie + "=;", StringComparison.Ordinal));
            var attributes = deleted.Split(';').Skip(1).Select(part => part.Trim().ToLowerInvariant()).ToList();
            Assert.Contains("expires=thu, 01 jan 1970 00:00:00 gmt", attributes);
            Assert.Contains("path=/", attributes);
            Assert.Contains("secure", attributes);
            Assert.Null(owner.Cookie(DashboardAuth.NoticeCookie));
        }

        // The status box's and a request box's refreshes, which refresh.js sends while a page is open, never write the
        // notice's cookie, and the notice still shows after them. Fails if a refresh endpoint touches it.
        [Fact]
        public async Task ARefreshNeverTouchesTheNotice()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.171");
            var alert = Assert.Single(await db.RequestsAsync()).id;
            var address = await RefusedPauseAsync(owner);

            foreach (var refresh in new[] { StatusBoxes.Address, $"/admin/requests/{alert}?handler=result" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, refresh);
                request.Headers.Add(BackgroundRefresh.Header, BackgroundRefresh.Value);
                var response = await owner.Client.SendAsync(request, Token);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.False(RequestSteps.TouchesNotice(response), $"{refresh} wrote the notice's cookie");
            }

            var answer = await owner.Client.GetAsync(address, Token);
            Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
            Assert.Contains(NotConfirmed, Text(await HtmlAsync(answer)));
        }

        // There's one notice per browser, so a refusal in another tab replaces it: the first address then gets the page as it
        // is now, with the line that says the notice has gone, and leaves the cookie to the newer one, which still shows
        [Fact]
        public async Task ANewerPressReplacesTheNotice()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.172");
            var page = await RequestSteps.ControlsAsync(owner);

            var first = await RequestSteps.NoticeOfAsync(await RequestSteps.PostControlAsync(owner, page, ControlWords.Pause, ("reason", "First"), ("confirm", "paws")));
            var second = await RequestSteps.NoticeOfAsync(await RequestSteps.PostControlAsync(owner, page, ControlWords.Restart, ("confirm", "restar")));

            var replaced = await owner.Client.GetAsync(first, Token);
            await AssertGoneAsync("a notice a newer one replaced", replaced);
            Assert.False(RequestSteps.TouchesNotice(replaced));

            var newer = await owner.Client.GetAsync(second, Token);
            Assert.Equal(HttpStatusCode.BadRequest, newer.StatusCode);
            Assert.Contains("Type restart in the box to confirm. Nothing was sent.", Text(await HtmlAsync(newer)));
        }

        // While MinerBot's database stays down after a save it didn't confirm, the notice's GET can't read its session, so
        // the error page says it can't tell whether it was saved, at 503, never that nothing was changed. A notice that isn't
        // unsure, one from another session, a broken cookie, an ID that isn't one, another notice's ID at the unsure one's
        // address and the unsure notice's ID at another address each get the usual page that says nothing was changed, and
        // so does the unsure notice past its 30 minutes. A failure that isn't the database's, on the unsure notice's own GET,
        // gets the page for what it is. None of these answers writes the notice's cookie. Fails if IsUnsure answers wrongly
        // either way, or the error page asks it for any failure.
        [Fact]
        public async Task TheUnsureNoticeWhileTheDatabaseIsDownSaysNotSure()
        {
            var db = await fixture.ReadyAsync();
            SwitchedDatabase? database = null;
            var failing = false;
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services =>
                {
                    services.RemoveAll<ISQLConnectionFactory>();
                    services.AddSingleton<ISQLConnectionFactory>(provider =>
                        database = new SwitchedDatabase(new SQLConnectionFactory(provider.GetRequiredService<IDictionary<DBConnectionName, string>>())));
                    services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider =>
                        new UnconfirmedQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider), () => database!.Down = true)));
                    services.Replace(ServiceDescriptor.Transient<IBotRequestReads>(provider =>
                        new FailingLatestReads(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider), () => failing)));
                }
            });
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.173");
            var refusedBrowser = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.174");
            var other = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.175");

            var refused = await RefusedPauseAsync(refusedBrowser);
            var unsure = await RequestSteps.NoticeOfAsync(await RequestSteps.PostControlAsync(owner, await RequestSteps.ControlsAsync(owner), ControlWords.Pause,
                ("reason", "Testing the controls"), ("confirm", "pause")));
            Assert.True(database!.Down, "The database came back before the notice's GET");

            var notSure = await owner.Client.GetAsync(unsure, Token);
            var notSureHtml = WebUtility.HtmlDecode(await HtmlAsync(notSure));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, notSure.StatusCode);
            Assert.Contains($"<h1>{ErrorResponses.UnsureTitle}</h1>", notSureHtml);
            Assert.Contains(ErrorResponses.UnsureUnavailable, notSureHtml);
            Assert.DoesNotContain(ErrorResponses.Unavailable, notSureHtml);
            Assert.Empty(RequestSteps.SetCookies(notSure));

            other.Jar.Add(DashboardFactory.PublicAddress, new Cookie(DashboardAuth.NoticeCookie, owner.Cookie(DashboardAuth.NoticeCookie)) { Secure = true });
            var broken = factory.Visitor("198.51.100.176");
            broken.Jar.Add(DashboardFactory.PublicAddress, new Cookie(DashboardAuth.SessionCookie, owner.SessionKey) { Secure = true });
            var cookie = owner.Cookie(DashboardAuth.NoticeCookie)!;
            broken.Jar.Add(DashboardFactory.PublicAddress, new Cookie(DashboardAuth.NoticeCookie, cookie[..20] + (cookie[20] == 'A' ? 'B' : 'A') + cookie[21..]) { Secure = true });

            foreach (var (what, browser, address) in new[]
            {
                ("a notice that isn't unsure", refusedBrowser, refused),
                ("another session's unsure notice", other, unsure),
                ("a broken notice", broken, unsure),
                ("an ID that isn't one", owner, "/admin/bot/controls?notice=" + RequestSteps.NoticeId(unsure)[..31]),
                ("another notice's ID at the unsure one's address", owner, "/admin/bot/controls?notice=" + RequestFields.NewKey()),
                ("the unsure notice's ID at another address", owner, "/admin?notice=" + RequestSteps.NoticeId(unsure))
            })
            {
                var response = await browser.Client.GetAsync(address, Token);
                var html = WebUtility.HtmlDecode(await HtmlAsync(response));

                Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable, $"{what} answered {response.StatusCode}");
                Assert.True(html.Contains(ErrorResponses.Unavailable, StringComparison.Ordinal), $"{what} doesn't say nothing was changed");
                Assert.DoesNotContain(ErrorResponses.UnsureTitle, html);
                Assert.Empty(RequestSteps.SetCookies(response));
            }

            // Once the database answers, the same address draws the notice
            database.Down = false;
            var back = await owner.Client.GetAsync(unsure, Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, back.StatusCode);
            Assert.Contains(RequestWords.Unsure, Text(await HtmlAsync(back)));

            // A fault of the page's own on that GET, with the database answering, is the page that says something went wrong,
            // never that the database still can't be reached
            failing = true;
            var faulted = await owner.Client.GetAsync(unsure, Token);
            var faultedHtml = WebUtility.HtmlDecode(await HtmlAsync(faulted));
            failing = false;

            Assert.Equal(HttpStatusCode.InternalServerError, faulted.StatusCode);
            Assert.Contains("<h1>Something went wrong</h1>", faultedHtml);
            Assert.Contains(ErrorResponses.Failed, faultedHtml);
            Assert.DoesNotContain(ErrorResponses.UnsureTitle, faultedHtml);
            Assert.False(RequestSteps.TouchesNotice(faulted), "A fault on the unsure notice's GET wrote the notice's cookie");

            // The database down again once the notice is 30 minutes old, as only a cookie kept past its Max-Age is sent then
            database.Down = true;
            factory.Clock.Advance(FormNotices.Lifetime);
            var late = await owner.Client.GetAsync(unsure, Token);
            var lateHtml = WebUtility.HtmlDecode(await HtmlAsync(late));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, late.StatusCode);
            Assert.Contains(ErrorResponses.Unavailable, lateHtml);
            Assert.DoesNotContain(ErrorResponses.UnsureTitle, lateHtml);
            Assert.Empty(RequestSteps.SetCookies(late));
        }

        // The real reads, whose read of each control's last use fails with something that isn't the database's while the test
        // says so, as a fault of the page's own would
        sealed class FailingLatestReads(BotRequestQueue real, Func<bool> failing) : IBotRequestReads
        {
            public Task<IReadOnlyList<RequestPageRow>> ListLatest(IReadOnlyCollection<string> kinds, ulong? guildId = null) =>
                failing() ? throw new InvalidOperationException("A fault of the page's own, for a test") : real.ListLatest(kinds, guildId);

            public Task<long> CountAlertsSince(DateTime since) => real.CountAlertsSince(since);
            public Task<RequestPageRow?> GetRequestView(ulong id) => real.GetRequestView(id);
            public Task<RequestPageRow?> LatestDm() => real.LatestDm();
            public Task<RequestPageRow?> LatestRefusedAlert() => real.LatestRefusedAlert();
            public Task<IReadOnlyList<AskedRow>> ListAskedBy(ulong actorId, DateTime since) => real.ListAskedBy(actorId, since);
            public Task<IReadOnlyList<ClaimRow>> ListClaimsSince(DateTime since) => real.ListClaimsSince(since);
            public Task<IReadOnlyList<WaitingRow>> ListWaiting(DateTime now) => real.ListWaiting(now);
        }
    }
}
