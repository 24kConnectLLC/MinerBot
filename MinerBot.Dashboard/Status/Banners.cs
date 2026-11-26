using System.Globalization;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using MinerBot.Dashboard.Servers;

namespace MinerBot.Dashboard.Status
{
    // Something on the Overview that needs someone: what's wrong and what to do, with a link to where to do it
    public sealed record Banner(string Text, string LinkText, string Link);

    // How many armor wait for a review, and when the oldest was sent, as the website saved it, by its own clock
    public sealed record PendingArmor(long Count, DateTime? Oldest);

    // The Overview's reads of the website's database that failed after it answered the dashboard's check, such as one of a
    // table whose right the dashboard's login lost
    [Flags]
    public enum WebsiteReads
    {
        None = 0,
        Armor = 1,
        Settings = 2
    }

    // The Overview's banners, most urgent first. The parts that need the website's database are null while it doesn't
    // answer, and the banner that says so stands for them. A read that failed after it answered has a banner of its own.
    internal static class Banners
    {
        public const string DeployNotes = "https://github.com/24kConnectLLC/MinerBot/blob/main/deploy/README.md";
        public const string SteamSignIn = "https://github.com/24kConnectLLC/MinerBot#-steam-sign-in";
        public const string JobsLink = "/admin/bot#jobs";
        public const string PendingLink = "/admin/armor?status=pending";
        public const string ProblemsLink = "/admin/servers?show=problems";

        public static readonly TimeSpan ArmorWait = TimeSpan.FromDays(3);

        // The bot that runs keeps its posts only in memory, so the words and promises of the pages about posts that survive a
        // restart aren't true of it: an older bot, as ./rollback.sh bot puts back
        public const string OlderBot = "The bot that runs now is older than this dashboard: it ignores the switches, records no posts, and keeps what it hasn't "
            + "posted only in memory, so a restart loses it. Deploy the current bot.";

        // serversFailing is null when the record of the bot's posts couldn't be read
        public static IReadOnlyList<Banner> Of(StatusBox box, IReadOnlyList<JobStatusRow> jobs, PendingArmor? armor, int? serversCantPost,
            WebsiteReads failedReads = WebsiteReads.None, int? serversFailing = null)
        {
            var banners = new List<Banner>();
            var (status, state, now) = (box.Status, box.State, box.Now);

            if (status == null)
                banners.Add(new Banner("MinerBot hasn't checked in: there's no status row. Once the bot runs with BotConnection set, it writes one within seconds; if it runs and none comes, check it on the server with ./status.sh.",
                    "The deploy notes", DeployNotes));
            else if (state == BotState.Offline)
                banners.Add(new Banner($"MinerBot is offline: it last checked in {TimeWords.Ago(status.heartbeat_at, now)}, at {TimeWords.Utc(status.heartbeat_at)}. Check it on the server with ./status.sh.",
                    "The deploy notes", DeployNotes));
            else if (BotStates.StoppedTooLong(status, state, now))
                banners.Add(new Banner($"MinerBot stopped {TimeWords.Ago(status.stopped_at!.Value, now)}, at {TimeWords.Utc(status.stopped_at.Value)}, and hasn't started again, though a deploy's swap stops it for seconds only. Check it on the server with ./status.sh.",
                    "The deploy notes", DeployNotes));

            // Only while that bot runs: a row it left as it stopped, or one it hasn't written for 10 minutes, says nothing of
            // the bot that runs next
            if (status != null && state is (BotState.Online or BotState.Late) && !PostingRules.KeepsPosts(status))
                banners.Add(new Banner(OlderBot, "The deploy notes", DeployNotes));

            // Only while MinerBot's own database answers, which the page needs. status.sh never asks the website's database
            // anything: its look asks MariaDB nothing, and its nightly --check only looks for triggers, events and routines in
            // MinerBot's. So this names what can put the website's side right: the login's rights there, or a database renamed
            if (!box.WebsiteAnswers)
                banners.Add(new Banner("The website's database isn't answering the dashboard, so pending armor and the servers' settings can't be shown here, and the armor, blacklist and servers pages won't open. If the website itself works, the dashboard's login may have lost its rights there, which ./setup-dashboard.sh --no-questions applies again; the deploy notes say what to do when a database's name changes.",
                    "The deploy notes", DeployNotes));

            // It answered the check, which the status box shows as its refresh does, but a read after that failed, so this
            // names that read and the pages that make it, rather than saying the database doesn't answer
            else if (failedReads != WebsiteReads.None)
                banners.Add(new Banner(ReadFailed(failedReads) + " If it goes on, the dashboard's login may have lost its rights there, which ./setup-dashboard.sh --no-questions applies again.",
                    "The deploy notes", DeployNotes));

            if (status != null && BotStates.DiscordDownTooLong(status, state, now))
            {
                var since = status.discord_since ?? status.started_at;
                var how = status.discord_state == DiscordState.Connecting ? "connecting to Discord" : "disconnected from Discord";
                banners.Add(new Banner($"MinerBot has been {how} since {TimeWords.Utc(since)}, {TimeWords.Ago(since, now)}. It reconnects by itself; if it doesn't, check its log on the server.",
                    "The deploy notes", DeployNotes));
            }

            if (status != null && BotStates.SteamNeedsSignIn(status))
            {
                var since = status.steam_since is { } time ? $", since {TimeWords.Utc(time)}" : "";
                var answer = status.steam_last_result is { Length: > 0 } result ? $" Steam's last answer was {result}." : "";
                banners.Add(new Banner($"Steam needs a sign-in{since}.{answer} Sign in again as the Steam Sign-In steps say.", "Steam Sign-In", SteamSignIn));
            }

            // A token past its end can't be renewed any more, so there's no "before then" to sign in by
            if (status != null && BotStates.TokenEndsSoon(status, now))
            {
                var end = TimeWords.Day(status.steam_token_expires!.Value);
                var days = BotStates.TokenDaysLeft(status, now)!.Value;
                var text = days switch
                {
                    < 0 => $"The Steam sign-in token ended on {end}, so the bot can't log on to Steam with it again: sign in again, as the Steam Sign-In steps say.",
                    0 => Renewing($"ends today, {end}"),
                    _ => Renewing($"ends on {end}, in {TimeWords.Count(days, "day")}")
                };

                banners.Add(new Banner(text, "Steam Sign-In", SteamSignIn));
            }

            // A run is skipped only while the one before it, which was cut off, still runs, which its detail says
            foreach (var job in jobs.Where(job => JobWords.IsProblem(job.last_result)))
            {
                var what = job.last_result switch
                {
                    JobResult.Failed => "failed",
                    JobResult.CutOff => "was cut off by the time limit",
                    _ => "was skipped"
                };
                var at = job.last_started_at is { } started ? $" at {TimeWords.Utc(started)}" : "";
                var detail = string.IsNullOrEmpty(job.last_detail) ? "" : $": {job.last_detail}";

                banners.Add(new Banner($"The last run of {JobWords.Name(job.job)} {what}{at}{detail}.", "Its runs on the Bot status page", JobsLink));
            }

            if (serversCantPost is int count && count > 0)
                banners.Add(new Banner($"{TimeWords.Count(count, "server")} {(count == 1 ? "has" : "have")} a setting MinerBot can't post in.", "See them", ProblemsLink));

            if (serversFailing is int failing && failing > 0)
                banners.Add(new Banner(DeliveryWords.Banner(failing), "See them", ProblemsLink));

            if (armor is { Oldest: { } oldest } && WebsiteClock.ToUtc(oldest) is { } sent && now - sent >= ArmorWait)
                banners.Add(new Banner($"{armor.Count.ToString("N0", CultureInfo.InvariantCulture)} armor {(armor.Count == 1 ? "is" : "are")} waiting for a review, the oldest since {oldest.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} by the website's clock, {TimeWords.Span(now - sent)} ago.",
                    "Review them", PendingLink));

            return banners;
        }

