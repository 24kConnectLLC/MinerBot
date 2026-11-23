using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using DataAccessLibrary;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MinerBot.Dashboard.Armor;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Pages.Admin;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Status;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Requests;
using MySql.Data.MySqlClient;

namespace MinerBot.Dashboard.Tests
{
    // The bot's controls page at /admin/bot/controls: the Owner's every control and an Admin's two, a gate that refuses an
    // Owner control before its handler, each form sending its own kind through the sender, a form sent twice answered with
    // its first request, the bot's limit met before writing, and the settings shown by the bot's own rule
    [Collection("Database")]
    public partial class ControlsPageTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        public static TheoryData<string> AllHandlers => [.. ControlsModel.Handlers.Keys];

        // The handlers whose kind only the Owner can ask for: every one but the cache clear and the notification job
        public static TheoryData<string> OwnerHandlers => [.. ControlsModel.Handlers.Where(entry => RequestKinds.Find(entry.Value)!.OwnerOnly).Select(entry => entry.Key)];

        static async Task<TestBrowser> SignedInAsync(DashboardFactory factory, TestUser user, string address)
        {
            var browser = factory.Visitor(address);
            await SignInSteps.SignedInAsync(factory, browser, user);
            return browser;
        }

        // A form's other fields as a person fills them in: a pause's reason and word, a status text, and the word for the
        // other controls that need one
        static (string Name, string Value)[] Filled(string handler) => handler switch
        {
            ControlWords.Pause => [("reason", "Testing the controls"), ("confirm", "pause")],
            ControlWords.SetStatus => [("text", "Back soon")],
            ControlWords.ReconnectSteam => [("confirm", "reconnect")],
            ControlWords.RenewToken => [("confirm", "renew")],
            ControlWords.Restart => [("confirm", "restart")],
            _ => []
        };

