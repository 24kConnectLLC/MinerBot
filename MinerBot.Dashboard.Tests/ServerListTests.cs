using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Servers;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Delivery;

namespace MinerBot.Dashboard.Tests
{
    // The servers list's Has problems, from what the bot really sent as well as the copy's permissions
    [Collection("Database")]
    public class ServerListTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        // Each server's channels are all ones MinerBot can post in, so only its posts can make it a problem. Failing: 3 tries in
        // a row of a kind that's on. Twice: 2. Gone: left. Off: the kind's channel is 0. Switched: the kind is switched off.
        const ulong Failing = 1000000000000000861, Twice = 1000000000000000862, Gone = 1000000000000000863, Off = 1000000000000000864,
            Switched = 1000000000000000865;

        static string Text(string html) => Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(html), "<[^>]+>", " "), "\\s+", " ");

        // A server's row in the servers list, as text, found by the ID it shows
        static string Row(string html, ulong id)
        {
            var at = html.IndexOf($"<p class=\"cell-line muted address\">{id}</p>", StringComparison.Ordinal);
            Assert.True(at >= 0, $"The list has no row for {id}");

            return Text(html[html.LastIndexOf("<tr>", at, StringComparison.Ordinal)..html.IndexOf("</tr>", at, StringComparison.Ordinal)]).Trim();
        }

        // The five servers, each with a channel for armor and maps but Off, whose armor is 0, and armor posts that failed the
        // tries in a row the list below gives, the last waiting an hour; Switched's armor went once before they began to fail
        static async Task SeedAsync(MariaDbFixture db, DateTime now)
        {
            foreach (var server in new[] { Failing, Twice, Gone, Off, Switched })
            {
                var channel = server % 1000;
                await db.BotGuilds().SaveGuild(StatusSteps.Server(server, "Server " + channel), [StatusSteps.Channel(channel, "general", 0)], now, StatusSteps.RunId, (uint)channel);
                await db.Guilds().SaveGuild(new Guild { guild_id = server, armor_channel_id = server == Off ? 0 : channel, map_channel_id = channel });
            }

            await db.BotGuilds().LeaveGuild(Gone, now, StatusSteps.RunId, 9, null);
            await PostSteps.SwitchesAsync(db, Switched, armor: false, maps: true, lobbies: true, now);
            await PostSteps.SentAsync(db, await PostSteps.QueueAsync(db, PostSource.Armor, 865, now.AddHours(-3), Switched), 865, now.AddHours(-3), 5000000000000000865);

            foreach (var (server, tries) in new[] { (Failing, 3), (Twice, 2), (Gone, 3), (Off, 3), (Switched, 3) })
                await PostSteps.RefusedAsync(db, server, PostSource.Armor, server % 1000, now.AddHours(-2), DeliveryReasons.DiscordPermissions, null, tries, now.AddHours(1));
        }

        // A kind whose posts keep failing makes its server a problem though every setting reads OK, counted in the tab and
        // listed under Has problems with why on its mark; 2 tries, a server the bot left, and a kind that's off or switched off
        // don't count
        [Fact]
        public async Task PostsThatKeepFailingCountAsAProblem()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            await SeedAsync(db, now);

            var admin = factory.Visitor("203.0.113.185");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            var list = await admin.Client.GetStringAsync("/admin/servers", Token);
            Assert.Contains("<a href=\"/admin/servers?show=problems\">Has problems (1)</a>", list);

            var problems = Text(await admin.Client.GetStringAsync("/admin/servers?show=problems", Token));
            Assert.Contains("Server 861", problems);
            Assert.Contains($"OK general Failing since {now.AddHours(-2):yyyy-MM-dd}", problems);
            Assert.DoesNotContain("Server 862", problems);
            Assert.DoesNotContain("Server 863", problems);
            Assert.DoesNotContain("Server 864", problems);
            Assert.DoesNotContain("Server 865", problems);
            PageTests.AssertAccessible("the servers list's problems", await admin.Client.GetStringAsync("/admin/servers?show=problems", Token));
        }

        // A kind that's off isn't tried, so the pages leave out what its record says of failing, which the bot clears only at its
        // next run that isn't paused, as Has problems does: no Failing since under its mark, and no failure or next try on its
        // server's page, which show its last post instead, or that none is recorded. A kind that's on still shows all of it.
        [Fact]
        public async Task KindThatIsOffIsNeverShownFailing()
        {
            var db = await fixture.ReadyBotAsync();
            await using var factory = new DashboardFactory(db);
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            await SeedAsync(db, now);

            var admin = factory.Visitor("203.0.113.186");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            var list = await admin.Client.GetStringAsync("/admin/servers", Token);
            Assert.EndsWith($"OK general Failing since {now.AddHours(-2):yyyy-MM-dd} OK general Off Off", Row(list, Failing));
            Assert.EndsWith("Off OK general Off Off", Row(list, Off));
            Assert.EndsWith("Switched off general Last post 3 hours ago OK general Off Off", Row(list, Switched));

            async Task<string> PageAsync(ulong id) => Text(await admin.Client.GetStringAsync("/admin/servers/" + id.ToString(CultureInfo.InvariantCulture), Token));

            Assert.Contains($"MinerBot tries again at {now.AddHours(1):HH:mm} UTC", await PageAsync(Failing));

            var off = await PageAsync(Off);
            Assert.Contains("Armor None Off The server turned this kind of post off. " + DeliveryWords.NoPostYet, off);
            Assert.DoesNotContain("Couldn't post since", off);
            Assert.DoesNotContain("MinerBot tries again", off);

            var switched = await PageAsync(Switched);
            Assert.Contains($"Armor general 865 Switched off {SwitchWords.SwitchedOffLine} {DeliveryWords.Posted(now.AddHours(-3))} {DeliveryWords.OpenIt}", switched);
            Assert.DoesNotContain("Couldn't post since", switched);
            Assert.DoesNotContain("MinerBot tries again", switched);
        }
    }
}