        // A token that hasn't ended yet, which the daily renewal should have kept further off
        static string Renewing(string when) =>
            $"The Steam sign-in token {when}. The daily renewal usually keeps it further off than {BotStates.TokenWarningDays} days, so it may not be renewing: sign in again before then, as the Steam Sign-In steps say.";

        static string ReadFailed(WebsiteReads failed) => failed switch
        {
            WebsiteReads.Armor => "The website's database answers the dashboard, but reading pending armor from it failed, so it can't be shown here, and the armor pages may fail the same way.",
            WebsiteReads.Settings => "The website's database answers the dashboard, but reading the servers' settings from it failed, so this page can't check them, and the servers pages may fail the same way.",
            _ => "The website's database answers the dashboard, but reading pending armor and the servers' settings from it failed, so neither can be shown here, and the armor and servers pages may fail the same way."
        };
    }

    // The website's MariaDB runs on the server's clock, US Eastern, and saves an armor's CreatedAt by it, with no zone. The
    // bot finds the zone by the same name, in the same base image.
    internal static class WebsiteClock
    {
        static readonly TimeZoneInfo? Eastern = Find();

        // Null when the zone can't be found, which leaves the age unknown rather than wrong. A time in the hour the clocks
        // skip in spring can't be on the clock, so it's read as the hour after.
        public static DateTime? ToUtc(DateTime saved)
        {
            if (Eastern == null)
                return null;

            var time = DateTime.SpecifyKind(saved, DateTimeKind.Unspecified);

            if (Eastern.IsInvalidTime(time))
                time = time.AddHours(1);

            return TimeZoneInfo.ConvertTimeToUtc(time, Eastern);
        }

        // The website's clock at a UTC time, for the tests' armor
        public static DateTime? FromUtc(DateTime utc) => Eastern == null ? null : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Eastern);

        static TimeZoneInfo? Find()
        {
            foreach (var id in new[] { "Eastern Standard Time", "America/New_York" })
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                }
            }

            return null;
        }
    }
}
