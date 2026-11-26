using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using MinerBot.Dashboard.Status;

namespace MinerBot.Dashboard.Tests
{
    // The Overview's banners about which bot runs and about its posts. No database.
    public class BannerTests
    {
        static readonly DateTime Now = new(2026, 11, 24, 14, 3, 30, DateTimeKind.Utc);

        static BotStatusRow Row(DateTime heartbeat, ulong? postingRun, DateTime? stopped = null) => new()
        {
            run_id = 5,
            posting_run_id = postingRun,
            version = "5a62c9fcc6112c68e671285da53fb25369ea1407",
            started_at = heartbeat.AddHours(-1),
            heartbeat_at = heartbeat,
            stopped_at = stopped,
            discord_state = DiscordState.Connected,
            discord_since = heartbeat.AddHours(-1),
            steam_state = SteamState.LoggedOn,
            steam_since = heartbeat.AddHours(-1)
        };

        static IReadOnlyList<Banner> Of(BotStatusRow? row, int? failing = null) =>
            Banners.Of(new StatusBox(row, BotStates.Of(row, Now), true, Now), [], null, null, serversFailing: failing);

        // The bot that runs is older than the dashboard while its status row's posting_run_id isn't its own run: never written,
        // as before PR 11, or left by a newer bot, as after ./rollback.sh bot. Only while that bot is online or late: a row a
        // stopped or offline bot left says nothing of the bot that runs next, and with no row the missing bot's banner speaks.
        [Fact]
        public void OlderBotBannerShowsOnlyForAnOlderBot()
        {
            var older = Assert.Single(Of(Row(Now.AddSeconds(-10), null)));
            Assert.Equal(Banners.OlderBot, older.Text);
            Assert.Equal(("The deploy notes", Banners.DeployNotes), (older.LinkText, older.Link));
            Assert.Equal("The bot that runs now is older than this dashboard: it ignores the switches, records no posts, and keeps what it hasn't posted only in memory, so a restart loses it. Deploy the current bot.",
                Banners.OlderBot);

            Assert.Equal(Banners.OlderBot, Assert.Single(Of(Row(Now.AddSeconds(-10), 4))).Text);
            Assert.Equal(Banners.OlderBot, Assert.Single(Of(Row(Now.AddMinutes(-5), null))).Text);
            Assert.Empty(Of(Row(Now.AddSeconds(-10), 5)));
            Assert.Empty(Of(Row(Now.AddMinutes(-5), 5)));

            Assert.DoesNotContain(Of(Row(Now.AddMinutes(-11), null)), banner => banner.Text == Banners.OlderBot);
            Assert.Empty(Of(Row(Now.AddMinutes(-2), null, stopped: Now.AddMinutes(-2))));
            Assert.DoesNotContain(Of(null), banner => banner.Text == Banners.OlderBot);
        }

        // The posts that keep failing: a banner with the count of servers, in the plain words of one or several, linking to the
        // servers list's problems, and none for none or for a record that couldn't be read
        [Fact]
        public void PostsThatKeepFailingHaveTheirBanner()
        {
            var row = Row(Now.AddSeconds(-10), 5);

            var one = Assert.Single(Of(row, 1));
            Assert.Equal("1 server's posts keep failing: MinerBot couldn't post there in 3 or more tries in a row.", one.Text);
            Assert.Equal(("See them", Banners.ProblemsLink), (one.LinkText, one.Link));
            Assert.Equal("12 servers' posts keep failing: MinerBot couldn't post there in 3 or more tries in a row.", Assert.Single(Of(row, 12)).Text);
            Assert.Empty(Of(row, 0));
            Assert.Empty(Of(row, null));
        }
    }
}
