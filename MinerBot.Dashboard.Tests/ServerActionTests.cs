using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // A channel that isn't a channel's ID is a form someone made up, a 400. One of the copy's that the server didn't
        // choose is refused as things stand, since the server could choose it later, and so is a choice that can't take it
        // now, another server's channel or one without Embed Links: each is a notice whose GET says why at 409, with the
        // picker as it is; nothing is sent either way. One that can is sent, with the server and the channel. Fails if the
        // POST doesn't check the channel again.
        [Fact]
        public async Task ATestMessageToAChannelNotOfferedIsRefused()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            await SeedAsync(db, new Guild { guild_id = A, armor_channel_id = General, map_channel_id = Elsewhere, lobby_channel_id = NoEmbed, announcements_channel_id = News });
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.141");
            var action = Page(A) + "?handler=TestMessage";

            foreach (var made in new[] { "0", "-1", "abc", "", "18446744073709551616" })
                Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(owner, Page(A), action, ("channel", made))).StatusCode);

            var notChosen = await RequestSteps.AnsweredAsync(owner, await PostAsync(owner, Page(A), action, ("channel", Postable.ToString(CultureInfo.InvariantCulture))));
            var notChosenHtml = await notChosen.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.Conflict, notChosen.StatusCode);
            Assert.Contains(ServerActions.NotChosenNow, Text(notChosenHtml));
            Assert.Equal([(General.ToString(CultureInfo.InvariantCulture), "Armor: #general")], Options(notChosenHtml));

            var refusals = new (ulong Channel, string Why)[]
            {
                (Elsewhere, "it isn't one of this server's text or announcement channels in MinerBot's copy"),
                (NoEmbed, "MinerBot is missing Embed Links there"),
                (News, ServerActions.NeedsEmbed)
            };

            foreach (var (channel, why) in refusals)
            {
                var refused = await RequestSteps.AnsweredAsync(owner, await PostAsync(owner, Page(A), action, ("channel", channel.ToString(CultureInfo.InvariantCulture))));
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

        // A test message drawn while the server chose general, sent after /admin set_channels moved its posts elsewhere, is
        // refused as things stand: its notice says so at 409, keeps no channel, and draws the picker as it is. Once the server
        // chooses general again, the notice's address still sends nothing, and a press on the form it drew sends once.
        // Fails if the stale channel is a 400 again, which a browser holds as a POST it can send later, or if the notice's
        // GET acts.
        [Fact]
        public async Task AStaleChannelIsANotice()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            await SeedAsync(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.155");
            var action = Page(A) + "?handler=TestMessage";
            var general = General.ToString(CultureInfo.InvariantCulture);
            var chosen = new Guild { guild_id = A, armor_channel_id = General, map_channel_id = General, lobby_channel_id = NoEmbed, announcements_channel_id = News };

            var drawn = await owner.Client.GetStringAsync(Page(A), Token);
            await db.Guilds().SaveGuild(new Guild { guild_id = A, armor_channel_id = Postable, map_channel_id = Postable, lobby_channel_id = NoEmbed, announcements_channel_id = News });

            var refused = await SignInSteps.PostAsync(owner, action, SignInSteps.FormToken(drawn), origin: DashboardFactory.PublicOrigin,
                fields: [new("formkey", Key(drawn, action)!), new("channel", general)]);
            var address = await RequestSteps.NoticeOfAsync(refused);
            var answer = await owner.Client.GetAsync(address, Token);
            var html = await answer.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
            Assert.Contains(ServerActions.NotChosenNow, Text(html));
            Assert.Equal([(Postable.ToString(CultureInfo.InvariantCulture), "Armor and maps: #postable")], Options(html));
            Assert.Null(RequestSteps.OpenNotice(factory, owner).Kept);
            PageTests.AssertAccessible("a test message to a channel the server doesn't choose now", html);

            // Chosen again: the notice's address is a page, never the form sent again
            await db.Guilds().SaveGuild(chosen);
            var again = await owner.Client.GetAsync(address, Token);
            var againHtml = await again.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Contains(ServerActions.NotChosenNow, Text(againHtml));
            Assert.Equal([(general, "Armor and maps: #general")], Options(againHtml));
            Assert.Empty(await db.SentAsync());

            var sent = await SignInSteps.PostAsync(owner, action, SignInSteps.FormToken(againHtml), origin: DashboardFactory.PublicOrigin,
                fields: [new("formkey", Key(againHtml, action)!), new("channel", general)]);
            var row = Assert.Single(await db.SentAsync());

            SentTo(sent, A, row.id);
            Assert.Equal((RequestKinds.GuildTestMessage, (ulong?)A, (ulong?)General), (row.kind, row.guild_id, row.channel_id));
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

        // Check again, the next look at a form's request without the script, loads a server's page each time it's pressed: from
        // the address the form's redirect leads to, and from the one Check again leads to, whose own leads back. Fails if it's
        // the address the browser shows with a fragment, which only scrolls, or is built from the query the page was sent.
        [Fact]
        public async Task CheckAgainLoadsThePageAgain()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            await SeedAsync(db);
            var admin = await SignedInAsync(factory, TestUsers.Admin, "198.51.100.156");

            await RequestSteps.CheckAgainWalkAsync(admin, await PostAsync(admin, Page(A), Page(A) + "?handler=Refresh"));
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
            Assert.Contains($"<a href=\"{Page(A)}?request={row.id}&amp;again=1#result\">Check again</a>", queued);
            Assert.True(PageTests.LoadsRefreshScript(queued), "The page with a box that isn't final doesn't load refresh.js");
            PageTests.AssertAccessible("a server's page with its request's box", queued);

            // Opened from Check again, its Check again takes again off
            Assert.Contains($"<a href=\"{Page(A)}?request={row.id}#result\">Check again</a>", await admin.Client.GetStringAsync($"{Page(A)}?request={row.id}&again=1", Token));

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
                var test = await RequestSteps.AnsweredAsync(owner,
                    await PostAsync(owner, Page(server), Page(server) + "?handler=TestMessage", ("channel", channel.ToString(CultureInfo.InvariantCulture))));
                Assert.Equal(HttpStatusCode.Conflict, test.StatusCode);

                var refresh = await RequestSteps.AnsweredAsync(owner, await PostAsync(owner, Page(server), Page(server) + "?handler=Refresh"));
                Assert.Equal(HttpStatusCode.Conflict, refresh.StatusCode);
                Assert.Contains("Nothing was sent.", Text(await refresh.Content.ReadAsStringAsync(Token)));
            }

            Assert.Empty(await db.SentAsync());

            await using var off = new DashboardFactory(db, new DashboardFactoryOptions { Settings = new Dictionary<string, string?> { ["Dashboard:RequestSigningKey"] = "" } });
            var offOwner = await SignedInAsync(off, TestUsers.Owner, "198.51.100.145");
            var offHtml = await offOwner.Client.GetStringAsync(Page(A), Token);

            Assert.Contains(ServerActions.RequestsOff, Text(offHtml));
            Assert.DoesNotMatch("<form[^>]*\\?handler=", offHtml);
            Assert.Equal(HttpStatusCode.Conflict, (await RequestSteps.AnsweredAsync(offOwner, await PostAsync(offOwner, Page(A), Page(A) + "?handler=Refresh"))).StatusCode);
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
        // names the one that waits, with the channel it named still chosen in the picker, and one to D is still sent
        [Fact]
        public async Task PerServerLimitsCountEachServer()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            await SeedAsync(db, new Guild { guild_id = A, armor_channel_id = General, map_channel_id = Postable, lobby_channel_id = NoEmbed, announcements_channel_id = News });
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.147");
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var waiting = new List<ulong>();

            for (var minute = 3; minute >= 1; minute--)
                waiting.Add(await RequestSteps.AddAsync(db, factory, RequestKinds.GuildTestMessage, now.AddSeconds(-10 * minute), guildId: A));

            Assert.Contains($"A request like this is waiting: request {waiting[0]}.", Text(await owner.Client.GetStringAsync(Page(A), Token)));

            var refused = await RequestSteps.AnsweredAsync(owner, await PostAsync(owner, Page(A), Page(A) + "?handler=TestMessage", ("channel", Postable.ToString(CultureInfo.InvariantCulture))));
            var html = await refused.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains($"A request like this is already waiting for the bot: request {waiting[0]}", Text(html));
            Assert.Contains($"<a href=\"/admin/requests/{waiting[0]}\">That request</a>", html);
            Assert.Equal([General.ToString(CultureInfo.InvariantCulture), Postable.ToString(CultureInfo.InvariantCulture)], Options(html).Select(option => option.Value));
            Assert.Matches($"<option value=\"{Postable}\" selected=\"selected\">", html);
            Assert.DoesNotMatch($"<option value=\"{General}\" selected", html);
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
                var refused = await RequestSteps.AnsweredAsync(owner, await PostAsync(owner, leave, leave, ("confirm", typed)));
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
            var old = await RequestSteps.AnsweredAsync(owner, await SignInSteps.PostAsync(owner, leave, token, origin: DashboardFactory.PublicOrigin,
                fields: [new("formkey", RequestFields.NewKey()), new("confirm", A.ToString(CultureInfo.InvariantCulture))]));
            var oldHtml = await old.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.Forbidden, old.StatusCode);
            Assert.Contains("This needs a sign-in from the last 15 minutes", Text(oldHtml));
            Assert.Null(Key(oldHtml, leave));
            Assert.Contains(WebUtility.HtmlEncode(FreshSignIn.SignInAgainLink(leave)), oldHtml);
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

            var refused = await RequestSteps.AnsweredAsync(owner, await PostAsync(owner, Page(C) + "/leave", Page(C) + "/leave", ("confirm", C.ToString(CultureInfo.InvariantCulture))));
            var refusedHtml = await refused.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains(ServerActions.NothingToLeave + " Nothing was sent.", Text(refusedHtml));
            Assert.DoesNotContain("name=\"confirm\"", refusedHtml);
            PageTests.AssertAccessible("a leave of a server MinerBot isn't in", refusedHtml);
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

        // The app with a queue whose commits land but whose answers never come, as when the database stops answering then
        static DashboardFactoryOptions Unconfirmed => new()
        {
            Services = services => services.Replace(ServiceDescriptor.Transient<IBotRequestQueue>(provider =>
                new UnconfirmedQueue(ActivatorUtilities.CreateInstance<BotRequestQueue>(provider))))
        };

        // A leave whose save the database didn't confirm: while MinerBot is in the server, its notice's GET draws the form
        // with the key it was sent with, and the words offer it. Once the bot has taken the leave and left, a reload of the
        // same address, as the error page advises while the database is down, draws no form and no last use, so the words
        // don't offer the form, but say where to look. Fails if the words offer a form the page doesn't draw.
        [Fact]
        public async Task AnUnconfirmedLeaveOfAServerMinerBotHasLeftOffersNoForm()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db, Unconfirmed);
            await SeedAsync(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.213");
            var leave = Page(A) + "/leave";

            var address = await RequestSteps.NoticeOfAsync(await PostAsync(owner, leave, leave, ("confirm", "Test Server")));
            var row = Assert.Single(await db.SentAsync());
            var unsure = await (await owner.Client.GetAsync(address, Token)).Content.ReadAsStringAsync(Token);

            Assert.Contains(RequestWords.Unsure, Text(unsure));
            Assert.Equal(row.request_key, Key(unsure, leave));

            Assert.True(await db.BotGuilds().LeaveGuild(A, At.AddHours(3), StatusSteps.RunId, 6, null));
            var left = await owner.Client.GetAsync(address, Token);
            var html = await left.Content.ReadAsStringAsync(Token);
            var text = Text(html);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, left.StatusCode);
            Assert.Contains(RequestWords.UnsureNotOffered, text);
            Assert.DoesNotContain(RequestWords.Unsure, text);
            Assert.Contains(ServerActions.NothingToLeave, text);
            Assert.DoesNotContain("name=\"formkey\"", html);
            PageTests.AssertAccessible("a leave the database didn't confirm, of a server MinerBot has left since", html);
        }

        // A test message and a refresh whose saves the database didn't confirm: once none of the channels the server chose can
        // take a test message, its notice's GET draws no form for it, and once MinerBot has left the server, none for either,
        // so the words don't offer one, but say where to look; a form that's still drawn keeps them. Fails if the words offer
        // a form the page doesn't draw.
        [Fact]
        public async Task AnUnconfirmedActionWhoseFormHasGoneOffersNoForm()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db, Unconfirmed);
            await SeedAsync(db);
            var tester = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.214");
            var refresher = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.215");
            var test = Page(A) + "?handler=TestMessage";
            var refresh = Page(A) + "?handler=Refresh";

            var tested = await RequestSteps.NoticeOfAsync(await PostAsync(tester, Page(A), test, ("channel", General.ToString(CultureInfo.InvariantCulture))));
            var refreshed = await RequestSteps.NoticeOfAsync(await PostAsync(refresher, Page(A), refresh));
            Assert.Equal(2, (await db.SentAsync()).Count);

            // Every kind turned off: the test message's form has gone, and the refresh's is still drawn
            await db.Guilds().SaveGuild(new Guild { guild_id = A });
            var noTest = await tester.Client.GetAsync(tested, Token);
            var noTestHtml = await noTest.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, noTest.StatusCode);
            Assert.Contains(ServerActions.NoChannels, Text(noTestHtml));
            Assert.Contains(RequestWords.UnsureNotOffered, Text(noTestHtml));
            Assert.DoesNotContain(RequestWords.Unsure, Text(noTestHtml));
            Assert.Null(Key(noTestHtml, test));
            Assert.NotNull(Key(noTestHtml, refresh));
            Assert.Contains(RequestWords.Unsure, Text(await (await refresher.Client.GetAsync(refreshed, Token)).Content.ReadAsStringAsync(Token)));

            // MinerBot has left: no form at all
            Assert.True(await db.BotGuilds().LeaveGuild(A, At.AddHours(3), StatusSteps.RunId, 6, null));
            var left = await refresher.Client.GetAsync(refreshed, Token);
            var html = await left.Content.ReadAsStringAsync(Token);
            var text = Text(html);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, left.StatusCode);
            Assert.Contains(ServerActions.NotIn, text);
            Assert.Contains(RequestWords.UnsureNotOffered, text);
            Assert.DoesNotContain(RequestWords.Unsure, text);
            Assert.DoesNotContain("name=\"formkey\"", html);
            PageTests.AssertAccessible("a refresh the database didn't confirm, of a server MinerBot has left since", html);
        }

        // A test message sent again to another channel from the page a browser kept with the first one's key, as Back can
        // restore it from the back-forward cache: while its form is drawn, the words offer the form below. Once none of the
        // channels the server chose can take one, and once MinerBot has left the server, the page draws no form for it, so
        // the words name the request without offering one. Fails if they offer a form the page doesn't draw.
        [Fact]
        public async Task ATestMessageSentBeforeWhoseFormHasGoneOffersNoForm()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            await SeedAsync(db, new Guild { guild_id = A, armor_channel_id = General, map_channel_id = Postable, lobby_channel_id = NoEmbed, announcements_channel_id = News });
            var owner = await SignedInAsync(factory, TestUsers.Owner, "198.51.100.239");
            var test = Page(A) + "?handler=TestMessage";
            var drawn = await owner.Client.GetStringAsync(Page(A), Token);

            // The form as the browser kept it, with its key and token, and the channel chosen
            Task<HttpResponseMessage> SendAsync(ulong channel) => SignInSteps.PostAsync(owner, test, SignInSteps.FormToken(drawn), origin: DashboardFactory.PublicOrigin,
                fields: [new("formkey", Key(drawn, test)!), new("channel", channel.ToString(CultureInfo.InvariantCulture))]);

            var first = await SendAsync(General);
            var row = Assert.Single(await db.SentAsync());
            SentTo(first, A, row.id);

            var address = await RequestSteps.NoticeOfAsync(await SendAsync(Postable));
            var again = await owner.Client.GetAsync(address, Token);
            var againHtml = await again.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Contains(RequestWords.SentBefore(row.id), Text(againHtml));
            Assert.NotNull(Key(againHtml, test));

            // Every kind turned off: the test message's form has gone, and the refresh's is still drawn
            await db.Guilds().SaveGuild(new Guild { guild_id = A });
            var noTest = await owner.Client.GetAsync(address, Token);
            var noTestHtml = await noTest.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.Conflict, noTest.StatusCode);
            Assert.Contains(ServerActions.NoChannels, Text(noTestHtml));
            Assert.Contains(RequestWords.SentBeforeNotOffered(row.id), Text(noTestHtml));
            Assert.DoesNotContain("The form below is new", Text(noTestHtml));
            Assert.Null(Key(noTestHtml, test));
            Assert.NotNull(Key(noTestHtml, Page(A) + "?handler=Refresh"));

            // MinerBot has left: no form at all
            Assert.True(await db.BotGuilds().LeaveGuild(A, At.AddHours(3), StatusSteps.RunId, 6, null));
            var left = await owner.Client.GetAsync(address, Token);
            var html = await left.Content.ReadAsStringAsync(Token);
            var text = Text(html);

            Assert.Equal(HttpStatusCode.Conflict, left.StatusCode);
            Assert.Contains(ServerActions.NotIn, text);
            Assert.Contains(RequestWords.SentBeforeNotOffered(row.id), text);
            Assert.DoesNotContain("The form below is new", text);
            Assert.Contains($"<a href=\"/admin/requests/{row.id}\">That request</a>", html);
            Assert.DoesNotContain("name=\"formkey\"", html);
            Assert.Single(await db.SentAsync());
            PageTests.AssertAccessible("a test message sent before, of a server MinerBot has left since", html);
        }

        // The same for a test message sent before, and one whose save the database didn't confirm, while MinerBot is in the
        // server: once the chosen channels it was offered for can't take one, as when they lose Embed Links, the page lists
        // them with why and draws no form for it, and once Discord marks the server unavailable, no form at all, so the
        // words don't offer one; while the channels can take one again before that, the form and the words that offer it
        // are back. Fails if the words offer a form the page doesn't draw.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ATestMessageNoticeWhoseChannelsCantTakeOneOffersNoForm(bool unsure)
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db, unsure ? Unconfirmed : new DashboardFactoryOptions());
            await SeedAsync(db, new Guild { guild_id = A, armor_channel_id = General, map_channel_id = Postable, lobby_channel_id = NoEmbed, announcements_channel_id = News });
            var owner = await SignedInAsync(factory, TestUsers.Owner, unsure ? "192.0.2.165" : "192.0.2.164");
            var test = Page(A) + "?handler=TestMessage";
            var drawn = await owner.Client.GetStringAsync(Page(A), Token);

            // The form as the browser kept it, with its key and token, and the channel chosen
            Task<HttpResponseMessage> SendAsync(ulong channel) => SignInSteps.PostAsync(owner, test, SignInSteps.FormToken(drawn), origin: DashboardFactory.PublicOrigin,
                fields: [new("formkey", Key(drawn, test)!), new("channel", channel.ToString(CultureInfo.InvariantCulture))]);

            // A's channels as the bot's code writes them, general and postable with Embed Links or without
            Task<bool> EmbedAsync(bool embed, uint seq) => db.BotGuilds().SaveGuild(StatusSteps.Server(A, "Test Server"),
            [
                StatusSteps.Channel(General, "general", 0, embed: embed),
                StatusSteps.Channel(News, "news", 1, GuildChannelType.Announcement, embed: false),
                StatusSteps.Channel(NoEmbed, "no-embed", 2, embed: false),
                StatusSteps.Channel(Postable, "postable", 3, embed: embed),
                StatusSteps.Channel(Hidden, "___hidden___", 4, view: false, send: false, embed: false)
            ], At.AddHours(3), StatusSteps.RunId, seq);

            var first = await SendAsync(General);
            var row = Assert.Single(await db.SentAsync());
            var address = unsure ? await RequestSteps.NoticeOfAsync(first) : await RequestSteps.NoticeOfAsync(await SendAsync(Postable));
            var offered = unsure ? RequestWords.Unsure : RequestWords.SentBefore(row.id);
            var notOffered = unsure ? RequestWords.UnsureNotOffered : RequestWords.SentBeforeNotOffered(row.id);

            if (!unsure)
                SentTo(first, A, row.id);

            var again = await owner.Client.GetAsync(address, Token);
            var againHtml = await again.Content.ReadAsStringAsync(Token);

            Assert.Contains(offered, Text(againHtml));
            Assert.NotNull(Key(againHtml, test));

            // General and postable lose Embed Links: still chosen, listed with why, and none offered
            Assert.False(await EmbedAsync(false, 6));
            var noneOffered = await owner.Client.GetAsync(address, Token);
            var noneOfferedHtml = await noneOffered.Content.ReadAsStringAsync(Token);
            var noneOfferedText = Text(noneOfferedHtml);

            Assert.Equal(unsure ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Conflict, noneOffered.StatusCode);
            Assert.Contains(ServerActions.NoneOffered, noneOfferedText);
            Assert.Contains(notOffered, noneOfferedText);
            Assert.DoesNotContain(unsure ? RequestWords.Unsure : "The form below is new", noneOfferedText);
            Assert.Null(Key(noneOfferedHtml, test));
            Assert.NotNull(Key(noneOfferedHtml, Page(A) + "?handler=Refresh"));

            // Embed Links back: the form and the words that offer it
            Assert.False(await EmbedAsync(true, 7));
            var back = await (await owner.Client.GetAsync(address, Token)).Content.ReadAsStringAsync(Token);
            Assert.Contains(offered, Text(back));
            Assert.NotNull(Key(back, test));

            // Discord marks the server unavailable: no form at all
            Assert.Equal(1, await db.BotGuilds().MarkUnavailable([A], At.AddHours(4)));
            var unavailable = await owner.Client.GetAsync(address, Token);
            var html = await unavailable.Content.ReadAsStringAsync(Token);
            var text = Text(html);

            Assert.Equal(noneOffered.StatusCode, unavailable.StatusCode);
            Assert.Contains(ServerActions.Unavailable, text);
            Assert.Contains(notOffered, text);
            Assert.DoesNotContain(unsure ? RequestWords.Unsure : "The form below is new", text);
            Assert.DoesNotContain("name=\"formkey\"", html);
            Assert.Single(await db.SentAsync());
            PageTests.AssertAccessible("a test message's notice, of a server Discord marks unavailable since", html);
        }

        // A name typed wrong gets words that send the person to the box and the name above it, while the page draws them.
        // Once the sign-in has passed its 15 minutes, within the notice's 30, and once MinerBot has left the server, the page
        // draws no box, so the words point to none: as after the leave sent from the notice's own page, with the sign-in that
        // sent it still fresh, and on an older notice whose sign-in is stale. Fails if they send the person to a box or a name
        // the page doesn't draw, or the box's words go while it's drawn.
        [Fact]
        public async Task ANameTypedWrongPointsToNoBoxThePageDoesntDraw()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            await SeedAsync(db);
            var owner = await SignedInAsync(factory, TestUsers.Owner, "192.0.2.163");
            var leave = Page(A) + "/leave";

            var address = await RequestSteps.NoticeOfAsync(await PostAsync(owner, leave, leave, ("confirm", "Test Sever")));
            var refused = await owner.Client.GetAsync(address, Token);
            var refusedHtml = await refused.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains(RequestWords.NotConfirmedServer, Text(refusedHtml));
            Assert.DoesNotContain(RequestWords.NotConfirmedNotOffered, Text(refusedHtml));
            Assert.NotNull(Key(refusedHtml, leave));

            factory.Clock.Advance(TimeSpan.FromMinutes(16));
            var stale = await owner.Client.GetAsync(address, Token);
            var staleHtml = await stale.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
            Assert.Contains(RequestWords.NotConfirmedNotOffered, Text(staleHtml));
            Assert.DoesNotContain(RequestWords.NotConfirmedServer, Text(staleHtml));
            Assert.Null(Key(staleHtml, leave));
            Assert.Contains(WebUtility.HtmlEncode(FreshSignIn.SignInAgainLink(leave)), staleHtml);

            // A second session, signed in now, so fresh: the name typed wrong, the leave corrected and sent from that notice's
            // own page, MinerBot gone, and the notice's address again within the sign-in's 15 minutes
            var fresh = await SignedInAsync(factory, TestUsers.Owner, "192.0.2.166");
            var freshAddress = await RequestSteps.NoticeOfAsync(await PostAsync(fresh, leave, leave, ("confirm", "Test Sever")));
            var notice = await (await fresh.Client.GetAsync(freshAddress, Token)).Content.ReadAsStringAsync(Token);
            var key = Key(notice, leave);
            Assert.NotNull(key);

            var sent = await SignInSteps.PostAsync(fresh, leave, SignInSteps.FormToken(notice), origin: DashboardFactory.PublicOrigin,
                fields: [new("formkey", key), new("confirm", "Test Server")]);
            SentTo(sent, A, Assert.Single(await db.SentAsync()).id);

            Assert.True(await db.BotGuilds().LeaveGuild(A, At.AddHours(3), StatusSteps.RunId, 6, null));
            var gone = await fresh.Client.GetAsync(freshAddress, Token);
            var goneHtml = await gone.Content.ReadAsStringAsync(Token);
            var goneText = Text(goneHtml);

            Assert.Equal(HttpStatusCode.BadRequest, gone.StatusCode);
            Assert.Contains(RequestWords.NotConfirmedNotOffered, goneText);
            Assert.DoesNotContain(RequestWords.NotConfirmedServer, goneText);
            Assert.Contains(ServerActions.NothingToLeave, goneText);
            Assert.DoesNotContain("name=\"confirm\"", goneHtml);

            // The first session's notice, whose sign-in is stale
            var left = await owner.Client.GetAsync(address, Token);
            var html = await left.Content.ReadAsStringAsync(Token);
            var text = Text(html);

            Assert.Equal(HttpStatusCode.BadRequest, left.StatusCode);
            Assert.Contains(RequestWords.NotConfirmedNotOffered, text);
            Assert.DoesNotContain(RequestWords.NotConfirmedServer, text);
            Assert.Contains(ServerActions.NothingToLeave, text);
            Assert.DoesNotContain("name=\"confirm\"", html);
            Assert.Single(await db.SentAsync());
            PageTests.AssertAccessible("a name typed wrong, of a server MinerBot has left since", html);
        }
    }
}
