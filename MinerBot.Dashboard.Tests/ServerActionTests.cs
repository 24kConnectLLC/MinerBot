using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Servers;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Tests
{
    // A server page's actions: a test message only to the channels the server chose for MinerBot's posts that can take an
    // embed, checked again at the POST, a refresh of the copy, each one's last use for that server and the box of the request
    // a form sent, none of it for a server the bot isn't in or Discord doesn't have, and the Owner's own page for leaving,
    // which takes the server's name as it reads, or its ID, and a sign-in from the last 15 minutes
    [Collection("Database")]
    public partial class ServerActionTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        // A: in, with every kind of chosen channel. B: in, in an outage. C: left. D: in, another server whose channel A's
        // settings may name. E: settings only.
        const ulong A = 1000000000000000901, B = 1000000000000000902, C = 1000000000000000903, D = 1000000000000000904, E = 1000000000000000905;

        // A's channels: general takes a test message, news is an announcements channel without Embed Links, no-embed a text
        // channel without it, postable one the server didn't choose, and a hidden one. D's own channel.
        const ulong General = 101, News = 102, NoEmbed = 103, Postable = 104, Hidden = 105, Elsewhere = 201;

        static readonly DateTime At = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

        static async Task<TestBrowser> SignedInAsync(DashboardFactory factory, TestUser user, string address)
        {
            var browser = factory.Visitor(address);
            await SignInSteps.SignedInAsync(factory, browser, user);
            return browser;
        }

        // A page's text without its markup, so a sentence reads as one string, a link's full stop included
        static string Text(string html) => Regex.Replace(Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(html), "<[^>]+>", " "), "\\s+", " "), " ([.,:])(?= |$)", "$1");

        // The same with the markup dropped where it is, so a channel's # stays beside the name its bdi holds
        static string Plain(string html) => Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(html), "<[^>]+>", ""), "\\s+", " ");

        static string Page(ulong id) => "/admin/servers/" + id.ToString(CultureInfo.InvariantCulture);

        // The servers as the bot's code writes them, and A's settings: general for armor and maps, no-embed for lobbies and
        // news for announcements, unless the test gives its own
        static async Task SeedAsync(MariaDbFixture db, Guild? settings = null, string serverName = "Test Server", string generalName = "general")
        {
            var guilds = db.BotGuilds();

            await guilds.SaveGuild(StatusSteps.Server(A, serverName),
            [
                StatusSteps.Channel(General, generalName, 0),
                StatusSteps.Channel(News, "news", 1, GuildChannelType.Announcement, embed: false),
                StatusSteps.Channel(NoEmbed, "no-embed", 2, embed: false),
                StatusSteps.Channel(Postable, "postable", 3),
                StatusSteps.Channel(Hidden, "___hidden___", 4, view: false, send: false, embed: false)
            ], At, StatusSteps.RunId, 1);
            await guilds.SaveGuild(StatusSteps.Server(B, "Outage Server"), [StatusSteps.Channel(301, "general", 0)], At, StatusSteps.RunId, 2);
            await guilds.SaveGuild(StatusSteps.Server(C, "Gone Server"), [StatusSteps.Channel(401, "general", 0)], At, StatusSteps.RunId, 3);
            await guilds.SaveGuild(StatusSteps.Server(D, "Other Server"), [StatusSteps.Channel(Elsewhere, "general", 0)], At, StatusSteps.RunId, 4);
            await guilds.MarkUnavailable([B], At.AddHours(1));
            await guilds.LeaveGuild(C, At.AddHours(2), StatusSteps.RunId, 5, null);

            var website = db.Guilds();
            await website.SaveGuild(settings ?? new Guild { guild_id = A, armor_channel_id = General, map_channel_id = General, lobby_channel_id = NoEmbed, announcements_channel_id = News });
            await website.SaveGuild(new Guild { guild_id = B, armor_channel_id = 301 });
            await website.SaveGuild(new Guild { guild_id = C, armor_channel_id = 401 });
            await website.SaveGuild(new Guild { guild_id = D, armor_channel_id = Elsewhere });
            await website.SaveGuild(new Guild { guild_id = E, armor_channel_id = 501 });
        }

        // The test message's options, as value and text
        static List<(string Value, string Text)> Options(string html) =>
            [.. Regex.Matches(html, "<option value=\"([0-9]+)\"[^>]*>([^<]*)</option>").Select(match => (match.Groups[1].Value, WebUtility.HtmlDecode(match.Groups[2].Value)))];

        // A form's key, by the address its action names
        static string? Key(string html, string action)
        {
            var match = Regex.Match(html, $"action=\"{Regex.Escape(action)}\"[^>]*>\\s*<input type=\"hidden\" name=\"formkey\" value=\"([0-9a-f]{{32}})\" />");
            return match.Success ? match.Groups[1].Value : null;
        }

        // A form of a server's page or its leave page, as a browser sends it from the dashboard, with the key and token the
        // page drew it with, or a new key for a form the page didn't draw
        static async Task<HttpResponseMessage> PostAsync(TestBrowser browser, string page, string action, params (string Name, string Value)[] fields)
        {
            var html = await browser.Client.GetStringAsync(page, Token);
            var sent = new List<KeyValuePair<string, string>> { new("formkey", Key(html, action) ?? RequestFields.NewKey()) };
            sent.AddRange(fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));

            return await SignInSteps.PostAsync(browser, action, SignInSteps.FormToken(html), origin: DashboardFactory.PublicOrigin, fields: sent);
        }

        static void SentTo(HttpResponseMessage response, ulong server, ulong id)
        {
            Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"The form answered {response.StatusCode}");
            Assert.Equal($"{Page(server)}?request={id}#result", SignInSteps.PathOf(response.Headers.Location));
        }

        // Of the channels the server chose, only one of its own where the bot can view, send and embed links is offered, each
        // once with every kind it was chosen for; a chosen announcements channel without Embed Links, a text channel without
        // it and a channel the copy doesn't have are listed with why; a channel the bot can post in that the server didn't
        // choose isn't offered. Fails if the picker offers every channel the bot can post in, or an announcements channel
        // without Embed Links.
        [Fact]
        public async Task TheTestMessageOffersTheServersOwnSetChannelsThatCanTakeIt()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            await SeedAsync(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.140");

            var html = await admin.Client.GetStringAsync(Page(A), Token);
            var text = Text(html);

            Assert.Equal([(General.ToString(CultureInfo.InvariantCulture), "Armor and maps: #general")], Options(html));
            Assert.Contains("Lobbies: #no-embed can't take the test message: MinerBot is missing Embed Links there.", Plain(html));
            Assert.Contains("Announcements: #news can't take the test message: it needs Embed Links, which plain-text announcements don't.", Plain(html));
            Assert.Contains(ServerActions.TestMessageHelp, text);
            Assert.Contains("At most 3 times every 10 minutes, for each server.", text);
            Assert.Contains(RequestWords.NotAskedFor, text);
            Assert.Contains("Available now.", text);
            Assert.NotNull(Key(html, Page(A) + "?handler=TestMessage"));
            PageTests.AssertAccessible("a server's page with its actions", html);

            // A hidden channel, another server's and one the copy doesn't have, each chosen: listed, never offered
            await db.Guilds().SaveGuild(new Guild { guild_id = A, armor_channel_id = Hidden, map_channel_id = Elsewhere, lobby_channel_id = 999, announcements_channel_id = 0 });
            var none = await admin.Client.GetStringAsync(Page(A), Token);
            var noneText = Text(none);

            Assert.Empty(Options(none));
            Assert.Contains(ServerActions.NoneOffered, noneText);
            Assert.Contains($"Armor: channel {Hidden}, hidden from MinerBot, can't take the test message: MinerBot is missing View Channel, Send Messages and Embed Links there.", noneText);
            Assert.Contains($"Maps: channel {Elsewhere} can't take the test message: it isn't one of this server's text or announcement channels in MinerBot's copy.", noneText);
            Assert.Contains("Lobbies: channel 999 can't take the test message: it isn't one of this server's text or announcement channels in MinerBot's copy.", noneText);
            Assert.DoesNotContain("?handler=TestMessage", none);
            PageTests.AssertAccessible("a server's page whose chosen channels can't take a test message", none);

            // Every kind turned off, and no settings at all
            await db.Guilds().SaveGuild(new Guild { guild_id = A });
            Assert.Contains(ServerActions.NoChannels, Text(await admin.Client.GetStringAsync(Page(A), Token)));
            await db.Guilds().DeleteGuild(A);
            Assert.Contains(ServerActions.NoChannels, Text(await admin.Client.GetStringAsync(Page(A), Token)));
        }

        // A channel that's one of the copy's but not one of the server's choices is a form someone made up, a 400; a choice
        // that can't take it now, another server's channel or one without Embed Links, is refused with why and the picker as
        // it is; nothing is sent either way. One that can is sent, with the server and the channel. Fails if the POST doesn't
        // check the channel again.
        [Fact]
        public async Task ATestMessageToAChannelNotOfferedIsRefused()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            await SeedAsync(db, new Guild { guild_id = A, armor_channel_id = General, map_channel_id = Elsewhere, lobby_channel_id = NoEmbed, announcements_channel_id = News });
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.141");
            var action = Page(A) + "?handler=TestMessage";

            foreach (var made in new[] { Postable.ToString(CultureInfo.InvariantCulture), "0", "-1", "abc", "", "18446744073709551616" })
                Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(owner, Page(A), action, ("channel", made))).StatusCode);

            var refusals = new (ulong Channel, string Why)[]
            {
                (Elsewhere, "it isn't one of this server's text or announcement channels in MinerBot's copy"),
                (NoEmbed, "MinerBot is missing Embed Links there"),
                (News, ServerActions.NeedsEmbed)
            };

            foreach (var (channel, why) in refusals)
            {
                var refused = await PostAsync(owner, Page(A), action, ("channel", channel.ToString(CultureInfo.InvariantCulture)));
                var html = await refused.Content.ReadAsStringAsync(Token);

                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                Assert.Contains($"That channel can't take a test message now: {why}. Nothing was sent.", Text(html));
                Assert.Contains("<div class=\"notice notice-problem\" role=\"alert\">", html);
                Assert.Equal([(General.ToString(CultureInfo.InvariantCulture), "Armor: #general")], Options(html));
                PageTests.AssertAccessible("a test message refused for its channel", html);
            }

            Assert.Empty(await db.SentAsync());

            var sent = await PostAsync(owner, Page(A), action, ("channel", General.ToString(CultureInfo.InvariantCulture)));
            var row = Assert.Single(await db.SentAsync());

            SentTo(sent, A, row.id);
            Assert.Equal((RequestKinds.GuildTestMessage, (ulong?)A, (ulong?)General, TestUsers.Owner.Id), (row.kind, row.guild_id, row.channel_id, row.actor_id));
        }

        // An option holds text alone, so a channel's name there has its hidden and direction-changing characters as their
        // codes, and no markup. Fails if the label goes through RevealedText's markup.
        [Fact]
        public async Task OptionsShowHiddenCharactersAsCodes()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            await SeedAsync(db, generalName: "gen\u202Eeral<b>");
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.142");

            var html = await admin.Client.GetStringAsync(Page(A), Token);
            var option = Regex.Match(html, "<option value=\"101\"[^>]*>(.*?)</option>", RegexOptions.Singleline);

            Assert.True(option.Success, "The page offers no option for the channel");
            Assert.DoesNotContain("<", option.Groups[1].Value);
            Assert.Equal("Armor and maps: #gen[U+202E]eral<b>", WebUtility.HtmlDecode(option.Groups[1].Value));
            Assert.DoesNotContain("\u202E", html, StringComparison.Ordinal);
        }

        // Rewrite the copy says what it can and can't mend, sends the server's refresh, and its box says to reload the page
        // once the bot has done it
        [Fact]
        public async Task RefreshSendsAndSaysItRewritesTheCopy()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            await SeedAsync(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.143");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            Assert.Contains(ServerActions.RefreshHelp, Text(await admin.Client.GetStringAsync(Page(A), Token)));

            var sent = await PostAsync(admin, Page(A), Page(A) + "?handler=Refresh");
            var row = Assert.Single(await db.SentAsync());

            SentTo(sent, A, row.id);
            Assert.Equal((RequestKinds.GuildRefresh, (ulong?)A, (ulong?)null, RequestRoles.Admin), (row.kind, row.guild_id, row.channel_id, row.actor_role));

            var queued = await admin.Client.GetStringAsync($"{Page(A)}?request={row.id}", Token);
            Assert.Contains($"Refresh a server's channels, request {row.id}, asked for by Test Admin (Admin)", Text(queued));
            Assert.Contains($"data-refresh=\"/admin/requests/{row.id}?handler=result\"", queued);
            Assert.Contains($"<a href=\"{Page(A)}?request={row.id}#result\">Check again</a>", queued);
            Assert.True(PageTests.LoadsRefreshScript(queued), "The page with a box that isn't final doesn't load refresh.js");
            PageTests.AssertAccessible("a server's page with its request's box", queued);

            await RequestSteps.ClaimAsync(db, row.id, now.AddSeconds(1));
            await RequestSteps.FinishAsync(db, row.id, RequestStates.Done, "Rewrote the copy of this server and its channels at 12:00:02 UTC: 5 text and announcement channels.",
                now.AddSeconds(2));

            var done = await admin.Client.GetStringAsync($"{Page(A)}?request={row.id}", Token);
            Assert.Contains("Reload the page to see the new copy.", Text(done));
            Assert.DoesNotContain("data-refresh=\"/admin/requests/", done);
            Assert.False(PageTests.LoadsRefreshScript(done), "The page with a final box loads refresh.js");
            Assert.False(PageTests.LoadsRefreshScript(await admin.Client.GetStringAsync(Page(A), Token)), "A server's page without a box loads refresh.js");
        }

        // A server the website alone knows, one the bot left and one in an outage offer no action, each saying why, and a form
        // made up for any of them writes nothing; with requests off, nothing is offered either
        [Fact]
        public async Task ActionsNeedTheBotInTheServer()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            await SeedAsync(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.144");

            foreach (var (server, line) in new[] { (E, ServerActions.NotIn), (C, ServerActions.NotIn), (B, ServerActions.Unavailable) })
            {
                var html = await owner.Client.GetStringAsync(Page(server), Token);

                Assert.Contains(line, Text(html));
                Assert.DoesNotMatch("<form[^>]*\\?handler=", html);
                Assert.DoesNotContain("/leave", html);
                PageTests.AssertAccessible($"server {server}'s page", html);
            }

            foreach (var (server, channel) in new[] { (E, 501UL), (C, 401UL), (B, 301UL) })
            {
                var test = await PostAsync(owner, Page(server), Page(server) + "?handler=TestMessage", ("channel", channel.ToString(CultureInfo.InvariantCulture)));
                var refresh = await PostAsync(owner, Page(server), Page(server) + "?handler=Refresh");

                Assert.Equal(HttpStatusCode.Conflict, test.StatusCode);
                Assert.Equal(HttpStatusCode.Conflict, refresh.StatusCode);
                Assert.Contains("Nothing was sent.", Text(await refresh.Content.ReadAsStringAsync(Token)));
            }

            Assert.Empty(await db.SentAsync());

            await using var off = new DashboardFactory(db, new DashboardFactoryOptions { Settings = new Dictionary<string, string?> { ["Dashboard:RequestSigningKey"] = "" } });
            var offOwner = await SignedInAsync(off, TestUsers.Owner, "198.51.100.145");
            var offHtml = await offOwner.Client.GetStringAsync(Page(A), Token);

            Assert.Contains(ServerActions.RequestsOff, Text(offHtml));
            Assert.DoesNotMatch("<form[^>]*\\?handler=", offHtml);
            Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(offOwner, Page(A), Page(A) + "?handler=Refresh")).StatusCode);
            Assert.Empty(await db.SentAsync());
        }

        // Each action's last use is this server's own, a done test message linking to it in Discord, and ?request= shows the
        // box only for a test message, refresh or leave of this server. Fails if the box shows whatever the parameter names.
        [Fact]
        public async Task ThePageShowsOnlyThisServersRequests()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            await SeedAsync(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.146");
            var now = factory.Clock.GetUtcNow().UtcDateTime;

            var test = await RequestSteps.AddAsync(db, factory, RequestKinds.GuildTestMessage, now.AddMinutes(-5), guildId: A);
            await RequestSteps.ClaimAsync(db, test, now.AddMinutes(-5).AddSeconds(1));
            await RequestSteps.FinishAsync(db, test, RequestStates.Done, "Sent a test message.", now.AddMinutes(-5).AddSeconds(2), number: 1234567890123456789);
            var otherRefresh = await RequestSteps.AddAsync(db, factory, RequestKinds.GuildRefresh, now.AddMinutes(-4), guildId: D);
            var otherTest = await RequestSteps.AddAsync(db, factory, RequestKinds.GuildTestMessage, now.AddMinutes(-3), guildId: D);
            var clear = await RequestSteps.AddAsync(db, factory, RequestKinds.CachesClear, now.AddMinutes(-2));

            var html = await admin.Client.GetStringAsync(Page(A), Token);
            var text = Text(html);

            Assert.Contains($"Last asked for by Test Owner (Owner) at {Status.TimeWords.Utc(now.AddMinutes(-5))}: Done. Sent a test message. Open the test message in Discord. "
                + $"Request {test}.", text);
            Assert.Contains($"<a href=\"https://discord.com/channels/{A}/{RequestSteps.ChannelId}/1234567890123456789\">Open the test message in Discord</a>", html);
            Assert.Contains(RequestWords.NotAskedFor, text);
            Assert.DoesNotContain($"Request {otherTest}.", text);
            Assert.DoesNotContain($"Request {otherRefresh}.", text);

            var shown = await admin.Client.GetStringAsync($"{Page(A)}?request={test}", Token);
            Assert.Contains("id=\"result\"", shown);
            Assert.Contains($"Send a test message, request {test}, asked for by Test Owner (Owner)", Text(shown));

            foreach (var other in new[] { $"{otherRefresh}", $"{otherTest}", $"{clear}", $"{test}x", "0", "-1", "99999" })
                Assert.DoesNotContain("id=\"result\"", await admin.Client.GetStringAsync($"{Page(A)}?request={other}", Token));

            Assert.Contains("id=\"result\"", await admin.Client.GetStringAsync($"{Page(D)}?request={otherRefresh}", Token));
        }

        // The bot's limit counts each server apart for a test message: three waiting for A make a fourth to A a refusal that
        // names the one that waits, and one to D is still sent
        [Fact]
        public async Task PerServerLimitsCountEachServer()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            await SeedAsync(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.147");
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var waiting = new List<ulong>();

            for (var minute = 3; minute >= 1; minute--)
                waiting.Add(await RequestSteps.AddAsync(db, factory, RequestKinds.GuildTestMessage, now.AddSeconds(-10 * minute), guildId: A));

            Assert.Contains($"A request like this is waiting: request {waiting[0]}.", Text(await owner.Client.GetStringAsync(Page(A), Token)));

            var refused = await PostAsync(owner, Page(A), Page(A) + "?handler=TestMessage", ("channel", General.ToString(CultureInfo.InvariantCulture)));
            var html = await refused.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains($"A request like this is already waiting for the bot: request {waiting[0]}", Text(html));
            Assert.Contains($"<a href=\"/admin/requests/{waiting[0]}\">That request</a>", html);
            Assert.Equal(3, (await db.SentAsync()).Count);

            var other = await PostAsync(owner, Page(D), Page(D) + "?handler=TestMessage", ("channel", Elsewhere.ToString(CultureInfo.InvariantCulture)));
            SentTo(other, D, (await db.SentAsync())[^1].id);
            Assert.Equal(4, (await db.SentAsync()).Count);
        }

        // An Admin never reaches the leave page: a GET and a POST with a good token both end at /denied before any handler,
        // nothing is sent, and the server's page shows an Admin no link to it, where the Owner has one. Fails if the page
        // loses the Owner policy.
        [Fact]
        public async Task LeaveIsTheOwnersOnItsOwnPage()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            await SeedAsync(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.148");
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.149");
            var leave = Page(A) + "/leave";

            var get = await admin.Client.GetAsync(leave, Token);
            Assert.Equal(HttpStatusCode.Redirect, get.StatusCode);
            Assert.StartsWith("/denied", SignInSteps.PathOf(get.Headers.Location));

            var token = SignInSteps.FormToken(await admin.Client.GetStringAsync(Page(A), Token));
            var post = await SignInSteps.PostAsync(admin, leave, token, origin: DashboardFactory.PublicOrigin,
                fields: [new("formkey", RequestFields.NewKey()), new("confirm", A.ToString(CultureInfo.InvariantCulture))]);
            Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
            Assert.StartsWith("/denied", SignInSteps.PathOf(post.Headers.Location));
            Assert.Empty(await db.SentAsync());

            Assert.DoesNotContain(leave, await admin.Client.GetStringAsync(Page(A), Token));
            Assert.Contains($"<a href=\"{leave}\">Leave this server&hellip;</a>", await owner.Client.GetStringAsync(Page(A), Token));
            Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync(leave, Token)).StatusCode);
        }

        // A leave is sent for the server's name as it reads, without its hidden characters, in any case and with any spacing,
        // or as the page shows it, with their codes, as copying it from the page gives it, or for its ID; a name that reads
        // otherwise is refused, writing nothing. While the sign-in is older than 15 minutes the page draws the way to sign in
        // again and no form, and a form sent anyway is refused. Fails if the name is compared as typed.
        [Fact]
        public async Task LeaveNeedsAFreshSignInAndTheNameOrId()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            await SeedAsync(db, serverName: "Test\u200B Server");
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.150");
            var leave = Page(A) + "/leave";

            var shown = Plain(Regex.Match(await owner.Client.GetStringAsync(leave, Token), "MinerBot leaves this server: (<bdi class=\"revealed\">.*?</bdi>)").Groups[1].Value);
            Assert.Equal("Test[U+200B] Server", shown);

            foreach (var typed in new[] { "Test\u200B Server", "test server", "  TEST   SERVER ", shown, A.ToString(CultureInfo.InvariantCulture) })
            {
                var sent = await PostAsync(owner, leave, leave, ("confirm", typed));
                var row = Assert.Single(await db.SentAsync());

                SentTo(sent, A, row.id);
                Assert.Equal((RequestKinds.GuildLeave, (ulong?)A, RequestRoles.Owner), (row.kind, row.guild_id, row.actor_role));
                await db.ClearAsync(MariaDbFixture.RequestTable);
            }

            foreach (var typed in new[] { "TestServer", "Another", "" })
            {
                var refused = await PostAsync(owner, leave, leave, ("confirm", typed));
                var html = await refused.Content.ReadAsStringAsync(Token);

                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
                Assert.Contains(RequestWords.NotConfirmedServer, Text(html));
                Assert.DoesNotContain("value=\"" + typed + "\"", html.Replace("value=\"\"", ""));
                PageTests.AssertAccessible("a leave refused for what was typed", html);
            }

            Assert.Empty(await db.SentAsync());

            factory.Clock.Advance(TimeSpan.FromMinutes(16));
            var stale = await owner.Client.GetStringAsync(leave, Token);

            Assert.Null(Key(stale, leave));
            Assert.Contains(WebUtility.HtmlEncode(FreshSignIn.SignInAgainLink(leave)), stale);
            PageTests.AssertAccessible("the leave page while the sign-in isn't fresh", stale);

            var token = SignInSteps.FormToken(stale);
            var old = await SignInSteps.PostAsync(owner, leave, token, origin: DashboardFactory.PublicOrigin,
                fields: [new("formkey", RequestFields.NewKey()), new("confirm", A.ToString(CultureInfo.InvariantCulture))]);

            Assert.Equal(HttpStatusCode.Forbidden, old.StatusCode);
            Assert.Contains("This needs a sign-in from the last 15 minutes", Text(await old.Content.ReadAsStringAsync(Token)));
            Assert.Empty(await db.SentAsync());
        }

        // The leave page says in full what a leave deletes, names the server and its ID, and offers the form with its box and
        // its button; a server the bot left, or one it never was in, offers nothing to leave
        [Fact]
        public async Task TheLeavePageSaysWhatALeaveDeletes()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            await SeedAsync(db, serverName: "Test<b>Server");
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.151");
            var leave = Page(A) + "/leave";

            var html = await owner.Client.GetStringAsync(leave, Token);
            var text = Text(html);

            Assert.Contains($"<title>Leave server {A} - MinerBot dashboard</title>", html);
            Assert.Contains($"<h1>Leave server {A}</h1>", html);
            Assert.Contains("MinerBot leaves this server: <bdi class=\"revealed\">Test&lt;b&gt;Server</bdi>", html);
            Assert.Contains($"ID {A}, 120 members.", text);
            Assert.Contains(ServerActions.WhatALeaveDoes, text);
            Assert.Contains(ServerActions.TypeTheServer(A.ToString(CultureInfo.InvariantCulture)), text);
            Assert.Contains("<button type=\"submit\" class=\"button button-danger\">Leave the server</button>", html);
            Assert.NotNull(Key(html, leave));
            Assert.Contains("name=\"confirm\" autocomplete=\"off\" spellcheck=\"false\" required", html);
            PageTests.AssertAccessible("the leave page", html);

            foreach (var gone in new[] { C, E, 12345UL })
            {
                var page = await owner.Client.GetStringAsync(Page(gone) + "/leave", Token);

                Assert.Contains(ServerActions.NothingToLeave, Text(page));
                Assert.DoesNotContain("name=\"confirm\"", page);
                PageTests.AssertAccessible("the leave page of a server MinerBot isn't in", page);
            }

            var refused = await PostAsync(owner, Page(C) + "/leave", Page(C) + "/leave", ("confirm", C.ToString(CultureInfo.InvariantCulture)));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Empty(await db.SentAsync());
        }

        // With requests off no leave is offered, and a sign-in sends the owner no message, so the leave page offers no sign-in
        // either, fresh or not, as the controls page doesn't. Fails if it says a sign-in makes something work until a time,
        // or offers one again with the usual message.
        [Fact]
        public async Task WithRequestsOffTheLeavePageOffersNoSignIn()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions { Settings = new Dictionary<string, string?> { ["Dashboard:RequestSigningKey"] = "" } });
            await SeedAsync(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.154");
            var leave = Page(A) + "/leave";

            foreach (var minutes in new[] { 0, 16 })
            {
                factory.Clock.Advance(TimeSpan.FromMinutes(minutes));
                var html = await owner.Client.GetStringAsync(leave, Token);
                var text = Text(html);

                Assert.Contains(ServerActions.LeaveOff, text);
                Assert.Contains(ServerActions.WhatALeaveDoes, text);
                Assert.DoesNotContain("id=\"sign-in\"", html);
                Assert.DoesNotContain(ControlWords.SignInAgain, text);
                Assert.DoesNotContain("so these work until", text);
                Assert.Null(Key(html, leave));
                PageTests.AssertAccessible("the leave page with requests off", html);
            }
        }
    }
}