        // A page's text without its markup, so a sentence reads as one string, a link's full stop included
        static string Text(string html) => Regex.Replace(Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(html), "<[^>]+>", " "), "\\s+", " "), " ([.,:])(?= |$)", "$1");

        static async Task<string> TextAsync(HttpResponseMessage response) => Text(await response.Content.ReadAsStringAsync(Token));

        // One control's part of the page, from its heading to the next control or the end of its section
        static string Control(string html, string label)
        {
            var match = Regex.Match(html, $"<h3>{Regex.Escape(label)}</h3>(.*?)(?=<div class=\"control\">|</section>)", RegexOptions.Singleline);
            Assert.True(match.Success, $"The page has no control called {label}");
            return Text(match.Groups[1].Value);
        }

        static void SentTo(HttpResponseMessage response, ulong id)
        {
            Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"The form answered {response.StatusCode}");
            Assert.Equal($"/admin/bot/controls?request={id}#result", SignInSteps.PathOf(response.Headers.Location));
        }

        // A fresh Owner's page has a form for every control: Pause while posts aren't paused, Resume while they are, each of
        // the four that needs a word with its box, and the lines for the pause and the status
        [Fact]
        public async Task OwnerSeesEveryControl()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.101");

            var page = await RequestSteps.ControlsAsync(owner);

            string[] everyOther = [ControlWords.SetStatus, ControlWords.DefaultStatus, ControlWords.RunNotifications, ControlWords.RunColors, ControlWords.ClearCaches,
                ControlWords.ReconnectSteam, ControlWords.RenewToken, ControlWords.Restart];
            Assert.Equal([ControlWords.Pause, .. everyOther], page.Handlers);
            Assert.Equal(["confirm-Pause", "confirm-ReconnectSteam", "confirm-RenewToken", "confirm-Restart"],
                Regex.Matches(page.Html, "<input type=\"text\" id=\"([^\"]+)\" name=\"confirm\"").Select(match => match.Groups[1].Value));
            Assert.Contains("Posts aren't paused.", page.Text);
            Assert.Contains("Discord shows the default status, Playing Murder Miners on my PC.", page.Text);
            Assert.Contains("Your sign-in is from", page.Text);
            Assert.DoesNotContain("fresh=1", page.Html);
            Assert.DoesNotContain(ControlWords.AdminLine, page.Text);
            PageTests.AssertAccessible("the Owner's controls page", page.Html);

            // Each section in the order the page gives them, with its anchor
            var sections = Regex.Matches(page.Html, "<h2 id=\"([a-z-]+)\">").Select(match => match.Groups[1].Value);
            Assert.Equal(["now", "sign-in", "posts", "status-text", "jobs", "caches", "steam", "restart"], sections);

            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var pause = await RequestSteps.AddAsync(db, factory, RequestKinds.PostsPause, now.AddMinutes(-1));
            await db.Requests().SavePause(true, "Testing the controls", now.AddMinutes(-1), TestUsers.Owner.Id, pause, now.AddMinutes(-1));

            var paused = await RequestSteps.ControlsAsync(owner);
            Assert.Equal([ControlWords.Resume, .. everyOther], paused.Handlers);
            Assert.Equal(3, Regex.Count(paused.Html, "name=\"confirm\""));
            Assert.Contains($"Posts are paused, since {TimeWords.Utc(now.AddMinutes(-1))}, on request {pause}, by Test Owner. The reason given: Testing the controls.", Text(paused.Html));
            PageTests.AssertAccessible("the Owner's controls page while posts are paused", paused.Html);
        }

        // An Admin's page has exactly the cache clear and the notification job, the line that says the rest are the Owner's,
        // no box for a word and no way to sign in again
        [Fact]
        public async Task AdminSeesTwoControls()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.102");

            var page = await RequestSteps.ControlsAsync(admin);

            Assert.Equal([ControlWords.RunNotifications, ControlWords.ClearCaches], page.Handlers);
            Assert.Equal(2, Regex.Count(page.Html, "<form[^>]*\\?handler="));
            Assert.Contains(ControlWords.AdminLine, page.Text);
            Assert.DoesNotContain("name=\"confirm\"", page.Html);
            Assert.DoesNotContain("fresh=1", page.Html);
            Assert.Equal(["now", "jobs", "caches"], Regex.Matches(page.Html, "<h2 id=\"([a-z-]+)\">").Select(match => match.Groups[1].Value));
            PageTests.AssertAccessible("an Admin's controls page", page.Html);
        }

        // An Admin's POST to an Owner control, with a good token and key, is refused by the page's gate before its handler:
        // it ends at /denied, the app never makes a sender for it, nothing is sent, and one audit row records the try. Fails
        // if the gate leaves the role to the sender, which would refuse it too, but only once the handler had run.
        [Theory]
        [MemberData(nameof(OwnerHandlers))]
        public async Task AnAdminsPostToAnOwnerControlIsRefusedBeforeItsHandler(string handler)
        {
            var db = await fixture.ReadyAsync();
            var senders = 0;
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                // Every sender the app makes is counted: a handler that ran would make one
                Services = services => services.Replace(ServiceDescriptor.Transient<RequestSender>(provider =>
                {
                    Interlocked.Increment(ref senders);
                    return ActivatorUtilities.CreateInstance<RequestSender>(provider);
                }))
            });
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.103");
            var page = await RequestSteps.ControlsAsync(admin);

            var response = await RequestSteps.PostControlAsync(admin, handler, page.Token, RequestFields.NewKey(), Filled(handler));

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/denied", SignInSteps.PathOf(response.Headers.Location));
            Assert.Equal(0, Volatile.Read(ref senders));
            Assert.Empty(await db.SentAsync());

            var audit = Assert.Single(await db.AuditAsync(), entry => entry.outcome == AuditOutcome.Refused);
            Assert.Equal((ControlsModel.Handlers[handler], AuditRole.Admin, (ulong?)TestUsers.Admin.Id, "request", "Only the Owner can ask for this", "198.51.100.103"),
                (audit.action, audit.actor_role, audit.actor_id, audit.target_type!, audit.detail!, audit.ip!));
            Assert.Null(audit.target_id);
        }

        // A request no handler takes, a POST without one, with one the page doesn't have, or another method, is a 400 from the
        // gate, never the page drawn as though it were a GET, and sends nothing
        [Fact]
        public async Task ARequestNoHandlerTakesIsABadRequest()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.123");
            var page = await RequestSteps.ControlsAsync(owner);
            KeyValuePair<string, string>[] fields = [new("formkey", RequestFields.NewKey()), new("confirm", "restart")];

            foreach (var path in new[] { "/admin/bot/controls", "/admin/bot/controls?handler=Explode", "/admin/bot/controls?handler=Status" })
                Assert.Equal(HttpStatusCode.BadRequest, (await SignInSteps.PostAsync(owner, path, page.Token, origin: DashboardFactory.PublicOrigin, fields: fields)).StatusCode);

            var put = await SignInSteps.PostAsync(owner, "/admin/bot/controls?handler=Restart", page.Token, origin: DashboardFactory.PublicOrigin, method: HttpMethod.Put, fields: fields);
            Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
            Assert.Empty(await db.SentAsync());
        }

        // Every POST handler the page has is on the gate's map with a kind on the shared list, has its words, and every entry
        // has its handler. Fails for a handler added without an entry, which the gate refuses.
        [Fact]
        public void EveryPostHandlerIsOnTheMap()
        {
            var methods = typeof(ControlsModel).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(method => Regex.Match(method.Name, "^On(?<verb>Get|Post|Put|Delete|Patch|Head|Options)(?<handler>[A-Za-z]*?)(?:Async)?$"))
                .Where(match => match.Success)
                .ToList();

            Assert.Equal(["Get"], methods.Where(match => match.Groups["handler"].Value.Length == 0).Select(match => match.Groups["verb"].Value));
            Assert.All(methods.Where(match => match.Groups["handler"].Value.Length > 0), match => Assert.Equal("Post", match.Groups["verb"].Value));

            var posts = methods.Where(match => match.Groups["verb"].Value == "Post").Select(match => match.Groups["handler"].Value).Order(StringComparer.Ordinal);
            Assert.Equal(ControlsModel.Handlers.Keys.Order(StringComparer.Ordinal), posts);
            Assert.All(ControlsModel.Handlers.Values, word => Assert.NotNull(RequestKinds.Find(word)));
            Assert.All(ControlsModel.Handlers.Keys, handler => Assert.Equal(handler, ControlWords.Of(handler).Handler));
            Assert.Equal(ControlsModel.Handlers.Keys.Order(StringComparer.Ordinal), ControlWords.Controls.Select(control => control.Handler).Order(StringComparer.Ordinal));

            Assert.Equal([RequestKinds.BotRestart, RequestKinds.CachesClear, RequestKinds.JobColors, RequestKinds.JobNotifications, RequestKinds.PostsPause,
                RequestKinds.PostsResume, RequestKinds.StatusSet, RequestKinds.SteamReconnect, RequestKinds.SteamRenew], ControlsModel.Kinds.Order(StringComparer.Ordinal));
        }

        // Each form sends its own kind as the Owner asked for it, and leads to its box: the kind, the person, their role, the
        // reason without the white space around it, the text, nothing for the others, a signature the bot verifies, and one
        // audit row with the request and the address
        [Theory]
        [MemberData(nameof(AllHandlers))]
        public async Task EachControlSendsItsKind(string handler)
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.104");
            var page = await RequestSteps.ControlsAsync(owner);

            (string Name, string Value)[] fields = handler == ControlWords.Pause ? [("reason", "  Testing the controls  "), ("confirm", "pause")] : Filled(handler);
            var response = await RequestSteps.PostControlAsync(owner, page, handler, fields);

            var row = Assert.Single(await db.SentAsync());
            SentTo(response, row.id);
            Assert.Equal((ControlsModel.Handlers[handler], TestUsers.Owner.Id, "Test Owner", RequestRoles.Owner), (row.kind, row.actor_id, row.actor_name, row.actor_role));
            Assert.Equal(handler switch { ControlWords.Pause => "Testing the controls", ControlWords.SetStatus => "Back soon", _ => null }, row.argument);
            Assert.True(RequestSigning.Verify(Convert.FromHexString(factory.RequestSigningKey), row.Fields(), row.signature));

            var audit = Assert.Single(await db.AuditAsync(), entry => entry.action == row.kind);
            Assert.Equal((AuditOutcome.Ok, "request", row.id.ToString(CultureInfo.InvariantCulture), "198.51.100.104"), (audit.outcome, audit.target_type!, audit.target_id!, audit.ip!));
        }

        // An Admin's request carries their own role and ID, from the lists on the request, never the Owner's
        [Fact]
        public async Task AnAdminsRequestCarriesTheAdminRole()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.105");

            var response = await RequestSteps.PostControlAsync(admin, await RequestSteps.ControlsAsync(admin), ControlWords.ClearCaches);

            var row = Assert.Single(await db.SentAsync());
            SentTo(response, row.id);
            Assert.Equal((TestUsers.Admin.Id, "Test Admin", RequestRoles.Admin), (row.actor_id, row.actor_name, row.actor_role));
            Assert.Equal(AuditRole.Admin, Assert.Single(await db.AuditAsync(), entry => entry.action == RequestKinds.CachesClear).actor_role);
        }

        // Each control says who asked for it last, with their role, when, how it stands and the bot's own words, linking to
        // the request's page; request.sh's requests by its own name; a control nobody used, that it was never asked for
        [Fact]
        public async Task LastUseShowsWhoWhenAndTheResult()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.106");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            // A cache clear the bot did, an older one, a restart it refused, and the colour job asked for by request.sh
            await RequestSteps.AddAsync(db, factory, RequestKinds.CachesClear, now.AddMinutes(-9));
            var clear = await RequestSteps.AddAsync(db, factory, RequestKinds.CachesClear, now.AddMinutes(-3), TestUsers.Admin);
            await RequestSteps.ClaimAsync(db, clear, now.AddMinutes(-3).AddSeconds(1));
            await RequestSteps.FinishAsync(db, clear, RequestStates.Done, "Cleared the armor-name and latest-maps caches.", now.AddMinutes(-3).AddSeconds(2));
            var restart = await RequestSteps.AddAsync(db, factory, RequestKinds.BotRestart, now.AddMinutes(-2));
            await RequestSteps.DecideAsync(db, restart, RequestStates.Refused, "The bot takes a restart only 5 minutes after it starts.", now.AddMinutes(-2).AddSeconds(1));
            var colors = await RequestSteps.AddAsync(db, factory, RequestKinds.JobColors, now.AddMinutes(-1), name: RequestCommand.ActorName);

            var html = await owner.Client.GetStringAsync("/admin/bot/controls", Token);

            Assert.Contains($"Last asked for by Test Admin (Admin) at {TimeWords.Utc(now.AddMinutes(-3))}: Done. Cleared the armor-name and latest-maps caches. Request {clear}.",
                Control(html, "Clear the caches"));
            Assert.Contains($"Last asked for by Test Owner (Owner) at {TimeWords.Utc(now.AddMinutes(-2))}: Refused. The bot takes a restart only 5 minutes after it starts. Request {restart}.",
                Control(html, "Restart the bot"));
            Assert.Contains($"Last asked for by request.sh on the server (Owner) at {TimeWords.Utc(now.AddMinutes(-1))}: Queued. Request {colors}.", Control(html, "Run the colour job now"));
            Assert.Contains(RequestWords.NotAskedFor, Control(html, "Renew the Steam token"));
            Assert.Contains($"<a href=\"/admin/requests/{clear}\">Request {clear}</a>", html);
            PageTests.AssertAccessible("the controls page with last uses", html);
        }

        // One form sent twice, as a double click sends it, writes one request, and both answers lead to it
        [Fact]
        public async Task ADoubleSubmitWritesOneRequest()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.107");
            var page = await RequestSteps.ControlsAsync(owner);

            var first = await RequestSteps.PostControlAsync(owner, page, ControlWords.ClearCaches);
            var second = await RequestSteps.PostControlAsync(owner, page, ControlWords.ClearCaches);

            var row = Assert.Single(await db.SentAsync());
            SentTo(first, row.id);
            SentTo(second, row.id);
        }

        // A restart's form sent twice is still its first request, though the first waits and fills the bot's limit: the key
        // is looked up before any limit. Fails if a second sending meets the limit and says so.
        [Fact]
        public async Task ADoubleSubmitOfARestartShowsTheFirstRequest()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.108");
            var page = await RequestSteps.ControlsAsync(owner);

            var first = await RequestSteps.PostControlAsync(owner, page, ControlWords.Restart, ("confirm", "restart"));
            var second = await RequestSteps.PostControlAsync(owner, page, ControlWords.Restart, ("confirm", "restart"));

            var row = Assert.Single(await db.SentAsync());
            SentTo(first, row.id);
            SentTo(second, row.id);
        }

        // A restart's form sent again from the page a browser kept, once the sign-in that sent it is more than 15 minutes old,
        // still leads to its first request, and no refusal is recorded: the key is looked up before the fresh sign-in. Fails
        // if the look comes after the fresh sign-in, when the page says to sign in again though the restart was sent.
        [Fact]
        public async Task ARestartSentAgainOnceTheSignInIsStaleShowsTheFirstRequest()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.230");
            var page = await RequestSteps.ControlsAsync(owner);

            var first = await RequestSteps.PostControlAsync(owner, page, ControlWords.Restart, ("confirm", "restart"));
            factory.Clock.Advance(TimeSpan.FromMinutes(16));
            var again = await RequestSteps.PostControlAsync(owner, page, ControlWords.Restart, ("confirm", "restart"));

            var row = Assert.Single(await db.SentAsync());
            SentTo(first, row.id);
            SentTo(again, row.id);
            Assert.DoesNotContain(await db.AuditAsync(), entry => entry.outcome == AuditOutcome.Refused);
        }

        // The status text's form sent again with another text, as from a page a browser kept, isn't sent: its notice says
        // the form was used, as request N, linking to it, and draws the form again with the new text under a new key
        [Fact]
        public async Task ASecondSendingWithOtherDetailsSaysTheFormWasUsed()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.109");
            var page = await RequestSteps.ControlsAsync(owner);

            var first = await RequestSteps.PostControlAsync(owner, page, ControlWords.SetStatus, ("text", "Back soon"));
            var again = await RequestSteps.AnsweredAsync(owner, await RequestSteps.PostControlAsync(owner, page, ControlWords.SetStatus, ("text", "Back later")));
            var html = await again.Content.ReadAsStringAsync(Token);

            var row = Assert.Single(await db.SentAsync());
            SentTo(first, row.id);
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal("Back soon", row.argument);
            Assert.Contains(RequestWords.SentBefore(row.id), Text(html));
            Assert.Contains($"<a href=\"/admin/requests/{row.id}\">That request</a>", html);
            Assert.Equal("Back later", BlacklistSteps.BoxValue(html, "text"));
            Assert.NotEqual(page.Keys[ControlWords.SetStatus], RequestSteps.ReadControls(html).Keys[ControlWords.SetStatus]);
            PageTests.AssertAccessible("a form sent before with other details", html);
        }

        // A form's key that belongs to another person's request, or another kind's, is refused with words of its own and
        // writes nothing, whether the look before the write finds it or the write's own unique key does, as when another tab
        // wrote it in between. Fails with an error page if the write's refusal isn't caught.
        [Fact]
        public async Task AFormKeyOfAnotherPersonOrKindIsRefused()
        {
            var db = await fixture.ReadyAsync();
            var racing = new ConcurrentDictionary<string, bool>();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider =>
                    new RacingQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider), racing)))
            });
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.110");
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.111");
            var theirs = RequestFields.NewKey();
            await RequestSteps.AddAsync(db, factory, RequestKinds.CachesClear, factory.Clock.GetUtcNow().UtcDateTime, TestUsers.Owner, key: theirs);

            // The first look misses the key, so the write's unique key refuses it; the second look finds it
            var otherPerson = await RequestSteps.AnsweredAsync(admin,
                await RequestSteps.PostControlAsync(admin, ControlWords.ClearCaches, (await RequestSteps.ControlsAsync(admin)).Token, theirs));
            var otherKind = await RequestSteps.AnsweredAsync(owner,
                await RequestSteps.PostControlAsync(owner, ControlWords.RunNotifications, (await RequestSteps.ControlsAsync(owner)).Token, theirs));

            foreach (var response in new[] { otherPerson, otherKind })
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Contains(RequestWords.KeyTaken, await TextAsync(response));
            }

            Assert.True(racing.ContainsKey(theirs), "The look before the write didn't miss the key");
            Assert.Single(await db.SentAsync());
        }

        // With no request-signing key nothing can be sent: the page says so, draws no form, and a form someone made up is
        // refused with the words that say how to turn requests on, writing nothing. Nor does it offer the Owner's sign-in
        // section, which would say the controls work until a time, or offer a sign-in that sends no message and makes nothing
        // work, fresh or not.
        [Fact]
        public async Task RequestsOffShowsNoControlsAndRefusesPosts()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions { Settings = new Dictionary<string, string?> { ["Dashboard:RequestSigningKey"] = "" } });
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.112");

            var html = await owner.Client.GetStringAsync("/admin/bot/controls", Token);
            Assert.Contains("Requests to the bot are off: the dashboard has no request-signing key, so its controls, Announce now, test messages, refreshes, custom-link lookups and "
                + "sign-in messages don't work.", Text(html));
            Assert.DoesNotMatch("<form[^>]*\\?handler=", html);
            Assert.DoesNotContain("id=\"sign-in\"", html);
            Assert.DoesNotContain("so these work until", Text(html));
            PageTests.AssertAccessible("the controls page with requests off", html);

            var response = await RequestSteps.AnsweredAsync(owner, await RequestSteps.PostControlAsync(owner, ControlWords.ClearCaches, SignInSteps.FormToken(html),
                RequestFields.NewKey()));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains(RequestWords.Off, await TextAsync(response));
            Assert.Empty(await db.RequestsAsync());

            factory.Clock.Advance(TimeSpan.FromMinutes(16));
            var stale = await owner.Client.GetStringAsync("/admin/bot/controls", Token);
            Assert.DoesNotContain("id=\"sign-in\"", stale);
            Assert.DoesNotContain(ControlWords.SignInAgain, stale);
        }

        // A text the bot would refuse is refused before anything is written, in the rule's own words, with what was typed
        // back in its box, encoded, and the typed word's box empty
        [Fact]
        public async Task ARuleRefusalKeepsTheTextAndNamesTheRule()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.113");
            var page = await RequestSteps.ControlsAsync(owner);

            var link = await RequestSteps.AnsweredAsync(owner, await RequestSteps.PostControlAsync(owner, page, ControlWords.SetStatus, ("text", "<b>x.com/minerbot</b>")));
            var linkHtml = await link.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.BadRequest, link.StatusCode);
            Assert.Contains("The bot would refuse this, so nothing was sent: the status text has a link.", Text(linkHtml));
            Assert.Contains("<div class=\"notice notice-problem\" role=\"alert\">", linkHtml);
            Assert.Contains("value=\"&lt;b&gt;x.com/minerbot&lt;/b&gt;\"", linkHtml);
            Assert.DoesNotContain("<b>x.com", linkHtml);
            PageTests.AssertAccessible("a status text the rules refuse", linkHtml);

            var hidden = await RequestSteps.AnsweredAsync(owner, await RequestSteps.PostControlAsync(owner, page, ControlWords.Pause, ("reason", "Wait\u200Bing"), ("confirm", "pause")));
            var hiddenHtml = await hidden.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.BadRequest, hidden.StatusCode);
            Assert.Contains("The bot would refuse this, so nothing was sent: the reason has a character that shows nothing or changes the text's direction.", Text(hiddenHtml));
            Assert.Equal("Wait\u200Bing", BlacklistSteps.BoxValue(hiddenHtml, "reason"));
            Assert.DoesNotContain("value=\"pause\"", hiddenHtml);
            Assert.Empty(await db.SentAsync());
        }

        // The status text's own form, sent empty or without its box, asks for the text and sends nothing; Back to the default
        // sends the status kind with no text
        [Fact]
        public async Task AnEmptyStatusTextIsNotTheDefault()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.114");
            var page = await RequestSteps.ControlsAsync(owner);

            // Each notice read before the next press, which would replace it
            foreach (var empty in new Func<Task<HttpResponseMessage>>[]
            {
                () => RequestSteps.PostControlAsync(owner, page, ControlWords.SetStatus, ("text", "")),
                () => RequestSteps.PostControlAsync(owner, page, ControlWords.SetStatus, ("text", "   ")),
                () => RequestSteps.PostControlAsync(owner, ControlWords.SetStatus, page.Token, RequestFields.NewKey())
            })
            {
                var answer = await RequestSteps.AnsweredAsync(owner, await empty());
                Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
                Assert.Contains(RequestWords.NoStatusText, await TextAsync(answer));
            }

            Assert.Empty(await db.SentAsync());

            var reset = await RequestSteps.PostControlAsync(owner, page, ControlWords.DefaultStatus);
            var row = Assert.Single(await db.SentAsync());
            SentTo(reset, row.id);
            Assert.Equal((RequestKinds.StatusSet, (string?)null), (row.kind, row.argument));
        }

        // The typed word is the control's own, trimmed and in any case: an empty box, a word cut short and a form without the
        // box send nothing, and the word with a space after it, or in capitals, is sent. The form's own key still sends once
        // the word is right, since a refusal wrote nothing.
        [Fact]
        public async Task AWrongOrMissingConfirmationSendsNothing()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.115");
            var page = await RequestSteps.ControlsAsync(owner);

            // Each notice read before the next press, which would replace it
            foreach (var typed in new (string Name, string Value)[][] { [("confirm", "")], [("confirm", "restar")], [("confirm", "pause")], [] })
            {
                var answer = await RequestSteps.AnsweredAsync(owner, await RequestSteps.PostControlAsync(owner, page, ControlWords.Restart, typed));
                Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
                Assert.Contains("Type restart in the box to confirm. Nothing was sent.", await TextAsync(answer));
            }

            Assert.Empty(await db.SentAsync());

            SentTo(await RequestSteps.PostControlAsync(owner, page, ControlWords.Restart, ("confirm", "Restart ")), Assert.Single(await db.SentAsync()).id);
            await db.ClearAsync(MariaDbFixture.RequestTable);

            var capitals = await RequestSteps.PostControlAsync(owner, await RequestSteps.ControlsAsync(owner), ControlWords.Restart, ("confirm", "RESTART"));
            SentTo(capitals, Assert.Single(await db.SentAsync()).id);
        }

        // The bot's own limit, met before writing: a restart the bot took 2 minutes ago makes another a refusal in the bot's
        // words, with when to try again, writing nothing. Refused and expired restarts, which the bot never took, don't count.
        [Fact]
        public async Task TheBotsLimitIsCheckedBeforeWriting()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.116");
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            await RequestSteps.TakenAsync(db, factory, RequestKinds.BotRestart, now.AddMinutes(-2).AddSeconds(-1));

            var refused = await RequestSteps.AnsweredAsync(owner,
                await RequestSteps.PostControlAsync(owner, await RequestSteps.ControlsAsync(owner), ControlWords.Restart, ("confirm", "restart")));

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains($"The bot takes this at most once every 10 minutes, and last took one at {RequestWords.Clock(now.AddMinutes(-2))}, so nothing was sent. "
                + $"Try again after {RequestWords.Clock(now.AddMinutes(8))}.", await TextAsync(refused));
            Assert.Single(await db.SentAsync());

            await db.ClearAsync(MariaDbFixture.RequestTable);
            await RequestSteps.DecideAsync(db, await RequestSteps.AddAsync(db, factory, RequestKinds.BotRestart, now.AddMinutes(-3)), RequestStates.Refused,
                "The bot takes this at most once every 10 minutes.", now.AddMinutes(-3).AddSeconds(1));
            await RequestSteps.DecideAsync(db, await RequestSteps.AddAsync(db, factory, RequestKinds.BotRestart, now.AddMinutes(-5)), RequestStates.Expired,
                "Expired", now.AddMinutes(-3));

            var sent = await RequestSteps.PostControlAsync(owner, await RequestSteps.ControlsAsync(owner), ControlWords.Restart, ("confirm", "restart"));
            SentTo(sent, (await db.SentAsync())[^1].id);
            Assert.Equal(3, (await db.SentAsync()).Count);
        }

        // A restart that waits for the bot fills the limit the bot will count it toward: another is refused, naming it with
        // a link, until it expires, when one is sent
        [Fact]
        public async Task AWaitingRequestCountsTowardTheLimit()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.117");
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var waiting = await RequestSteps.AddAsync(db, factory, RequestKinds.BotRestart, now.AddSeconds(-30));

            var refused = await RequestSteps.AnsweredAsync(owner,
                await RequestSteps.PostControlAsync(owner, await RequestSteps.ControlsAsync(owner), ControlWords.Restart, ("confirm", "restart")));
            var html = await refused.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains($"A request like this is already waiting for the bot: request {waiting}, asked for at {RequestWords.Clock(now.AddSeconds(-30))}.", Text(html));
            Assert.Contains($"<a href=\"/admin/requests/{waiting}\">That request</a>", html);
            Assert.Single(await db.SentAsync());

            factory.Clock.Advance(TimeSpan.FromSeconds(91));
            var sent = await RequestSteps.PostControlAsync(owner, await RequestSteps.ControlsAsync(owner), ControlWords.Restart, ("confirm", "restart"));

            SentTo(sent, (await db.SentAsync())[^1].id);
            Assert.Equal(2, (await db.SentAsync()).Count);
        }

        // A save the database didn't confirm may have landed: its notice says the page can't tell, with the form's key kept,
        // so sending it again finds the first request rather than writing a second. Every other form gets a new key.
        [Fact]
        public async Task AnUnconfirmedSaveKeepsTheFormKeySoSendingAgainIsSafe()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider =>
                    new UnconfirmedQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider))))
            });
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.118");
            var page = await RequestSteps.ControlsAsync(owner);

            var unsure = await RequestSteps.AnsweredAsync(owner, await RequestSteps.PostControlAsync(owner, page, ControlWords.ClearCaches));
            var html = await unsure.Content.ReadAsStringAsync(Token);
            var redrawn = RequestSteps.ReadControls(html);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, unsure.StatusCode);
            Assert.Contains(RequestWords.Unsure, Text(html));
            Assert.Equal(page.Keys[ControlWords.ClearCaches], redrawn.Keys[ControlWords.ClearCaches]);
            Assert.NotEqual(page.Keys[ControlWords.RunNotifications], redrawn.Keys[ControlWords.RunNotifications]);
            PageTests.AssertAccessible("a save the database didn't confirm", html);

            var row = Assert.Single(await db.SentAsync());
            SentTo(await RequestSteps.PostControlAsync(owner, redrawn, ControlWords.ClearCaches), row.id);
            Assert.Single(await db.SentAsync());
        }

        // The same when the database stays down after the commit, as it usually does when a commit's answer never came. The
        // notice's GET can't read its session then, so the error page says it can't tell whether it was saved, never that
        // nothing was changed though the request is in the table, and writes no cookie. Once sessions answer but the page's
        // own reads still fail, the page shows why and the form sent, alone, with its key and its reason kept: a resume's too,
        // with the words for its form, since the page can't read posts then and draws no line under Posts. Once the database
        // answers, the whole page with the form, its key and its reason, and sending it again finds the first request.
        // Only such a save draws its form alone: a notice of a refusal, which sent nothing, gets the error page while the
        // page's reads fail, which truly says nothing was changed.
        [Theory]
        [InlineData(ControlWords.Pause)]
        [InlineData(ControlWords.Resume)]
        public async Task AnUnconfirmedSaveWhileTheDatabaseStaysDownStillShowsItsForm(string handler)
        {
            var db = await fixture.ReadyAsync();
            SwitchedDatabase? database = null;
            SwitchedDatabase? sessions = null;
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services =>
                {
                    services.RemoveAll<ISQLConnectionFactory>();
                    services.AddSingleton<ISQLConnectionFactory>(provider =>
                        database = new SwitchedDatabase(new SQLConnectionFactory(provider.GetRequiredService<IDictionary<DBConnectionName, string>>())));

                    // The sessions on connections of their own, which can answer while the page's reads don't
                    services.AddSingleton(provider => sessions = new SwitchedDatabase(new SQLConnectionFactory(provider.GetRequiredService<IDictionary<DBConnectionName, string>>())));
                    services.Replace(ServiceDescriptor.Transient<IDashboardSessionData>(provider =>
                        ActivatorUtilities.CreateInstance<DashboardSessionData>(provider, provider.GetRequiredService<SwitchedDatabase>())));
                    services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider =>
                        new UnconfirmedQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider), () => database!.Down = sessions!.Down = true)));
                }
            });
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.126");
            var pause = handler == ControlWords.Pause;

            // A resume's form is drawn while posts are paused
            if (!pause)
                await db.Requests().SavePause(true, "Testing the pause", factory.Clock.GetUtcNow().UtcDateTime, TestUsers.Owner.Id, 1, factory.Clock.GetUtcNow().UtcDateTime);

            var page = await RequestSteps.ControlsAsync(owner);

            var address = await RequestSteps.NoticeOfAsync(await RequestSteps.PostControlAsync(owner, page, handler, Filled(handler)));
            Assert.True(database!.Down && sessions!.Down, "The database came back before the notice's GET");

            var down = await owner.Client.GetAsync(address, Token);
            var downHtml = await down.Content.ReadAsStringAsync(Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
            Assert.Contains(ErrorResponses.UnsureTitle, Text(downHtml));
            Assert.Contains(ErrorResponses.UnsureUnavailable, Text(downHtml));
            Assert.DoesNotContain(ErrorResponses.Unavailable, Text(downHtml));
            Assert.Empty(RequestSteps.SetCookies(down));

            sessions.Down = false;
            var unread = await owner.Client.GetAsync(address, Token);
            var html = await unread.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, unread.StatusCode);
            Assert.DoesNotContain(ErrorResponses.Unavailable, Text(html));
            Assert.Contains(RequestWords.Unsure, Text(html));

            var alone = RequestSteps.ReadControls(html);
            Assert.Equal([handler], alone.Handlers);
            Assert.Equal(page.Keys[handler], alone.Keys[handler]);
            Assert.Equal(pause, html.Contains("name=\"reason\" value=\"Testing the controls\"", StringComparison.Ordinal));
            PageTests.AssertAccessible("a save the database didn't confirm, with the page's reads still down", html);

            database.Down = false;
            var back = await owner.Client.GetAsync(address, Token);
            var backHtml = await back.Content.ReadAsStringAsync(Token);
            var redrawn = RequestSteps.ReadControls(backHtml);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, back.StatusCode);
            Assert.Contains(RequestWords.Unsure, Text(backHtml));
            Assert.Contains(ControlWords.Restart, redrawn.Handlers);
            Assert.Equal(page.Keys[handler], redrawn.Keys[handler]);
            Assert.Equal(pause, backHtml.Contains("name=\"reason\" value=\"Testing the controls\"", StringComparison.Ordinal));

            var row = Assert.Single(await db.SentAsync());
            Assert.Equal(pause ? RequestKinds.PostsPause : RequestKinds.PostsResume, row.kind);
            SentTo(await RequestSteps.PostControlAsync(owner, redrawn, handler, Filled(handler)), row.id);
            Assert.Single(await db.SentAsync());

            var refused = await RequestSteps.NoticeOfAsync(await RequestSteps.PostControlAsync(owner, await RequestSteps.ControlsAsync(owner), ControlWords.Restart,
                ("confirm", "restar")));
            database.Down = true;
            var plain = await owner.Client.GetAsync(refused, Token);
            var plainHtml = await plain.Content.ReadAsStringAsync(Token);
            database.Down = false;

            Assert.Equal(HttpStatusCode.ServiceUnavailable, plain.StatusCode);
            Assert.Contains(ErrorResponses.Unavailable, Text(plainHtml));
            Assert.DoesNotContain("<form", plainHtml);
            Assert.Single(await db.SentAsync());
        }

        // The same for a form sent as the sign-in neared its 15 minutes, which passed while the commit's answer was awaited,
        // with the database down after it, when the notice's GET first says it can't tell, and not: once the page is drawn,
        // the form isn't drawn again, as none is while the sign-in isn't fresh, so the words don't offer it. The way to sign
        // in again comes back to the control, and the control's last use there is the request the database saved. Fails if
        // the page says to send it again from a form it doesn't draw, or the way back loses the control.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task AnUnconfirmedSaveWhoseSignInGoesStaleOffersNoForm(bool down)
        {
            var db = await fixture.ReadyAsync();
            SwitchedDatabase? database = null;
            FakeClock? clock = null;
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services =>
                {
                    services.RemoveAll<ISQLConnectionFactory>();
                    services.AddSingleton<ISQLConnectionFactory>(provider =>
                        database = new SwitchedDatabase(new SQLConnectionFactory(provider.GetRequiredService<IDictionary<DBConnectionName, string>>())));

                    // The commit's answer awaited for 15 seconds, and the database down after it or not
                    services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider =>
                        new UnconfirmedQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider), () =>
                        {
                            clock!.Advance(TimeSpan.FromSeconds(15));
                            database!.Down = down;
                        })));
                }
            });
            clock = factory.Clock;
            var owner = await SignedInAsync(factory, TestUsers.Owner, down ? "198.51.100.128" : "198.51.100.129");
            factory.Clock.Advance(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(50));
            var page = await RequestSteps.ControlsAsync(owner);
            Assert.Contains(ControlWords.SetStatus, page.Handlers);

            var address = await RequestSteps.NoticeOfAsync(await RequestSteps.PostControlAsync(owner, page, ControlWords.SetStatus, ("text", "Back at noon")));
            Assert.Equal(down, database!.Down);

            if (down)
            {
                var notSure = await owner.Client.GetAsync(address, Token);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, notSure.StatusCode);
                Assert.Contains(ErrorResponses.UnsureUnavailable, Text(await notSure.Content.ReadAsStringAsync(Token)));
                database.Down = false;
            }

            var unsure = await owner.Client.GetAsync(address, Token);
            var html = await unsure.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, unsure.StatusCode);
            Assert.DoesNotContain(ErrorResponses.Unavailable, Text(html));
            Assert.Contains(RequestWords.UnsureNotFresh, Text(html));
            Assert.DoesNotContain(RequestWords.Unsure, Text(html));
            Assert.DoesNotContain(ControlWords.SetStatus, RequestSteps.ReadControls(html).Handlers);
            Assert.Contains(WebUtility.HtmlEncode(FreshSignIn.SignInAgainLink("/admin/bot/controls#status-text")), html);
            Assert.Contains("What you typed: Back at noon", Text(html));
            PageTests.AssertAccessible("a save the database didn't confirm, whose sign-in went stale meanwhile", html);

            // Signed in again, back at the control, whose last use is the request the database saved
            var row = Assert.Single(await db.SentAsync());
            var again = await SignInSteps.SignInAgainAsync(factory, owner, TestUsers.Owner, "/admin/bot/controls#status-text");
            Assert.Equal("/admin/bot/controls#status-text", SignInSteps.PathOf(again.Headers.Location));
            var controls = await RequestSteps.ControlsAsync(owner);
            Assert.Contains($"Request {row.id}.", Control(controls.Html, ControlWords.Of(ControlWords.SetStatus).Label));
        }

        // The same for a pause or a resume the bot took while the commit's answer was awaited, as it does within 3 seconds:
        // the page draws the other one's control in its place, so it has neither the form sent nor that control's last use,
        // and the line under Posts names the request. With the database down and the sign-in gone stale, the notice's GET
        // first says it can't tell, and once the database answers, the page says the same as without: the line under Posts
        // needs no sign-in. Fails if the words offer a form that isn't drawn, or send the person only to a last use that isn't.
        [Theory]
        [InlineData(ControlWords.Pause, false)]
        [InlineData(ControlWords.Resume, false)]
        [InlineData(ControlWords.Pause, true)]
        public async Task AnUnconfirmedPauseOrResumeTheBotTookPointsToThePostsLine(string handler, bool staleAndDown)
        {
            var db = await fixture.ReadyBotAsync();
            SwitchedDatabase? database = null;
            FakeClock? clock = null;
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                Services = services =>
                {
                    services.RemoveAll<ISQLConnectionFactory>();
                    services.AddSingleton<ISQLConnectionFactory>(provider =>
                        database = new SwitchedDatabase(new SQLConnectionFactory(provider.GetRequiredService<IDictionary<DBConnectionName, string>>())));

                    // The bot's part, done before the answer that never comes
                    services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider =>
                        new UnconfirmedQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider), () =>
                        {
                            var at = clock!.GetUtcNow().UtcDateTime;

                            Task.Run(async () =>
                            {
                                var row = Assert.Single(await db.SentAsync());
                                await RequestSteps.ClaimAsync(db, row.id, at);
                                await db.Requests().SavePause(handler == ControlWords.Pause, row.argument, at, row.actor_id, row.id, at);
                                await RequestSteps.FinishAsync(db, row.id, RequestStates.Done, "Done for a test", at);
                            }, Token).GetAwaiter().GetResult();

                            clock.Advance(TimeSpan.FromSeconds(15));
                            database!.Down = staleAndDown;
                        })));
                }
            });
            clock = factory.Clock;
            var owner = await SignedInAsync(factory, TestUsers.Owner, staleAndDown ? "198.51.100.130" : handler == ControlWords.Pause ? "198.51.100.131" : "198.51.100.132");

            if (staleAndDown)
                clock.Advance(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(50));

            if (handler == ControlWords.Resume)
                await db.Requests().SavePause(true, "Testing the pause", clock.GetUtcNow().UtcDateTime, TestUsers.Owner.Id, 1, clock.GetUtcNow().UtcDateTime);

            var page = await RequestSteps.ControlsAsync(owner);
            Assert.Contains(handler, page.Handlers);
            var address = await RequestSteps.NoticeOfAsync(await RequestSteps.PostControlAsync(owner, page, handler, Filled(handler)));
            var row = Assert.Single(await db.SentAsync());

            if (staleAndDown)
            {
                var notSure = await owner.Client.GetAsync(address, Token);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, notSure.StatusCode);
                Assert.Contains(ErrorResponses.UnsureUnavailable, Text(await notSure.Content.ReadAsStringAsync(Token)));
                database!.Down = false;
            }

            var unsure = await owner.Client.GetAsync(address, Token);
            var html = await unsure.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, unsure.StatusCode);
            Assert.DoesNotContain(handler, RequestSteps.ReadControls(html).Handlers);
            Assert.DoesNotContain(RequestWords.Unsure, Text(html));
            Assert.Contains("The line under Posts shows posts as they are now and the request that last changed them", Text(html));
            Assert.Contains($"on request {row.id}", Text(html));
            PageTests.AssertAccessible("an unconfirmed pause or resume the bot took", html);
        }

        // The same once the dashboard has started again with requests off, within the notice's 30 minutes: the page draws no
        // form, so the words don't offer one, and say where to look. Two factories share a keys folder, as one dashboard
        // restarting, and the browser's cookies go from one to the other. Fails if the words offer a form that isn't drawn.
        [Fact]
        public async Task AnUnconfirmedSaveOnceRequestsAreOffOffersNoForm()
        {
            var db = await fixture.ReadyAsync();
            var keys = DashboardFactory.NewKeysFolder();

            try
            {
                string address;
                CookieCollection cookies;

                await using (var before = new DashboardFactory(db, new DashboardFactoryOptions
                {
                    KeysFolder = keys,
                    Services = services => services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider =>
                        new UnconfirmedQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider))))
                }))
                {
                    var owner = await SignedInAsync(before, TestUsers.Owner, "198.51.100.236");
                    address = await RequestSteps.NoticeOfAsync(await RequestSteps.PostControlAsync(owner, await RequestSteps.ControlsAsync(owner), ControlWords.ClearCaches));
                    cookies = owner.Jar.GetCookies(DashboardFactory.PublicAddress);
                }

                await using var after = new DashboardFactory(db, new DashboardFactoryOptions
                {
                    KeysFolder = keys,
                    Settings = new Dictionary<string, string?> { ["Dashboard:RequestSigningKey"] = "" }
                });
                var browser = after.Visitor("198.51.100.236");

                foreach (Cookie cookie in cookies)
                    browser.Jar.Add(DashboardFactory.PublicAddress, new Cookie(cookie.Name, cookie.Value) { Secure = true });

                var unsure = await browser.Client.GetAsync(address, Token);
                var html = await unsure.Content.ReadAsStringAsync(Token);

                Assert.Equal(HttpStatusCode.ServiceUnavailable, unsure.StatusCode);
                Assert.Empty(RequestSteps.ReadControls(html).Handlers);
                Assert.DoesNotContain(RequestWords.Unsure, Text(html));
                Assert.Contains(RequestWords.UnsureNotOffered, Text(html));
                PageTests.AssertAccessible("a save the database didn't confirm, once requests are off", html);
            }
            finally
            {
                DashboardFactory.DeleteFolder(keys);
            }
        }

        // The same once the lists have moved the Owner to the Admins: the page draws neither the form sent nor the Posts
        // section, so for a pause the bot took the words point to no line under Posts either. Fails if they offer a form or a
        // line the page doesn't draw.
        [Fact]
        public async Task AnUnconfirmedPauseOfSomeoneNoLongerTheOwnerOffersNoForm()
        {
            var db = await fixture.ReadyBotAsync();
            FakeClock? clock = null;
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions
            {
                // The bot's part, done before the answer that never comes
                Services = services => services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider =>
                    new UnconfirmedQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider), () =>
                    {
                        var at = clock!.GetUtcNow().UtcDateTime;

                        Task.Run(async () =>
                        {
                            var row = Assert.Single(await db.SentAsync());
                            await RequestSteps.ClaimAsync(db, row.id, at);
                            await db.Requests().SavePause(true, row.argument, at, row.actor_id, row.id, at);
                            await RequestSteps.FinishAsync(db, row.id, RequestStates.Done, "Done for a test", at);
                        }, Token).GetAwaiter().GetResult();
                    })))
            });
            clock = factory.Clock;
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.237");
            var address = await RequestSteps.NoticeOfAsync(await RequestSteps.PostControlAsync(owner, await RequestSteps.ControlsAsync(owner), ControlWords.Pause,
                Filled(ControlWords.Pause)));

            factory.Settings.Change(("Dashboard:Owners", TestUsers.SecondAdmin.IdText), ("Dashboard:Admins", $"{TestUsers.Owner.IdText},{TestUsers.Admin.IdText}"));
            var unsure = await owner.Client.GetAsync(address, Token);
            var html = await unsure.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, unsure.StatusCode);
            Assert.Contains(ControlWords.AdminLine, Text(html));
            Assert.DoesNotContain("id=\"posts\"", html);
            Assert.DoesNotContain(RequestWords.UnsurePostsLine, Text(html));
            Assert.DoesNotContain(RequestWords.Unsure, Text(html));
            Assert.Contains(RequestWords.UnsureNotOffered, Text(html));
            Assert.DoesNotContain(ControlWords.Pause, RequestSteps.ReadControls(html).Handlers);
        }

        // A pause sent again with another reason from the page a browser kept with the first one's key, as Back can restore it
        // from the back-forward cache: while the bot hasn't taken the first, the words offer the form below, drawn with the new
        // reason. Once it has, the page draws Resume in Pause's place, so they name the request without offering a form.
        // Fails if the words offer a form the page doesn't draw.
        [Fact]
        public async Task AFormSentBeforeWhoseFormIsntDrawnOffersNoForm()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.238");
            var page = await RequestSteps.ControlsAsync(owner);
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var first = await RequestSteps.PostControlAsync(owner, page, ControlWords.Pause, ("reason", "First reason"), ("confirm", "pause"));
            var row = Assert.Single(await db.SentAsync());
            SentTo(first, row.id);

            var waiting = await RequestSteps.AnsweredAsync(owner, await RequestSteps.PostControlAsync(owner, page, ControlWords.Pause, ("reason", "Second reason"), ("confirm", "pause")));
            var waitingHtml = await waiting.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.Conflict, waiting.StatusCode);
            Assert.Contains(RequestWords.SentBefore(row.id), Text(waitingHtml));
            Assert.Contains(ControlWords.Pause, RequestSteps.ReadControls(waitingHtml).Handlers);
            Assert.Equal("Second reason", BlacklistSteps.BoxValue(waitingHtml, "reason"));

            // The bot's part
            await RequestSteps.ClaimAsync(db, row.id, now);
            await db.Requests().SavePause(true, row.argument, now, row.actor_id, row.id, now);
            await RequestSteps.FinishAsync(db, row.id, RequestStates.Done, "Done for a test", now);

            var taken = await RequestSteps.AnsweredAsync(owner, await RequestSteps.PostControlAsync(owner, page, ControlWords.Pause, ("reason", "Third reason"), ("confirm", "pause")));
            var html = await taken.Content.ReadAsStringAsync(Token);
            var controls = RequestSteps.ReadControls(html);

            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
            Assert.Contains(RequestWords.SentBeforeNotOffered(row.id), Text(html));
            Assert.DoesNotContain("The form below is new", Text(html));
            Assert.Contains($"<a href=\"/admin/requests/{row.id}\">That request</a>", html);
            Assert.DoesNotContain(ControlWords.Pause, controls.Handlers);
            Assert.Contains(ControlWords.Resume, controls.Handlers);
            Assert.Single(await db.SentAsync());
            PageTests.AssertAccessible("a form sent before whose form isn't drawn", html);
        }

        // The same once the sign-in has passed its 15 minutes, within the notice's 30: a status text sent again with other
        // text from the page a browser kept, whose form the page then draws no more, gets the words that offer none. A
        // restart refused because another waits for the bot, whose form the page draws no more either, keeps its own words,
        // which name that request: it's no form of this one sent before. Fails if the words offer a form the page doesn't
        // draw, or say a form was sent before when it wasn't.
        [Fact]
        public async Task AFormSentBeforeOnceTheSignInIsStaleOffersNoForm()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "192.0.2.161");
            var other = await SignedInAsync(factory, TestUsers.Owner, "192.0.2.162");
            var page = await RequestSteps.ControlsAsync(owner);
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var first = await RequestSteps.PostControlAsync(owner, page, ControlWords.SetStatus, ("text", "Back soon"));
            var row = Assert.Single(await db.SentAsync());
            SentTo(first, row.id);
            var sentBefore = await RequestSteps.NoticeOfAsync(await RequestSteps.PostControlAsync(owner, page, ControlWords.SetStatus, ("text", "Back later")));

            var waiting = await RequestSteps.AddAsync(db, factory, RequestKinds.BotRestart, now.AddSeconds(-30));
            var waits = await RequestSteps.NoticeOfAsync(await RequestSteps.PostControlAsync(other, await RequestSteps.ControlsAsync(other), ControlWords.Restart,
                ("confirm", "restart")));
            var waitsWords = $"A request like this is already waiting for the bot: request {waiting}, asked for at {RequestWords.Clock(now.AddSeconds(-30))}.";
            Assert.Contains(waitsWords, await TextAsync(await other.Client.GetAsync(waits, Token)));

            factory.Clock.Advance(TimeSpan.FromMinutes(16));
            var stale = await owner.Client.GetAsync(sentBefore, Token);
            var html = await stale.Content.ReadAsStringAsync(Token);
            var controls = RequestSteps.ReadControls(html);

            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Contains(RequestWords.SentBeforeNotOffered(row.id), Text(html));
            Assert.DoesNotContain("The form below is new", Text(html));
            Assert.Contains($"<a href=\"/admin/requests/{row.id}\">That request</a>", html);
            Assert.DoesNotContain(ControlWords.SetStatus, controls.Handlers);
            Assert.Contains(ControlWords.DefaultStatus, controls.Handlers);
            PageTests.AssertAccessible("a form sent before, once the sign-in is stale", html);

            var waited = await other.Client.GetAsync(waits, Token);
            var waitedHtml = await waited.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.Conflict, waited.StatusCode);
            Assert.Contains(waitsWords, Text(waitedHtml));
            Assert.DoesNotContain("This form was sent already", Text(waitedHtml));
            Assert.DoesNotContain(ControlWords.Restart, RequestSteps.ReadControls(waitedHtml).Handlers);
            Assert.Equal(2, (await db.SentAsync()).Count);
        }

        // A word typed wrong gets words that send the person to the box, while the page draws it. Corrected and sent from the
        // notice's own page, a pause is taken by the bot, so the page draws Resume in its place, and the notice's address,
        // fetched again as Back or a reload fetches it within its 30 minutes, gets words that point to no box; so does a
        // restart's once the sign-in has passed its 15 minutes, when the way to sign in again stands in its form's place. A
        // pause the rules refused for its reason, with the word typed right, keeps the rule's words once its form has gone.
        // Fails if the words send the person to a box the page doesn't draw, or the box's words go while it's drawn, or a
        // refusal of the reason says the word didn't match.
        [Fact]
        public async Task AWordTypedWrongPointsToNoBoxThePageDoesntDraw()
        {
            const string ruleWords = "The bot would refuse this, so nothing was sent: the reason has a character that shows nothing or changes the text's direction.";
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "192.0.2.160");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var paws = await RequestSteps.NoticeOfAsync(await RequestSteps.PostControlAsync(owner, await RequestSteps.ControlsAsync(owner), ControlWords.Pause,
                ("reason", "Testing the controls"), ("confirm", "paws")));
            var refused = await owner.Client.GetAsync(paws, Token);
            var refusedHtml = await refused.Content.ReadAsStringAsync(Token);
            var drawn = RequestSteps.ReadControls(refusedHtml);

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains(RequestWords.NotConfirmed("pause"), Text(refusedHtml));
            Assert.DoesNotContain(RequestWords.NotConfirmedNotOffered, Text(refusedHtml));
            Assert.Contains(ControlWords.Pause, drawn.Handlers);

            // In a second session, a pause the rules refuse for its reason, with the word typed right, while its form is drawn
            var other = await SignedInAsync(factory, TestUsers.Owner, "192.0.2.167");
            var rule = await RequestSteps.NoticeOfAsync(await RequestSteps.PostControlAsync(other, await RequestSteps.ControlsAsync(other), ControlWords.Pause,
                ("reason", "Wait\u200Bing"), ("confirm", "pause")));
            Assert.Contains(ruleWords, await TextAsync(await other.Client.GetAsync(rule, Token)));

            var sent = await RequestSteps.PostControlAsync(owner, drawn, ControlWords.Pause, ("reason", "Testing the controls"), ("confirm", "pause"));
            var row = Assert.Single(await db.SentAsync());
            SentTo(sent, row.id);

            // The bot's part
            await RequestSteps.ClaimAsync(db, row.id, now);
            await db.Requests().SavePause(true, row.argument, now, row.actor_id, row.id, now);
            await RequestSteps.FinishAsync(db, row.id, RequestStates.Done, "Done for a test", now);

            var taken = await owner.Client.GetAsync(paws, Token);
            var html = await taken.Content.ReadAsStringAsync(Token);
            var controls = RequestSteps.ReadControls(html);

            Assert.Equal(HttpStatusCode.BadRequest, taken.StatusCode);
            Assert.Contains(RequestWords.NotConfirmedNotOffered, Text(html));
            Assert.DoesNotContain("in the box", Text(html));
            Assert.DoesNotContain(ControlWords.Pause, controls.Handlers);
            Assert.Contains(ControlWords.Resume, controls.Handlers);
            PageTests.AssertAccessible("a word typed wrong, once the bot has taken the pause sent after it", html);

            // The rules' refusal, fetched again now its form has gone, still names the rule: the word matched
            var ruled = await other.Client.GetAsync(rule, Token);
            var ruledHtml = await ruled.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.BadRequest, ruled.StatusCode);
            Assert.Contains(ruleWords, Text(ruledHtml));
            Assert.DoesNotContain(RequestWords.NotConfirmedNotOffered, Text(ruledHtml));
            Assert.DoesNotContain(ControlWords.Pause, RequestSteps.ReadControls(ruledHtml).Handlers);

            var restat = await RequestSteps.NoticeOfAsync(await RequestSteps.PostControlAsync(owner, await RequestSteps.ControlsAsync(owner), ControlWords.Restart,
                ("confirm", "restat")));
            Assert.Contains(RequestWords.NotConfirmed("restart"), await TextAsync(await owner.Client.GetAsync(restat, Token)));

            factory.Clock.Advance(TimeSpan.FromMinutes(16));
            var stale = await owner.Client.GetAsync(restat, Token);
            var staleHtml = await stale.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
            Assert.Contains(RequestWords.NotConfirmedNotOffered, Text(staleHtml));
            Assert.DoesNotContain("in the box", Text(staleHtml));
            Assert.DoesNotContain(ControlWords.Restart, RequestSteps.ReadControls(staleHtml).Handlers);
            Assert.Contains(WebUtility.HtmlEncode(FreshSignIn.SignInAgainLink("/admin/bot/controls#restart")), staleHtml);
            Assert.Single(await db.SentAsync());
        }

        // The pause and the status as the bot follows them: a pause with its reason, hidden characters as their codes, and
        // who paused, and a stored status text the rules refuse shown as the default with the rule, never the text. A text
        // the rules pass is shown, and so is the last resume.
        [Fact]
        public async Task PausedStateAndStatusTextFollowTheBotsRule()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.119");
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var bot = db.Requests();

            var pause = await RequestSteps.AddAsync(db, factory, RequestKinds.PostsPause, now.AddMinutes(-5));
            var status = await RequestSteps.AddAsync(db, factory, RequestKinds.StatusSet, now.AddMinutes(-4), argument: "Mining at night");
            await bot.SavePause(true, "Testing\u200B the pause", now.AddMinutes(-5), TestUsers.Owner.Id, pause, now.AddMinutes(-5));
            await bot.SaveStatus("Join x.com/minerbot", now.AddMinutes(-4), TestUsers.Owner.Id, status, now.AddMinutes(-4));

            var html = await owner.Client.GetStringAsync("/admin/bot/controls", Token);
            var text = Text(html);

            Assert.Contains($"Posts are paused, since {TimeWords.Utc(now.AddMinutes(-5))}, on request {pause}, by Test Owner. The reason given: Testing[U+200B] the pause.", text);
            Assert.Contains("Discord shows the default status, Playing Murder Miners on my PC, because the stored status text breaks the rules (the status text has a link).", text);
            Assert.DoesNotContain("x.com", html);
            PageTests.AssertAccessible("the controls page with a pause and a status text the rules refuse", html);

            await bot.SaveStatus("Mining at night", now.AddMinutes(-3), TestUsers.Owner.Id, status, now.AddMinutes(-3));
            await bot.SavePause(false, null, now.AddMinutes(-2), TestUsers.Owner.Id, pause, now.AddMinutes(-2));
            text = Text(await owner.Client.GetStringAsync("/admin/bot/controls", Token));

            Assert.Contains($"Discord shows the status text Mining at night, set at {TimeWords.Utc(now.AddMinutes(-3))}, on request {status}.", text);
            Assert.Contains($"Posts aren't paused. They were last resumed at {TimeWords.Utc(now.AddMinutes(-2))}, on request {pause}.", text);
        }

        // A row holding a time MySql.Data can't read never takes the page down: its last use reads as unreadable, a waiting
        // one still counts toward the bot's limit, and one the bot took whose time of taking can't be read can't be followed.
        // Each row's own page opens too. The waiting rows are written as the dashboard's own login, which MariaDB's default
        // mode lets add both times; the time of taking is the bot's own column, written by hand. Fails if the guard is gone.
        [Fact]
        public async Task AnUnreadableTimeDoesntBreakThePage()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.120");
            var rows = new List<ulong>();

            await DashboardLogin.AsDashboardAsync(db, async password =>
            {
                await using var connection = new MySqlConnection(DashboardLogin.ConnectionAs(db, password, MariaDbFixture.BotDatabase));
                await connection.OpenAsync(Token);

                foreach (var (kind, time) in new[] { (RequestKinds.CachesClear, "0000-00-00 00:00:00.000"), (RequestKinds.BotRestart, "2027-00-10 00:00:00.000") })
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        $@"INSERT INTO {MariaDbFixture.RequestTable} (request_key, kind, actor_id, actor_name, actor_role, created_at, expires_at, signature)
                           VALUES (@key, @kind, @actor, 'Test Owner', 'owner', @time, @time, REPEAT('s', 32))",
                        new { key = RequestFields.NewKey(), kind, actor = TestUsers.Owner.Id, time }, cancellationToken: Token));
                    rows.Add(await connection.ExecuteScalarAsync<ulong>(new CommandDefinition("SELECT LAST_INSERT_ID()", cancellationToken: Token)));
                }
            });

            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var colours = await RequestSteps.TakenAsync(db, factory, RequestKinds.JobColors, now);
            await db.RootExecuteAsync($"UPDATE {MariaDbFixture.RequestTable} SET claimed_at = '2027-00-10 00:00:00.000' WHERE id = @colours", new { colours });
            rows.Add(colours);

            var response = await owner.Client.GetAsync("/admin/bot/controls", Token);
            var html = await response.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains($"Last asked for by Test Owner (Owner) at an unreadable time: Can't follow. Request {rows[0]}.", Control(html, "Clear the caches"));
            Assert.Contains($"A request like this is waiting: request {rows[1]}.", Control(html, "Restart the bot"));
            Assert.Contains($"Last asked for by Test Owner (Owner) at {TimeWords.Utc(now)}: Can't follow. Request {colours}.", Control(html, "Run the colour job now"));

            foreach (var id in rows)
                Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync($"/admin/requests/{id}", Token)).StatusCode);
        }

        // Each control's line says whether one more would be sent now, as the sender would answer: available, the bot's limit
        // with when it takes another, a request like it that waits, or the person's own limit with when they may ask again
        [Fact]
        public async Task TheAvailabilityLineSaysWhen()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.121");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            await RequestSteps.TakenAsync(db, factory, RequestKinds.BotRestart, now.AddMinutes(-2).AddSeconds(-1));
            var renewal = await RequestSteps.AddAsync(db, factory, RequestKinds.SteamRenew, now.AddSeconds(-10));
            await RequestSteps.AddRefusedAsync(db, RequestKinds.CachesClear, TestUsers.Owner, Enumerable.Range(1, PersonLimits.Share(RequestKinds.Find(RequestKinds.CachesClear)!)!.Value)
                .Select(minute => now.AddMinutes(-50 + minute)));

            var html = await owner.Client.GetStringAsync("/admin/bot/controls", Token);

            Assert.Contains($"The bot takes this again after {RequestWords.Clock(now.AddMinutes(8))}.", Control(html, "Restart the bot"));
            Assert.Contains($"A request like this is waiting: request {renewal}.", Control(html, "Renew the Steam token"));
            Assert.Contains($"<a href=\"/admin/requests/{renewal}\">request {renewal}</a>", html);
            Assert.Contains($"You can ask for this again after {RequestWords.Clock(now.AddMinutes(11))}.", Control(html, "Clear the caches"));
            Assert.Contains("Available now.", Control(html, "Run the notification job now"));
        }

        // Where the page's own reads show the bot refusing a control now, by the bot's own rules, its line says so rather than
        // "Available now.", and the button stays: the notification job while posts are paused, for an Admin too, and a restart
        // in a run's first 5 minutes. Fails if the line says it's available beside the reason the bot refuses it.
        [Fact]
        public async Task TheAvailabilityLineSaysWhatTheBotRefuses()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.123");
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.124");

            var status = StatusSteps.Status(now.AddSeconds(-10));
            status.started_at = now.AddMinutes(-2);
            await db.Status().SaveStatus(status);
            var pause = await RequestSteps.AddAsync(db, factory, RequestKinds.PostsPause, now.AddMinutes(-1));
            await db.Requests().SavePause(true, "Testing the pause", now.AddMinutes(-1), TestUsers.Owner.Id, pause, now.AddMinutes(-1));

            var html = await owner.Client.GetStringAsync("/admin/bot/controls", Token);
            var notifications = Control(html, "Run the notification job now");
            var restart = Control(html, "Restart the bot");

            Assert.Contains(ControlWords.RefusedWhilePaused, notifications);
            Assert.Contains($"The bot takes a restart from {RequestWords.Clock(now.AddMinutes(3))}.", restart);
            Assert.DoesNotContain("Available now.", notifications + restart);
            Assert.Contains("Available now.", Control(html, "Clear the caches"));
            Assert.Matches("<form[^>]*\\?handler=RunNotifications", html);
            Assert.Matches("<form[^>]*\\?handler=Restart\"", html);

            var adminPage = await admin.Client.GetStringAsync("/admin/bot/controls", Token);
            Assert.Contains(ControlWords.RefusedWhilePaused, Control(adminPage, "Run the notification job now"));
            Assert.Matches("<form[^>]*\\?handler=RunNotifications", adminPage);

            // Five minutes after the start, and with posts resumed, both are available again
            await db.Requests().SavePause(false, null, now, TestUsers.Owner.Id, pause, now);
            factory.Clock.Advance(TimeSpan.FromMinutes(3).Add(TimeSpan.FromSeconds(1)));
            status.heartbeat_at = factory.Clock.GetUtcNow().UtcDateTime.AddSeconds(-10);
            await db.Status().SaveStatus(status);
            var later = await owner.Client.GetStringAsync("/admin/bot/controls", Token);
            Assert.Contains("Available now.", Control(later, "Run the notification job now"));
            Assert.Contains("Available now.", Control(later, "Restart the bot"));
        }

        // The words say the bot takes a restart from 5 minutes after it starts, which is the bot's own rule, read from its
        // source so the two can't drift, and that a notification run the time limit cut off doesn't stop it, as the bot's
        // JobHandler.Runs leaves such a run out, whether or not the bot has checked in yet
        [Fact]
        public void RestartRuleMatchesTheBot()
        {
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "bot-source", "RequestActions.cs"));
            var rule = UptimeRule().Match(source);

            Assert.True(rule.Success, "The bot's RequestActions.cs has no MinimumUptime in minutes");
            Assert.Equal(TimeSpan.FromMinutes(int.Parse(rule.Groups[1].Value, CultureInfo.InvariantCulture)), ControlWords.RestartAfterStart);

            Assert.Equal("This run started at 2026-11-21 10:00:00 UTC, so the bot takes a restart from 2026-11-21 10:05:00 UTC. It doesn't restart while a notification run is under way, "
                + "unless the time limit cut that run off.",
                ControlWords.RestartWhen(new DataAccessLibrary.Status.Models.BotStatusRow { started_at = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc) }));
            Assert.Equal("MinerBot hasn't checked in, so the dashboard can't tell when its run started. The bot takes a restart from 5 minutes after it starts, and it "
                + "doesn't restart while a notification run is under way, unless the time limit cut that run off.",
                ControlWords.RestartWhen(null));
        }

        // The bot records a job's run when it ends or when the time limit cuts it off, so a run with no end is one that was cut
        // off, which the Bot page marks as a problem: the controls page says so in JobWords' words, never that it's running
        // now, and once the bot has started again since that run began, as the restart that cures one does, or stopped cleanly
        // since it began and not started again, as docker stop leaves it, that it stopped with the bot. Fails if a cut-off run
        // reads as an ordinary run under way, or as one that may still run after a restart or while the bot is stopped.
        [Fact]
        public void ARunTheTimeLimitCutOffReadsAsCutOff()
        {
            var started = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
            var row = new DataAccessLibrary.Status.Models.JobStatusRow
            {
                job = JobWords.Notifications, schedule = "*/5 * * * *", last_started_at = started, last_result = DataAccessLibrary.Status.JobResult.CutOff,
                last_detail = "still running when the time limit of 270 s cut it off", updated_at = started.AddSeconds(270)
            };
            var running = new DataAccessLibrary.Status.Models.BotStatusRow { started_at = started.AddHours(-1) };
            var restarted = new DataAccessLibrary.Status.Models.BotStatusRow { started_at = started.AddMinutes(6) };
            var stopped = new DataAccessLibrary.Status.Models.BotStatusRow { started_at = started.AddHours(-1), heartbeat_at = started.AddMinutes(10), stopped_at = started.AddMinutes(10) };

            // A stop recorded before the run began was an earlier process's, so the run is a later one's, which may still be running
            var stoppedBefore = new DataAccessLibrary.Status.Models.BotStatusRow { started_at = started.AddHours(-1), heartbeat_at = started.AddMinutes(-1), stopped_at = started.AddMinutes(-1) };

            Assert.Equal("Notifications: last run Cut off, started at 2026-11-21 10:00:00 UTC, with no end recorded yet: it may still be running.",
                ControlWords.LastRun(JobWords.Notifications, row, running));
            Assert.Equal(ControlWords.LastRun(JobWords.Notifications, row, running), ControlWords.LastRun(JobWords.Notifications, row, null));
            Assert.Equal(ControlWords.LastRun(JobWords.Notifications, row, running), ControlWords.LastRun(JobWords.Notifications, row, stoppedBefore));
            Assert.Equal("Notifications: last run Cut off, started at 2026-11-21 10:00:00 UTC, with no end recorded: it stopped with the bot, which started again at "
                + "2026-11-21 10:06:00 UTC.", ControlWords.LastRun(JobWords.Notifications, row, restarted));
            Assert.Equal("Notifications: last run Cut off, started at 2026-11-21 10:00:00 UTC, with no end recorded: it stopped with the bot, at 2026-11-21 10:10:00 UTC.",
                ControlWords.LastRun(JobWords.Notifications, row, stopped));

            (row.last_ended_at, row.last_duration_ms) = (started.AddSeconds(300), 300000u);
            Assert.Equal("Notifications: last run Cut off, started at 2026-11-21 10:00:00 UTC, took 5 min 0 s.", ControlWords.LastRun(JobWords.Notifications, row, restarted));
        }

        // On the page, a cut-off notification run from before the bot's current run reads as stopped with the bot, from the
        // status row the page reads for its box, which the bot keeps after a restart until that job's next run is recorded.
        // Fails if the page doesn't give the job's line the bot's run.
        [Fact]
        public async Task ACutOffRunFromBeforeARestartReadsAsStopped()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.235");

            var status = StatusSteps.Status(now.AddSeconds(-10));
            status.started_at = now.AddMinutes(-2);
            await db.Status().SaveStatus(status);
            await db.Status().SaveJobStatus(
            [
                new DataAccessLibrary.Status.Models.JobStatus
                {
                    job = JobWords.Notifications, schedule = "*/5 * * * *", next_run_at = now.AddMinutes(3), last_started_at = now.AddMinutes(-7),
                    last_result = DataAccessLibrary.Status.JobResult.CutOff, last_detail = "still running when the time limit of 270 s cut it off", updated_at = now.AddMinutes(-3)
                }
            ]);

            var line = Control(await owner.Client.GetStringAsync("/admin/bot/controls", Token), "Run the notification job now");
            Assert.Contains($"Notifications: last run Cut off, started at {TimeWords.Utc(now.AddMinutes(-7))}, with no end recorded: it stopped with the bot, which started "
                + $"again at {TimeWords.Utc(now.AddMinutes(-2))}.", line);
        }

        // What a pause holds lives only in the bot's memory, so a restart loses it, as the README says: the pause's words, the
        // paused banner and the armor page's line in place of Announce now say so, and the restart's say what to do first while
        // posts are paused, since the bot refuses the notification job then. The restart's also say it loses what came since
        // the bot's last notification run, which the first run after a start takes as seen without posting it. Fails if they
        // promise that what waits goes out whatever happens.
        [Fact]
        public void ThePauseAndTheRestartSayARestartLosesWhatWaits()
        {
            Assert.Contains("goes out when posts are resumed, unless the bot restarts first, which loses it.", ControlWords.Of(ControlWords.Pause).Help);
            Assert.Contains("the bot posts this armor once they're resumed, unless the bot restarts first, which loses it.", ArmorWords.AnnouncePaused);
            Assert.Contains("those a pause holds included, so if any wait, resume posts if they're paused and run the notification job first.",
                ControlWords.Of(ControlWords.Restart).Help);
            Assert.Contains("A restart loses the armor, maps and lobbies the bot hasn't posted yet, those that came since its last notification run and",
                ControlWords.Of(ControlWords.Restart).Help);

            var settings = new DataAccessLibrary.Requests.Models.BotSettingsRow { posts_paused = true, updated_at = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc) };
            var facts = new BannerFacts(true, settings, null, null, null, null, null, null);

            foreach (var role in new[] { DashboardRole.Owner, DashboardRole.Admin })
                Assert.Contains("goes out when posts are resumed, unless the bot restarts first, which loses it.",
                    Assert.Single(RequestBanners.Of(facts, role, settings.updated_at)).Text);
        }

        // A reason and a status text someone typed can be one long word or a link, so the banners and the controls page's
        // lines about the pause and the status text break where they must, as names do, rather than widen a phone's page.
        // Fails if either loses its overflow-wrap.
        [Fact]
        public async Task TypedTextBreaksWhereItMust()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.125");

            var css = await factory.Visitor().Client.GetStringAsync("/css/site.css", Token);

            string Rule(string selector) => Regex.Match(css, @"\n" + selector + @" \{[^}]*\}").Value;

            Assert.Contains("overflow-wrap: anywhere;", Rule(@"\.notice p"));
            Assert.Contains("overflow-wrap: anywhere;", Rule(@"\.settings-line"));

            var html = await owner.Client.GetStringAsync("/admin/bot/controls", Token);
            Assert.Contains("<p class=\"settings-line\">Posts aren&#x27;t paused.", html);
            Assert.Contains("<p class=\"settings-line\">Discord shows the default status", html);
        }

        // ?request= draws the box only for one of the page's own kinds, whoever asked for it; another kind's request, or a
        // value that isn't a request's number, draws nothing. Fails if the parameter shows whatever it names.
        [Fact]
        public async Task TheBoxShowsOnlyTheControlsOwnRequests()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.122");
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var restart = await RequestSteps.AddAsync(db, factory, RequestKinds.BotRestart, now);
            var test = await RequestSteps.AddAsync(db, factory, RequestKinds.GuildTestMessage, now);

            var shown = await admin.Client.GetStringAsync($"/admin/bot/controls?request={restart}", Token);
            Assert.Contains($"Restart the bot, request {restart}, asked for by Test Owner (Owner)", Text(shown));
            Assert.Contains($"data-refresh=\"/admin/requests/{restart}?handler=result\"", shown);
            Assert.Contains($"<a href=\"/admin/bot/controls?request={restart}&amp;again=1#result\">Check again</a>", shown);
            PageTests.AssertAccessible("the controls page with a request's box", shown);

            // Opened from Check again, in any letter case, with any value or none, its Check again takes again off
            foreach (var again in new[] { "again=1", "Again=", "AGAIN" })
                Assert.Contains($"<a href=\"/admin/bot/controls?request={restart}#result\">Check again</a>",
                    await admin.Client.GetStringAsync($"/admin/bot/controls?request={restart}&{again}", Token));

            foreach (var other in new[] { $"{test}", $"{restart}x", "0", "-1", "abc", "99999" })
                Assert.DoesNotContain("id=\"result\"", await admin.Client.GetStringAsync($"/admin/bot/controls?request={other}", Token));
        }

        // Check again, the next look at a form's request without the script, loads the page each time it's pressed: from the
        // address the form's redirect leads to, and from the one Check again leads to, whose own leads back. Fails if it's the
        // address the browser shows with a fragment, which only scrolls, as it was before.
        [Fact]
        public async Task CheckAgainLoadsThePageAgain()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.133");

            await RequestSteps.CheckAgainWalkAsync(admin, await RequestSteps.PostControlAsync(admin, await RequestSteps.ControlsAsync(admin), ControlWords.ClearCaches));
        }

        [GeneratedRegex(@"MinimumUptime = TimeSpan\.FromMinutes\(([0-9]+)\);")]
        private static partial Regex UptimeRule();
    }
}
