using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Servers;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // A server's switches on its page: the Owner saves them, in one transaction with the audit row, while MinerBot is in the
    // server, from a form that applies only to the switches it was drawn with, at most 30 times an hour; an Admin sees them
    // read-only and an Admin's POST is recorded and refused before the handler; a visitor is sent to sign in, and a forged
    // POST is refused. Every answer is a redirect.
    [Collection("Database")]
    public partial class SwitchesFormTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        // A: in, with a channel for each kind. C: left. E: settings only.
        const ulong A = 1000000000000001001, C = 1000000000000001003, E = 1000000000000001005;

        static readonly DateTime At = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

        static string Page(ulong id) => "/admin/servers/" + id.ToString(CultureInfo.InvariantCulture);

        // A page's text without its markup, so a sentence reads as one string, a name's full stop included
        static string Text(string html) => Regex.Replace(Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(html), "<[^>]+>", " "), "\\s+", " "), " ([.,:])(?= |$)", "$1");

        static async Task SeedAsync(MariaDbFixture db)
        {
            var guilds = db.BotGuilds();
            await guilds.SaveGuild(StatusSteps.Server(A, "Switch Server"), [StatusSteps.Channel(101, "armor", 0), StatusSteps.Channel(102, "maps", 1), StatusSteps.Channel(103, "lobbies", 2)],
                At, StatusSteps.RunId, 1);
            await guilds.SaveGuild(StatusSteps.Server(C, "Gone Server"), [StatusSteps.Channel(301, "general", 0)], At, StatusSteps.RunId, 2);
            await guilds.LeaveGuild(C, At.AddHours(1), StatusSteps.RunId, 3, null);

            var website = db.Guilds();
            await website.SaveGuild(new Guild { guild_id = A, armor_channel_id = 101, map_channel_id = 102, lobby_channel_id = 103 });
            await website.SaveGuild(new Guild { guild_id = C, armor_channel_id = 301 });
            await website.SaveGuild(new Guild { guild_id = E, armor_channel_id = 501 });
        }

        static async Task<TestBrowser> SignedInAsync(DashboardFactory factory, TestUser user, string address)
        {
            var browser = factory.Visitor(address);
            await SignInSteps.SignedInAsync(factory, browser, user);
            return browser;
        }

        // The switches' form as the page drew it: its version, and which boxes it ticked
        sealed record SwitchForm(string Html, string Token, string Version, bool Armor, bool Maps, bool Lobbies);

        static async Task<SwitchForm> OpenAsync(TestBrowser browser, ulong server)
        {
            var html = await browser.Client.GetStringAsync(Page(server), Token);
            var form = Regex.Match(html, $"<form method=\"post\" action=\"{Regex.Escape(Page(server))}\\?handler=Switches\">(.*?)</form>", RegexOptions.Singleline);
            Assert.True(form.Success, "The page has no switches form");

            bool Ticked(string name) => Regex.IsMatch(form.Groups[1].Value, $"<input type=\"checkbox\" id=\"switch-{name}\" name=\"{name}\" value=\"on\" checked=\"checked\" />");

            return new SwitchForm(html, SignInSteps.FormToken(html), Regex.Match(form.Groups[1].Value, "name=\"version\" value=\"([0-9]+)\"").Groups[1].Value,
                Ticked("armor"), Ticked("maps"), Ticked("lobbies"));
        }

        // The form sent as a browser sends it: a ticked box as on, an unticked one not at all
        static Task<HttpResponseMessage> SaveAsync(TestBrowser browser, ulong server, SwitchForm form, bool armor, bool maps, bool lobbies, string? version = null)
        {
            var fields = new List<KeyValuePair<string, string>> { new("version", version ?? form.Version) };

            if (armor)
                fields.Add(new("armor", "on"));

            if (maps)
                fields.Add(new("maps", "on"));

            if (lobbies)
                fields.Add(new("lobbies", "on"));

            return SignInSteps.PostAsync(browser, Page(server) + "?handler=Switches", form.Token, origin: DashboardFactory.PublicOrigin, fields: fields);
        }

        static Task<List<GuildSwitches>> SwitchesAsync(MariaDbFixture db) =>
            db.RootQueryAsync<GuildSwitches>($"SELECT * FROM {MariaDbFixture.SwitchTable} ORDER BY guild_id");

        static async Task<List<AuditEntry>> SwitchAuditAsync(MariaDbFixture db) =>
            [.. (await db.AuditAsync()).Where(entry => entry.action == AuditWords.SwitchesChange)];

        // The Owner turns armor posts off: the row and its audit row commit together, the redirect carries the save's version,
        // and the page says so, shows armor as switched off, and draws the form from the new version. Turning it on again keeps
        // the row with the next version. A failing audit row takes the switches' change with it.
        [Fact]
        public async Task OwnerSavesAndTheAuditRowCommitsWithIt()
        {
            var db = await fixture.ReadyBotAsync();
            await SeedAsync(db);

            await using (var factory = new DashboardFactory(db))
            {
                var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.210");
                var form = await OpenAsync(owner, A);
                Assert.Equal(("0", true, true, true), (form.Version, form.Armor, form.Maps, form.Lobbies));
                Assert.Contains(SwitchWords.NoneSaved, Text(form.Html));
                PageTests.AssertAccessible("a server's page with its switches", form.Html);

                var response = await SaveAsync(owner, A, form, armor: false, maps: true, lobbies: true);

                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                Assert.Equal(Page(A) + "?done=switches&saved=1#switches", SignInSteps.PathOf(response.Headers.Location));

                var saved = Assert.Single(await SwitchesAsync(db));
                Assert.Equal((A, false, true, true, 1u, TestUsers.Owner.Id, AuditRole.Owner), (saved.guild_id, saved.armor_on, saved.maps_on, saved.lobbies_on, saved.version,
                    saved.changed_by, saved.changed_by_role));

                var audit = Assert.Single(await SwitchAuditAsync(db));
                Assert.Equal((AuditOutcome.Ok, (ulong?)A, AuditRole.Owner, (ulong?)TestUsers.Owner.Id, "{\"armor_on\":true}", "{\"armor_on\":false}", "198.51.100.210"),
                    (audit.outcome, audit.guild_id, audit.actor_role, audit.actor_id, audit.before_json, audit.after_json, audit.ip));
                Assert.Equal(saved.changed_at, audit.at);

                var done = await owner.Client.GetStringAsync(SignInSteps.PathOf(response.Headers.Location), Token);
                var text = Text(done);
                Assert.Contains(SwitchWords.Saved, text);
                Assert.Contains("Armor armor 101 Switched off " + SwitchWords.SwitchedOffLine, text);

                // Once, under the switches' heading, where the redirect's fragment brings the page, so it's in view there
                var heading = done.IndexOf($"<h3 id=\"{SwitchWords.Anchor}\">", StringComparison.Ordinal);
                Assert.True(heading >= 0, "The page has no switches' heading");
                Assert.Equal(1, Regex.Count(done, Regex.Escape(SwitchWords.Saved)));
                Assert.True(done.IndexOf(SwitchWords.Saved, StringComparison.Ordinal) > heading, "The save's notice is drawn above the switches' heading, out of view");
                Assert.Contains($"Last changed {saved.changed_at:yyyy-MM-dd HH:mm} UTC by Test Owner.", text);
                Assert.Contains("Changed a server's switches", text);
                Assert.Contains("Before Armor posts on After Armor posts off", text);
                PageTests.AssertAccessible("a server's page after a save", done);

                var again = await OpenAsync(owner, A);
                Assert.Equal(("1", false, true, true), (again.Version, again.Armor, again.Maps, again.Lobbies));

                Assert.Equal(Page(A) + "?done=switches&saved=2#switches", SignInSteps.PathOf((await SaveAsync(owner, A, again, true, true, true)).Headers.Location));
                Assert.Equal((true, 2u), Assert.Single(await SwitchesAsync(db)) is var on ? (on.armor_on, on.version) : default);
                Assert.Equal(2, (await SwitchAuditAsync(db)).Count);
            }

            // The audit row fails inside the transaction, so the switches stay as they were
            await using var failing = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IAuditLogData>(provider =>
                    new FailingSwitchAudit(ActivatorUtilities.CreateInstance<AuditLogData>(provider))))
            });
            var owner2 = await SignedInAsync(failing, TestUsers.Owner, "198.51.100.211");
            var form2 = await OpenAsync(owner2, A);
            var failed = await SaveAsync(owner2, A, form2, armor: false, maps: false, lobbies: true);

            Assert.Equal(HttpStatusCode.SeeOther, failed.StatusCode);
            Assert.StartsWith("/error", SignInSteps.PathOf(failed.Headers.Location));
            Assert.Equal((true, true, 2u), Assert.Single(await SwitchesAsync(db)) is var kept ? (kept.armor_on, kept.maps_on, kept.version) : default);
            Assert.Equal(2, (await SwitchAuditAsync(db)).Count);
        }

        // An Admin sees the switches read-only, and an Admin's POST, with a good token, is refused before the handler: one
        // refused audit row under the server, /denied, and no switch saved
        [Fact]
        public async Task AdminsPostIsRefusedWithAnAuditRow()
        {
            var db = await fixture.ReadyBotAsync();
            await SeedAsync(db);
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.212");

            var html = await admin.Client.GetStringAsync(Page(A), Token);
            Assert.DoesNotContain("handler=Switches", html);
            Assert.Contains("Armor posts: on Map posts: on Lobby posts: on " + SwitchWords.OnlyOwner, Text(html));

            var response = await SignInSteps.PostAsync(admin, Page(A) + "?handler=Switches", SignInSteps.FormToken(html), origin: DashboardFactory.PublicOrigin,
                fields: [new("version", "0"), new("maps", "on"), new("lobbies", "on")]);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/denied", SignInSteps.PathOf(response.Headers.Location));
            Assert.Empty(await SwitchesAsync(db));

            var audit = Assert.Single(await SwitchAuditAsync(db));
            Assert.Equal((AuditOutcome.Refused, (ulong?)A, AuditRole.Admin, (ulong?)TestUsers.Admin.Id, SwitchWords.OnlyOwnerDetail, "198.51.100.212"),
                (audit.outcome, audit.guild_id, audit.actor_role, audit.actor_id, audit.detail, audit.ip));
            Assert.Null(audit.before_json);
            Assert.Null(audit.after_json);
        }

        // Without a session the POST goes to the sign-in, which says the session ended, and nothing is saved or recorded. A
        // POST without the page's token, with a made-up one, or from another site is refused before the gate, so even an
        // Admin's leaves no row. Fields the form never sends are a 400.
        [Fact]
        public async Task VisitorsAndForgedPostsAreRefused()
        {
            var db = await fixture.ReadyBotAsync();
            await SeedAsync(db);
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.213");
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.214");
            var form = await OpenAsync(owner, A);
            var path = Page(A) + "?handler=Switches";
            KeyValuePair<string, string>[] fields = [new("version", "0"), new("lobbies", "on")];

            var anonymous = await SignInSteps.PostAsync(factory.Visitor("198.51.100.215"), path, form.Token, origin: DashboardFactory.PublicOrigin, fields: fields);
            Assert.Equal(HttpStatusCode.SeeOther, anonymous.StatusCode);
            Assert.Equal("/signin?ended=1&returnUrl=" + Uri.EscapeDataString(Page(A)), SignInSteps.PathOf(anonymous.Headers.Location));

            Assert.Equal(HttpStatusCode.BadRequest, (await SignInSteps.PostAsync(owner, path, null, origin: DashboardFactory.PublicOrigin, fields: fields)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await SignInSteps.PostAsync(owner, path, "made-up", origin: DashboardFactory.PublicOrigin, fields: fields)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SignInSteps.PostAsync(owner, path, form.Token, origin: "https://murderminershub.com", fields: fields)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SignInSteps.PostAsync(owner, path, form.Token, fetchSite: "same-site", fields: fields)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await SignInSteps.PostAsync(admin, path, null, origin: DashboardFactory.PublicOrigin, fields: fields)).StatusCode);

            foreach (var made in new KeyValuePair<string, string>[][]
            {
                [new("version", "one")],
                [new("version", "-1")],
                [new("version", "99999999999")],
                [new("version", "0"), new("armor", "yes")],
                [new("version", "0"), new("lobbies", "off")]
            })
                Assert.Equal(HttpStatusCode.BadRequest, (await SignInSteps.PostAsync(owner, path, form.Token, origin: DashboardFactory.PublicOrigin, fields: made)).StatusCode);

            Assert.Empty(await SwitchesAsync(db));
            Assert.Empty(await SwitchAuditAsync(db));
        }

        // A form drawn before another save applies to nothing: only its refused audit row is written, with the switches as
        // they were, and the page answers 409 with the switches as they are, from the new version
        [Fact]
        public async Task StaleFormIsRefused()
        {
            var db = await fixture.ReadyBotAsync();
            await SeedAsync(db);
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.216");

            var drawn = await OpenAsync(owner, A);
            await PostSteps.SwitchesAsync(db, A, armor: true, maps: false, lobbies: true, At);

            var answer = await RequestSteps.AnsweredAsync(owner, await SaveAsync(owner, A, drawn, armor: false, maps: true, lobbies: true));
            var html = await answer.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
            Assert.Contains(SwitchWords.Stale, Text(html));
            Assert.Contains("name=\"version\" value=\"1\"", html);
            Assert.Equal((true, false, 1u), Assert.Single(await SwitchesAsync(db)) is var row ? (row.armor_on, row.maps_on, row.version) : default);

            var audit = Assert.Single(await SwitchAuditAsync(db));
            Assert.Equal((AuditOutcome.Refused, SwitchWords.StaleDetail, "{\"armor_on\":true,\"maps_on\":false}", (string?)null),
                (audit.outcome, audit.detail, audit.before_json, audit.after_json));

            // A form drawn with no row, after another's first save, is stale too
            await db.ClearAsync(MariaDbFixture.SwitchTable);
            var first = await OpenAsync(owner, A);
            await PostSteps.SwitchesAsync(db, A, armor: true, maps: true, lobbies: false, At);
            Assert.Equal(HttpStatusCode.Conflict, (await RequestSteps.AnsweredAsync(owner, await SaveAsync(owner, A, first, false, true, true))).StatusCode);
            Assert.True(Assert.Single(await SwitchesAsync(db)).armor_on);
        }

        // A commit the database didn't confirm: the notice says it can't tell at 503, and an unknown audit row is written
        // beside what landed. Saving the same form again is safe: it's then stale or changes nothing.
        [Fact]
        public async Task UnconfirmedSaveSaysItCantTell()
        {
            var db = await fixture.ReadyBotAsync();
            await SeedAsync(db);
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IGuildSwitchData>(provider => new LateSwitches(ActivatorUtilities.CreateInstance<GuildSwitchData>(provider))))
            });
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.217");
            var form = await OpenAsync(owner, A);

            var answer = await RequestSteps.AnsweredAsync(owner, await SaveAsync(owner, A, form, armor: true, maps: false, lobbies: true));
            var html = await answer.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, answer.StatusCode);
            Assert.Contains(SwitchWords.Unsure, Text(html));
            PageTests.AssertAccessible("a server's page after a save it couldn't confirm", html);
            Assert.False(Assert.Single(await SwitchesAsync(db)).maps_on);
            Assert.Equal([AuditOutcome.Ok, AuditOutcome.Unknown], (await SwitchAuditAsync(db)).Select(entry => entry.outcome));
            Assert.Equal(UnconfirmedDetail, (await SwitchAuditAsync(db))[1].detail);
        }

        const string UnconfirmedDetail = "The database didn't confirm the save, so it may or may not have happened";

        // Switches as the server has them already: nothing is written, not even an audit row, and the notice says so
        [Fact]
        public async Task NothingChangedSavesNothing()
        {
            var db = await fixture.ReadyBotAsync();
            await SeedAsync(db);
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.218");

            var answer = await RequestSteps.AnsweredAsync(owner, await SaveAsync(owner, A, await OpenAsync(owner, A), armor: true, maps: true, lobbies: true));

            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.Contains(SwitchWords.Unchanged, Text(await answer.Content.ReadAsStringAsync(Token)));
            Assert.Empty(await SwitchesAsync(db));
            Assert.Empty(await SwitchAuditAsync(db));
        }

        // The same form sent again once it was saved, as a double click, Back and Resubmit, or a try again after a save the
        // database didn't confirm sends it: nothing more is written, and the answer says the switches are already as chosen,
        // since an earlier try was saved or someone made the same change, not that nothing changed. A form drawn from the
        // switches as they are still says nothing changed.
        [Fact]
        public async Task SameFormSentAgainSaysItWasSaved()
        {
            var db = await fixture.ReadyBotAsync();
            await SeedAsync(db);
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.223");
            var form = await OpenAsync(owner, A);

            Assert.Equal(Page(A) + "?done=switches&saved=1#switches", SignInSteps.PathOf((await SaveAsync(owner, A, form, armor: false, maps: true, lobbies: true)).Headers.Location));

            var again = await RequestSteps.AnsweredAsync(owner, await SaveAsync(owner, A, form, armor: false, maps: true, lobbies: true));
            var text = Text(await again.Content.ReadAsStringAsync(Token));

            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Contains(SwitchWords.Already, text);
            Assert.DoesNotContain(SwitchWords.Unchanged, text);
            Assert.Equal((false, 1u), Assert.Single(await SwitchesAsync(db)) is var row ? (row.armor_on, row.version) : default);
            Assert.Single(await SwitchAuditAsync(db));

            var redrawn = await RequestSteps.AnsweredAsync(owner, await SaveAsync(owner, A, await OpenAsync(owner, A), armor: false, maps: true, lobbies: true));
            Assert.Contains(SwitchWords.Unchanged, Text(await redrawn.Content.ReadAsStringAsync(Token)));
        }

        // A server MinerBot left, or was never recorded in, has no form for anyone, and a POST for it is refused at 409 with
        // nothing written
        [Fact]
        public async Task NoFormWhileMinerBotIsntInTheServer()
        {
            var db = await fixture.ReadyBotAsync();
            await SeedAsync(db);
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.219");
            var token = (await OpenAsync(owner, A)).Token;

            var left = await owner.Client.GetStringAsync(Page(C), Token);
            Assert.DoesNotContain("handler=Switches", left);
            Assert.Contains(SwitchWords.NotIn, Text(left));
            Assert.DoesNotContain("handler=Switches", await owner.Client.GetStringAsync(Page(E), Token));

            foreach (var server in new[] { C, E })
            {
                var answer = await RequestSteps.AnsweredAsync(owner, await SignInSteps.PostAsync(owner, Page(server) + "?handler=Switches", token,
                    origin: DashboardFactory.PublicOrigin, fields: [new("version", "0"), new("maps", "on")]));

                Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
                Assert.Contains(SwitchWords.NotInNow, Text(await answer.Content.ReadAsStringAsync(Token)));
            }

            Assert.Equal(HttpStatusCode.NotFound, (await SignInSteps.PostAsync(owner, Page(12345) + "?handler=Switches", token, origin: DashboardFactory.PublicOrigin,
                fields: [new("version", "0")])).StatusCode);
            Assert.Empty(await SwitchesAsync(db));
            Assert.Empty(await SwitchAuditAsync(db));
        }

        // 30 saves an hour, counted from the person's own rows in the audit log, those not sure to have been saved included and
        // their refusals not: the 31st is refused at 429 with Retry-After, its refused row and the time to try again, and once
        // the oldest leaves the hour, a save goes through
        [Fact]
        public async Task PersonsLimitIsThirtyAnHour()
        {
            var db = await fixture.ReadyBotAsync();
            await SeedAsync(db);
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.220");

            AuditEntry Row(int minutesAgo, string outcome, ulong actor) => new()
            {
                at = now.AddMinutes(-minutesAgo),
                actor_id = actor,
                actor_name = "Test Owner",
                actor_role = AuditRole.Owner,
                action = AuditWords.SwitchesChange,
                outcome = outcome,
                guild_id = A
            };

            // 29 counted in the hour, 40 refusals, an older save and someone else's: one more save is allowed
            for (var minute = 1; minute <= 29; minute++)
                await db.Audit().AddAudit(Row(minute + 30, minute % 2 == 0 ? AuditOutcome.Unknown : AuditOutcome.Ok, TestUsers.Owner.Id));

            for (var minute = 1; minute <= 40; minute++)
                await db.Audit().AddAudit(Row(minute, AuditOutcome.Refused, TestUsers.Owner.Id));

            await db.Audit().AddAudit(Row(61, AuditOutcome.Ok, TestUsers.Owner.Id));
            await db.Audit().AddAudit(Row(5, AuditOutcome.Ok, TestUsers.Admin.Id));

            var form = await OpenAsync(owner, A);
            Assert.Equal(HttpStatusCode.Redirect, (await SaveAsync(owner, A, form, armor: false, maps: true, lobbies: true)).StatusCode);

            // The 31st: the oldest of the 30 is the one 59 minutes ago, so the next save is allowed a minute from now
            var answer = await RequestSteps.AnsweredAsync(owner, await SaveAsync(owner, A, await OpenAsync(owner, A), armor: true, maps: true, lobbies: true));
            var html = await answer.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.TooManyRequests, answer.StatusCode);
            Assert.Equal("60", answer.Headers.RetryAfter?.ToString());
            Assert.Contains(SwitchWords.Limit(now.AddMinutes(1)), Text(html));
            Assert.False(Assert.Single(await SwitchesAsync(db)).armor_on);

            var refused = (await SwitchAuditAsync(db)).Last();
            Assert.Equal((AuditOutcome.Refused, SwitchWords.LimitDetail, (ulong?)A), (refused.outcome, refused.detail, refused.guild_id));

            factory.Clock.Advance(TimeSpan.FromSeconds(61));
            Assert.Equal(HttpStatusCode.Redirect, (await SaveAsync(owner, A, await OpenAsync(owner, A), armor: true, maps: true, lobbies: true)).StatusCode);
            Assert.True(Assert.Single(await SwitchesAsync(db)).armor_on);
        }

        // ?done= says the switches were saved only while they're the version the save made: not after another save, not for
        // a version made up, and not where there's no row
        [Fact]
        public async Task DoneWordShowsOnlyWhileTrue()
        {
            var db = await fixture.ReadyBotAsync();
            await SeedAsync(db);
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.221");

            async Task<bool> SaysSavedAsync(string query) => Text(await owner.Client.GetStringAsync(Page(A) + query, Token)).Contains(SwitchWords.Saved, StringComparison.Ordinal);

            Assert.False(await SaysSavedAsync("?done=switches&saved=0"));
            Assert.False(await SaysSavedAsync("?done=switches&saved=1"));

            await PostSteps.SwitchesAsync(db, A, armor: false, maps: true, lobbies: true, At);
            Assert.True(await SaysSavedAsync("?done=switches&saved=1"));
            Assert.False(await SaysSavedAsync("?done=switches&saved=7"));
            Assert.False(await SaysSavedAsync("?done=switches&saved=one"));
            Assert.False(await SaysSavedAsync("?done=saved&saved=1"));
            Assert.False(await SaysSavedAsync("?done=switches"));

            await PostSteps.SwitchesAsync(db, A, armor: true, maps: true, lobbies: true, At);
            Assert.False(await SaysSavedAsync("?done=switches&saved=1"));
            Assert.True(await SaysSavedAsync("?done=switches&saved=2"));
        }

        // The dashboard's own login, with the rights deploy gives it, saves the switches and its audit row, and reads them back
        [Fact]
        public async Task TheRealLoginSaves()
        {
            var db = await fixture.ReadyBotAsync();
            await SeedAsync(db);

            await DashboardLogin.AsDashboardAsync(db, async password =>
            {
                await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
                {
                    SiteConnection = DashboardLogin.ConnectionAs(db, password, MariaDbFixture.SiteDatabase),
                    BotConnection = DashboardLogin.ConnectionAs(db, password, MariaDbFixture.BotDatabase)
                });
                var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.222");

                var response = await SaveAsync(owner, A, await OpenAsync(owner, A), armor: true, maps: true, lobbies: false);
                Assert.Equal(Page(A) + "?done=switches&saved=1#switches", SignInSteps.PathOf(response.Headers.Location));

                var again = await SaveAsync(owner, A, await OpenAsync(owner, A), armor: true, maps: true, lobbies: true);
                Assert.Equal(Page(A) + "?done=switches&saved=2#switches", SignInSteps.PathOf(again.Headers.Location));
                Assert.Contains(SwitchWords.Saved, Text(await owner.Client.GetStringAsync(SignInSteps.PathOf(again.Headers.Location), Token)));
            });

            Assert.Equal((true, 2u), Assert.Single(await SwitchesAsync(db)) is var row ? (row.lobbies_on, row.version) : default);
            Assert.Equal(2, (await SwitchAuditAsync(db)).Count(entry => entry.outcome == AuditOutcome.Ok));
        }

        // The audit log, whose switches rows fail inside the save's transaction
        sealed class FailingSwitchAudit(AuditLogData real) : IAuditLogData
        {
            public Task AddAudit(AuditEntry entry, DataTransaction? transaction = null) => entry.action == AuditWords.SwitchesChange && transaction != null
                ? Task.FromException(new TimeoutException("The database didn't answer within 15 seconds"))
                : real.AddAudit(entry, transaction);

            public Task<IReadOnlyList<AuditEntry>> LatestChanges(string targetType, IReadOnlyCollection<string> targetIds) => real.LatestChanges(targetType, targetIds);
            public Task<Page<AuditEntry>> ListAudit(AuditQuery query) => real.ListAudit(query);
            public Task<IReadOnlyList<DateTime>> ListActionTimes(ulong actorId, string action, IReadOnlyCollection<string> outcomes, DateTime from, int take) =>
                real.ListActionTimes(actorId, action, outcomes, from, take);
        }
    }
}
