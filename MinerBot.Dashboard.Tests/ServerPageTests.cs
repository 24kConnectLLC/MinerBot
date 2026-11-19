using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // The servers list and each server's page, read from rows the bot's own code writes and the website's settings, for the
    // Owner and an Admin: each setting's status, hidden channels, an outage, a server MinerBot left, settings for one it
    // was never in, and the copy's age
    [Collection("Database")]
    public partial class ServerPageTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        // A: in, with settings OK, can't post, off and can't check. B: in, not set up, in an outage. C: left, with settings.
        // D: in, with a setting naming A's channel. E: settings only.
        const ulong A = 1000000000000000801, B = 1000000000000000802, C = 1000000000000000803, D = 1000000000000000804, E = 1000000000000000805;

        static readonly DateTime At = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

        static string Text(string html) => Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(html), "<[^>]+>", " "), "\\s+", " ");

        // A server's row in the servers list, as text, found by the ID it shows
        static string ServerRow(string html, ulong id)
        {
            var at = html.IndexOf($"<p class=\"cell-line muted address\">{id}</p>", StringComparison.Ordinal);
            Assert.True(at >= 0, $"The list has no row for {id}");

            return Text(html[html.LastIndexOf("<tr>", at, StringComparison.Ordinal)..html.IndexOf("</tr>", at, StringComparison.Ordinal)]).Trim();
        }

        // What the Administrator warning asks for instead: the README's recommended five, the pin command's two among them,
        // so a server that follows it keeps every command working, and Mention Everyone, which they leave out and without
        // which /admin announce's @everyone, @here and pings of a role that isn't mentionable notify no one
        const string AdministratorAdvice = "MinerBot works with the five permissions its README recommends: View Channel, Send Messages and Embed Links where it posts, and Read Message History and Pin Messages for its pin command. Its /admin announce also needs Mention Everyone to ping @everyone, @here or a role that isn't mentionable. A role with just those is safer.";

        static async Task SeedAsync(MariaDbFixture db, DateTime now)
        {
            var guilds = db.BotGuilds();

            await guilds.SaveGuild(StatusSteps.Server(A, "Test​Server<script>", administrator: true),
            [
                StatusSteps.Channel(10, "rules", 0, category: null, categoryId: null, categoryPosition: null),
                StatusSteps.Channel(20, "general", 0),
                StatusSteps.Channel(21, "___hidden___", 1, view: false, send: false, embed: false),
                StatusSteps.Channel(22, "___hidden___", 2, view: false, send: false, embed: false),
                StatusSteps.Channel(23, "no-send", 3, send: false),
                StatusSteps.Channel(30, "memes", 0, categoryId: 3901, categoryPosition: 1),
                StatusSteps.Channel(40, "___hidden___", 0, category: "___hidden___", categoryId: 3902, categoryPosition: 2, view: false, send: false, embed: false)
            ], At, StatusSteps.RunId, 1);
            await guilds.SaveGuild(StatusSteps.Server(B, "Other Server", icon: null), [StatusSteps.Channel(50, "general", 0)], At, StatusSteps.RunId, 2);
            await guilds.SaveGuild(StatusSteps.Server(C, "Gone Server"), [StatusSteps.Channel(60, "general", 0)], At, StatusSteps.RunId, 3);
            await guilds.SaveGuild(StatusSteps.Server(D, "Borrowing Server"), [StatusSteps.Channel(70, "general", 0)], At, StatusSteps.RunId, 4);
            await guilds.MarkUnavailable([B], At.AddHours(1));
            await guilds.LeaveGuild(C, At.AddHours(2), StatusSteps.RunId, 5, null);
            await guilds.SaveMemberCount(A, 118, now.Date.AddDays(-1), now);
            await guilds.SaveMemberCount(A, 120, now.Date, now);

            var settings = db.Guilds();
            await settings.SaveGuild(new Guild { guild_id = A, armor_channel_id = 20, map_channel_id = 23, lobby_channel_id = 0, announcements_channel_id = 9999 });
            await settings.SaveGuild(new Guild { guild_id = C, armor_channel_id = 60 });
            await settings.SaveGuild(new Guild { guild_id = D, armor_channel_id = 20 });
            await settings.SaveGuild(new Guild { guild_id = E, armor_channel_id = 4001, lobby_channel_id = 4002 });
        }

        // Every server the bot is in by default, each filter with its count, each setting's status in words, and the
        // settings of servers the bot isn't in apart
        [Fact]
        public async Task ListShowsEachServerAndItsSettings()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            await SeedAsync(db, now);
            await StatusSteps.StatusAsync(db, now);

            var admin = factory.Visitor("203.0.113.140");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            var html = await admin.Client.GetStringAsync("/admin/servers", Token);
            var text = Text(html);
            PageTests.AssertAccessible("the servers list", html);

            Assert.Contains("MinerBot is running and connected to Discord, so this copy is current.", text);
            Assert.Equal(["In (3)", "Not set up (1)", "Has problems (3)", "Can't check (1)", "Left (1)", "All (4)"],
                Regex.Matches(html, "<li><a href=\"[^\"]+\"[^>]*>([^<]+)</a></li>").Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)));
            Assert.Contains("<a href=\"/admin/servers\" aria-current=\"page\">In (3)</a>", html);

            // A's name revealed, its ID as text, the Administrator warning, its members' trend, and its four settings
            Assert.Contains("<a href=\"/admin/servers/1000000000000000801\"><bdi class=\"revealed\">Test<mark class=\"hidden-char\" title=\"zero-width space\">[U&#x2B;200B]</mark>Server&lt;script&gt;</bdi></a>", html);
            Assert.Contains("<img class=\"server-icon\" src=\"https://cdn.discordapp.com/icons/1000000000000000801/a_0123456789abcdef0123456789abcdef.png?size=64\" alt=\"\" width=\"32\" height=\"32\" />", html);
            Assert.Contains("Has Administrator", text);
            Assert.Contains("Has Administrator: " + AdministratorAdvice, text);
            Assert.Contains("120 up 2 since", text);

            // Each setting's mark in its own server's row, in the columns' order: armor, maps, lobbies and announcements. The
            // tabs, the key below the table and the other servers' settings hold these words too, so the page's text can't
            // tell.
            Assert.EndsWith("OK general Can't post: missing Send Messages no-send Off Can't check", ServerRow(html, A));

            // The day each server was joined stays whole: when the settings' marks want the room, the table's automatic layout
            // would otherwise cut it at a hyphen, even on a desktop
            PageTests.AssertTimesStayWhole("the servers list", html);

            // B in an outage, without an icon and not set up, and D posting to A's channel
            Assert.Contains("Outage: Discord reports it unavailable", ServerRow(html, B));
            Assert.Contains("<span class=\"server-icon server-icon-none\"></span>", html);
            Assert.EndsWith("Not set up Not set up Not set up Not set up", ServerRow(html, B));
            Assert.EndsWith("Another server's channel general Off Off Off", ServerRow(html, D));

            // C left, so it's not in the default list, and its settings and E's are apart
            Assert.DoesNotContain("Gone Server", text);
            var elsewhere = Text(html[html.IndexOf("id=\"elsewhere\"", StringComparison.Ordinal)..]);
            Assert.Contains("1000000000000000803 60 Off Off Off", elsewhere);
            Assert.Contains("1000000000000000805 4001 Off 4002 Off", elsewhere);
            Assert.Contains("<a href=\"/admin/servers/1000000000000000805\">1000000000000000805</a>", html);

            var left = Text(await admin.Client.GetStringAsync("/admin/servers?show=left", Token));
            Assert.Contains("Gone Server", left);
            Assert.Contains("Left on 2026-11-18", left);
            Assert.Contains("MinerBot isn't in this server", left);

            var problems = Text(await admin.Client.GetStringAsync("/admin/servers?show=problems", Token));
            Assert.Contains("Test", problems);
            Assert.Contains("Other Server", problems);
            Assert.Contains("Borrowing Server", problems);

            Assert.Contains("Other Server", Text(await admin.Client.GetStringAsync("/admin/servers?show=not-set-up", Token)));
            Assert.DoesNotContain("Other Server", Text(await admin.Client.GetStringAsync("/admin/servers?show=cant-check", Token)));

            foreach (var word in new[] { "in", "not-set-up", "problems", "cant-check", "left", "all" })
                PageTests.AssertAccessible("the servers list " + word, await admin.Client.GetStringAsync("/admin/servers?show=" + word, Token));

            Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.GetAsync("/admin/servers?show=everything", Token)).StatusCode);
        }

        // A server's channels in Discord's order, grouped by category, with the hidden ones counted, its settings by name,
        // its joins and leaves, its members, and the dashboard's changes to it, with addresses for the Owner only
        [Fact]
        public async Task ServerPageShowsChannelsSettingsAndHistory()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            await SeedAsync(db, now);
            await StatusSteps.StatusAsync(db, now);
            await db.Audit().AddAudit(new AuditEntry { at = now.AddMinutes(-1), actor_id = TestUsers.Owner.Id, actor_name = "Test Owner", actor_role = "owner",
                action = "auth.signin", outcome = "ok", guild_id = A, target_type = "session", ip = "192.0.2.93" });

            var owner = factory.Visitor("203.0.113.141");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var admin = factory.Visitor("203.0.113.142");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            var html = await owner.Client.GetStringAsync("/admin/servers/1000000000000000801", Token);
            var text = Text(html);
            PageTests.AssertAccessible("a server's page", html);
            PageTests.AssertTablesTakeTheKeyboard("a server's page", html);
            PageTests.AssertTimesStayWhole("a server's page", html);

            Assert.Contains("<title>Server 1000000000000000801 - MinerBot dashboard</title>", html);
            Assert.Contains("<h1>Server 1000000000000000801</h1>", html);
            Assert.Contains("<bdi class=\"revealed\">Test<mark class=\"hidden-char\" title=\"zero-width space\">[U&#x2B;200B]</mark>Server&lt;script&gt;</bdi>", html);
            Assert.Contains("In this server since 2026-08-01 12:00:00 UTC.", text);
            Assert.Contains("Members 120 (up 2 since", text);
            Assert.Contains("Has Administrator", text);
            Assert.Contains("Permissions Has Administrator " + AdministratorAdvice, text);

            // The settings, each with its channel by name
            Assert.Contains("Armor general 20 OK", text);
            Assert.Contains("Maps no-send 23 Can't post: missing Send Messages Give MinerBot Send Messages in this channel, or choose another.", text);
            Assert.Contains("Lobbies None Off The server turned this kind of post off.", text);
            Assert.Contains("Announcements 9999 Can't check MinerBot's copy has no text or announcement channel with this ID. It may be a voice channel's chat", text);

            // The channels in Discord's order: outside a category first, two categories of one name apart, and the hidden
            // ones counted where they are
            var channels = html[html.IndexOf("id=\"channels\"", StringComparison.Ordinal)..html.IndexOf("id=\"joins\"", StringComparison.Ordinal)];
            Assert.Equal(["Outside any category", "<bdi class=\"revealed\">Text Channels</bdi>", "<bdi class=\"revealed\">Text Channels</bdi>", "A category hidden from MinerBot"],
                Regex.Matches(channels, "<h3[^>]*>\\s*(.*?)\\s*</h3>", RegexOptions.Singleline).Select(match => match.Groups[1].Value.Trim()));
            Assert.Equal(["rules", "general", "no-send", "memes"],
                Regex.Matches(channels, "<th scope=\"row\">\\s*<p class=\"cell-line\"><bdi class=\"revealed\">([^<]+)</bdi>").Select(match => match.Groups[1].Value));
            Assert.Contains("2 channels hidden from MinerBot.", Text(channels));
            Assert.Contains("1 channel hidden from MinerBot.", Text(channels));
            Assert.DoesNotContain("___hidden___", html);
            Assert.Contains("no-send 23 Text Yes No Yes", Text(channels));

            Assert.Contains("Joined first time", text);
            Assert.Contains("1 change, newest first.", text);
            Assert.Contains("192.0.2.93", html);

            var adminPage = await admin.Client.GetStringAsync("/admin/servers/1000000000000000801", Token);
            Assert.DoesNotContain("192.0.2.93", adminPage);
            Assert.Contains("Each row's visitor address is shown to the Owner alone.", Text(adminPage));

            // D's setting names A's channel
            Assert.Contains("Armor 20 Another server's channel The channel is in server 1000000000000000801", Text(await admin.Client.GetStringAsync("/admin/servers/1000000000000000804", Token)));

            // B's outage, whose channels are as they were before it, while the rest of the copy is current
            var outage = Text(await admin.Client.GetStringAsync("/admin/servers/1000000000000000802", Token));
            Assert.Contains("Outage Unavailable", outage);
            Assert.Contains("Not known to be current: Discord reports this server unavailable, so MinerBot's copy of its channels is as it was before the outage. The rest of the copy is current.", outage);
            Assert.DoesNotContain("so this copy is current", outage);
            Assert.Contains("MinerBot is running and connected to Discord, so this copy is current.", text);
        }

        // A server the bot left keeps its page with its history, a server only the website knows gets a page saying
        // MinerBot isn't in it, and an ID that's neither is a 404
        [Fact]
        public async Task LeftSettingsOnlyAndUnknownServers()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            await SeedAsync(db, now);

            var admin = factory.Visitor("203.0.113.143");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            var left = await admin.Client.GetStringAsync("/admin/servers/1000000000000000803", Token);
            PageTests.AssertAccessible("a left server's page", left);
            Assert.Contains("Left on 2026-11-18 14:00:00 UTC, so posts to this server are skipped.", Text(left));
            Assert.Contains("Armor 60 MinerBot isn't in this server", Text(left));
            Assert.Contains("MinerBot's copy keeps no channels of a server it has left.", Text(left));
            Assert.Contains("Left", Text(left[left.IndexOf("id=\"joins\"", StringComparison.Ordinal)..]));

            var settingsOnly = await admin.Client.GetStringAsync("/admin/servers/1000000000000000805", Token);
            PageTests.AssertAccessible("a settings-only server's page", settingsOnly);
            PageTests.AssertTablesTakeTheKeyboard("a settings-only server's page", settingsOnly);
            Assert.Contains("MinerBot isn't in this server: its copy of its servers has never recorded it.", Text(settingsOnly));
            Assert.Contains("Armor 4001 MinerBot isn't in this server", Text(settingsOnly));
            Assert.Contains("Lobbies 4002 MinerBot isn't in this server", Text(settingsOnly));
            Assert.DoesNotContain("id=\"channels\"", settingsOnly);

            foreach (var path in new[] { "/admin/servers/12345", "/admin/servers/0", "/admin/servers/-5", "/admin/servers/abc", "/admin/servers/99999999999999999999" })
                Assert.True((await admin.Client.GetAsync(path, Token)).StatusCode == HttpStatusCode.NotFound, $"{path} isn't a 404");
        }

        // How current the copy is while the bot is stopped: as of its clean stop
        [Fact]
        public async Task CopyIsAsOfTheStopWhileTheBotIsStopped()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            await SeedAsync(db, now);

            var stopped = StatusSteps.Status(now.AddHours(-1));
            stopped.stopped_at = stopped.heartbeat_at;
            await db.Status().SaveStatus(stopped);

            var admin = factory.Visitor("203.0.113.144");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            foreach (var path in new[] { "/admin/servers", "/admin/servers/1000000000000000801" })
            {
                var text = Text(await admin.Client.GetStringAsync(path, Token));
                Assert.Contains($"As of {Status.TimeWords.Utc(stopped.heartbeat_at)}, 1 hour ago: MinerBot is stopped, so this copy is as of when it last heard from Discord.", text);
            }
        }

        // Running and connected, 2 minutes after MinerBot's database was unreachable for 18 minutes: as of the outage's start,
        // which the bot's database.unreachable event gives
        [Fact]
        public async Task CopyIsAsOfAnOutagesStartJustAfterIt()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            await SeedAsync(db, now);

            var began = now.AddMinutes(-20);
            var ended = now.AddMinutes(-2);
            await db.Status().SaveStatus(StatusSteps.Status(now.AddSeconds(-10)));
            await db.Status().AddEvents([StatusSteps.Event(1, ended, "database.unreachable", EventLevel.Warning,
                string.Create(CultureInfo.InvariantCulture, $"from {began:yyyy-MM-dd HH:mm:ss} to {ended:yyyy-MM-dd HH:mm:ss} UTC"))]);

            var admin = factory.Visitor("203.0.113.166");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            foreach (var path in new[] { "/admin/servers", "/admin/servers/1000000000000000801" })
                Assert.Contains($"As of {Status.TimeWords.Utc(began)}, 20 minutes ago: MinerBot's database was unreachable until {Status.TimeWords.Utc(ended)}",
                    Text(await admin.Client.GetStringAsync(path, Token)));
        }

        // Not checking in for 40 minutes, after Discord dropped it an hour ago: as of that drop, the first since its latest
        // connection, rather than an older drop before that connection or the later time its row says it was reconnecting
        [Fact]
        public async Task CopyIsAsOfTheFirstDropSinceTheLatestConnection()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            await SeedAsync(db, now);

            var offline = StatusSteps.Status(now.AddMinutes(-40));
            offline.discord_state = DiscordState.Connecting;
            offline.discord_since = now.AddMinutes(-50);
            await db.Status().SaveStatus(offline);
            await db.Status().AddEvents(
            [
                StatusSteps.Event(1, now.AddHours(-3), "discord.disconnected", EventLevel.Warning, "WebSocketException"),
                StatusSteps.Event(2, now.AddHours(-2), "discord.connected", EventLevel.Info, "after 3600 s down, since 2026-11-18 00:34:42 UTC"),
                StatusSteps.Event(3, now.AddHours(-1), "discord.disconnected", EventLevel.Warning, "WebSocketException")
            ]);

            var admin = factory.Visitor("203.0.113.167");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            foreach (var path in new[] { "/admin/servers", "/admin/servers/1000000000000000801" })
                Assert.Contains($"As of {Status.TimeWords.Utc(now.AddHours(-1))}, 1 hour ago: MinerBot isn't checking in, so this copy is as of when it last heard from Discord.",
                    Text(await admin.Client.GetStringAsync(path, Token)));
        }

        // A run that never reached Discord, after one that connected and then ended a day ago: the copy is as of that end,
        // its clean stop, the last heartbeat the next run names, or else that next start, and not the later run's own failed
        // attempt, which wrote nothing to it. The same once that later run has stopped, and when the run that connected had
        // itself followed a crash: it records that crash's unclean end at its first status write, 5 seconds after its start
        // and so after its first connection, and that names an older run's last heartbeat, not this run's end.
        [Fact]
        public async Task CopyIsAsOfTheEndOfTheRunThatLastConnected()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            await SeedAsync(db, now);

            var connected = now.AddDays(-3);
            var ended = now.AddDays(-1);
            var restarted = now.AddMinutes(-10);
            var admin = factory.Visitor("203.0.113.168");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            BotStatus Run(DateTime heartbeat, DateTime? stopped = null) => new()
            {
                run_id = 2, version = StatusSteps.Commit, started_at = restarted, heartbeat_at = heartbeat, stopped_at = stopped,
                discord_state = DiscordState.Connecting, discord_since = restarted.AddSeconds(5), steam_state = SteamState.LoggedOn, steam_since = restarted.AddSeconds(3)
            };

            static BotEvent Event(ulong runId, uint seq, DateTime at, string kind, string level, string? detail = null) =>
                new() { run_id = runId, seq = seq, at = at, kind = kind, level = level, detail = detail };

            // The run before's end as each case records it, then the later run's start and its first attempt
            var cases = new (string What, DateTime AsOf, BotEvent[] End)[]
            {
                ("a clean stop", ended, [Event(1, 4, ended, "bot.stopping", EventLevel.Info)]),
                ("an unclean end", ended, [Event(2, 2, restarted.AddSeconds(1), "bot.unclean_end", EventLevel.Warning,
                    string.Create(CultureInfo.InvariantCulture, $"the run that started at {connected.AddSeconds(-1):yyyy-MM-dd HH:mm:ss} UTC never stopped cleanly; its last heartbeat was at {ended:yyyy-MM-dd HH:mm:ss} UTC"))]),
                ("no end recorded", restarted, [])
            };

            // Each again after the unclean end the run that connected recorded of the one before it, whose last heartbeat was
            // 3 hours before that run started
            var crashBefore = Event(1, 3, connected.AddSeconds(4), "bot.unclean_end", EventLevel.Warning,
                string.Create(CultureInfo.InvariantCulture, $"the run that started at {connected.AddDays(-2):yyyy-MM-dd HH:mm:ss} UTC never stopped cleanly; its last heartbeat was at {connected.AddHours(-3):yyyy-MM-dd HH:mm:ss} UTC"));
            cases = [.. cases, .. cases.Select(item => (item.What + ", by a run that followed a crash", item.AsOf, (BotEvent[])[crashBefore, .. item.End]))];

            // A stopped run's row keeps its stop whatever that run writes after it, so each case starts with no row
            foreach (var (what, asOf, end) in cases)
            {
                await db.ClearAsync(MariaDbFixture.EventTable);
                await db.ClearAsync(MariaDbFixture.StatusTable);
                await db.Status().AddEvents(
                [
                    Event(1, 1, connected.AddSeconds(-1), "bot.started", EventLevel.Info, "version " + StatusSteps.Commit),
                    Event(1, 2, connected, "discord.connected", EventLevel.Info, "the first connection of this run, 1 s after the start"),
                    .. end,
                    Event(2, 1, restarted, "bot.started", EventLevel.Info, "version " + StatusSteps.Commit),
                    Event(2, 3, restarted.AddSeconds(4), "discord.disconnected", EventLevel.Warning, "WebSocketException")
                ]);

                var age = asOf == ended ? "1 day ago" : "10 minutes ago";

                await db.Status().SaveStatus(Run(now.AddSeconds(-10)));
                foreach (var path in new[] { "/admin/servers", "/admin/servers/1000000000000000801" })
                    Assert.True(Text(await admin.Client.GetStringAsync(path, Token)).Contains($"As of {Status.TimeWords.Utc(asOf)}, {age}: MinerBot isn't connected to Discord, so this copy is as of when it last heard from Discord.", StringComparison.Ordinal),
                        $"{path} after {what}, while the later run runs");

                await db.Status().SaveStatus(Run(now.AddMinutes(-2), now.AddMinutes(-2)));
                Assert.True(Text(await admin.Client.GetStringAsync("/admin/servers", Token)).Contains($"As of {Status.TimeWords.Utc(asOf)}, {age}: MinerBot is stopped, so this copy is as of when it last heard from Discord.", StringComparison.Ordinal),
                    $"the list after {what}, once the later run has stopped");
            }
        }

        // The run that connected last followed a crash, whose unclean end it recorded at its first status write, 5 seconds
        // after its start and so after its own first connection. That names the crashed run's last heartbeat, 3 hours before
        // this run began, and is no end of this run: once it stops, stops checking in, or Discord drops it, the copy is as of
        // then.
        [Fact]
        public async Task CopyIsNotAsOfTheCrashBeforeTheRunThatConnected()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            await SeedAsync(db, now);

            var started = now.AddHours(-1);
            var admin = factory.Visitor("203.0.113.170");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            BotStatus Row(DateTime heartbeat, DateTime? stopped = null, string discord = DiscordState.Connected, DateTime? discordSince = null) => new()
            {
                run_id = 1, version = StatusSteps.Commit, started_at = started, heartbeat_at = heartbeat, stopped_at = stopped,
                discord_state = discord, discord_since = discordSince ?? started.AddSeconds(5), steam_state = SteamState.LoggedOn, steam_since = started.AddSeconds(3)
            };

            static BotEvent Event(uint seq, DateTime at, string kind, string level, string? detail = null) =>
                new() { run_id = 1, seq = seq, at = at, kind = kind, level = level, detail = detail };

            // As the bot records them: its start, its first connection a second later, then the crash of the run before it
            BotEvent[] run =
            [
                Event(1, started, "bot.started", EventLevel.Info, "version " + StatusSteps.Commit),
                Event(2, started.AddSeconds(1), "discord.connected", EventLevel.Info, "the first connection of this run, 1 s after the start"),
                Event(3, started.AddSeconds(5), "bot.unclean_end", EventLevel.Warning, string.Create(CultureInfo.InvariantCulture,
                    $"the run that started at {started.AddDays(-2):yyyy-MM-dd HH:mm:ss} UTC never stopped cleanly; its last heartbeat was at {started.AddHours(-3):yyyy-MM-dd HH:mm:ss} UTC"))
            ];

            var cases = new (string Said, BotEvent[] Then, BotStatus Row)[]
            {
                ($"As of {Status.TimeWords.Utc(now.AddMinutes(-2))}, 2 minutes ago: MinerBot is stopped",
                    [Event(4, now.AddMinutes(-2), "bot.stopping", EventLevel.Info)], Row(now.AddMinutes(-2), now.AddMinutes(-2))),
                ($"As of {Status.TimeWords.Utc(now.AddMinutes(-30))}, 30 minutes ago: MinerBot isn't checking in", [], Row(now.AddMinutes(-30))),
                ($"As of {Status.TimeWords.Utc(now.AddMinutes(-5))}, 5 minutes ago: MinerBot isn't connected to Discord",
                    [Event(4, now.AddMinutes(-5), "discord.disconnected", EventLevel.Warning, "WebSocketException")],
                    Row(now.AddSeconds(-10), discord: DiscordState.Connecting, discordSince: now.AddMinutes(-5)))
            };

            foreach (var (said, then, row) in cases)
            {
                await db.ClearAsync(MariaDbFixture.EventTable);
                await db.ClearAsync(MariaDbFixture.StatusTable);
                await db.Status().AddEvents([.. run, .. then]);
                await db.Status().SaveStatus(row);

                foreach (var path in new[] { "/admin/servers", "/admin/servers/1000000000000000801" })
                {
                    var text = Text(await admin.Client.GetStringAsync(path, Token));
                    Assert.True(text.Contains(said + ", so this copy is as of when it last heard from Discord.", StringComparison.Ordinal),
                        $"{path} should say \"{said}\"; it says \"{Regex.Match(text, @"As of .{0,60}?: MinerBot [^.]*\.").Value}\"");
                }
            }
        }

        // With nothing recorded at all, the list says so and nothing fails
        [Fact]
        public async Task EmptyListAndTheirLinks()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var admin = factory.Visitor("203.0.113.145");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            var html = await admin.Client.GetStringAsync("/admin/servers", Token);
            PageTests.AssertAccessible("an empty servers list", html);
            Assert.Contains("No server here.", Text(html));
            Assert.Contains("None: every server with settings on the website has MinerBot in it.", Text(html));
            Assert.Contains("there's no telling how current this copy is.", Text(html));
        }

        // Discord's names break where they must, beside a server's icon and as a category's heading, so a long one with no
        // space can't widen a phone's page or push the list's settings out of view. An ID on its own line in a table's cell,
        // one in a cell's sentence, such as the server a setting's channel is in, and a cell that's only an ID, stay whole,
        // as the audit table's addresses do, where anywhere would let the table cut them into pieces. .address itself still
        // breaks anywhere, for the raw text of an audit row and the bot's commit. A server's page heads itself with its ID,
        // 19 digits that can't break, about 306 px at the h1's 1.75rem in a bold system font, wider than a 320 px screen's
        // 288 px column, so the narrowest screens get a smaller h1, which fits it.
        [Fact]
        public async Task NamesBreakWhereTheyMustAndIdsStayWhole()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var css = await factory.Visitor().Client.GetStringAsync("/css/site.css", Token);

            string Rule(string selector) => Regex.Match(css, @"\n" + selector + @" \{[^}]*\}").Value;

            Assert.Contains("overflow-wrap: anywhere;", Rule(@"\.server-name"));
            Assert.Contains("overflow-wrap: anywhere;", Rule("h3"));
            Assert.Contains("overflow-wrap: break-word;", Rule(@"\.cell-line\.address,\r?\n\.cell-line \.address,\r?\ntd\.address"));
            Assert.Contains("overflow-wrap: anywhere;", Rule(@"\.address"));
            Assert.Matches(@"\n@media \(max-width: 22\.5rem\) \{\s*h1 \{\s*font-size: 1\.5rem;\s*\}\s*\}", css);
        }
    }
}
