using System.Globalization;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using MinerBot.Dashboard.Status;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // The status pages' rules at their edges, on a fixed clock: the bot's state, its banners, uptime, the events' words
    // and the jobs' schedules. No database.
    public class StatusRulesTests
    {
        static readonly DateTime Now = new(2026, 11, 19, 14, 3, 30, 250, DateTimeKind.Utc);

        // A bot that keeps its posts in MinerBot's database, as the bot this dashboard ships with writes its row
        static BotStatusRow Row(DateTime heartbeat, DateTime? stopped = null, string discord = DiscordState.Connected, DateTime? discordSince = null,
            string steam = SteamState.LoggedOn, DateTime? tokenEnds = null) => new()
        {
            run_id = 1,
            posting_run_id = 1,
            version = StatusSteps.Commit,
            started_at = heartbeat.AddHours(-1),
            heartbeat_at = heartbeat,
            stopped_at = stopped,
            discord_state = discord,
            discord_since = discordSince ?? heartbeat.AddHours(-1),
            steam_state = steam,
            steam_since = heartbeat.AddHours(-1),
            steam_token_expires = tokenEnds
        };

        static StatusBox Box(BotStatusRow? row, bool website = true) => new(row, BotStates.Of(row, Now), website, Now);

        // Online up to 90 seconds after the last write, late up to 10 minutes, offline after that
        [Theory]
        [InlineData(0, BotState.Online)]
        [InlineData(90_000, BotState.Online)]
        [InlineData(90_001, BotState.Late)]
        [InlineData(600_000, BotState.Late)]
        [InlineData(600_001, BotState.Offline)]
        [InlineData(-5_000, BotState.Online)]
        public void StateFollowsTheHeartbeatsAge(int millisecondsAgo, BotState state)
        {
            Assert.Equal(state, BotStates.Of(Row(Now.AddMilliseconds(-millisecondsAgo)), Now));
        }

        [Fact]
        public void NoRowIsOffline()
        {
            Assert.Equal(BotState.Offline, BotStates.Of(null, Now));
        }

        // A clean stop writes its time as the heartbeat too, so it's stopped whatever its age. A stop older than the row's
        // last write isn't the run's last write.
        [Fact]
        public void CleanStopIsStoppedWhateverItsAge()
        {
            Assert.Equal(BotState.Stopped, BotStates.Of(Row(Now.AddHours(-3), stopped: Now.AddHours(-3)), Now));
            Assert.Equal(BotState.Stopped, BotStates.Of(Row(Now.AddSeconds(-5), stopped: Now.AddSeconds(-5)), Now));
            Assert.Equal(BotState.Stopped, BotStates.Of(Row(Now.AddSeconds(-5), stopped: Now.AddSeconds(-4)), Now));
            Assert.Equal(BotState.Online, BotStates.Of(Row(Now.AddSeconds(-5), stopped: Now.AddSeconds(-6)), Now));
        }

        // A swap stops the bot for seconds and Discord drops it now and then, so each says so only after 5 minutes, and
        // Discord only while the bot itself runs
        [Fact]
        public void StoppedAndDiscordWaitFiveMinutes()
        {
            var fiveMinutes = Now.AddMinutes(-5);
            Assert.False(BotStates.StoppedTooLong(Row(fiveMinutes, stopped: fiveMinutes), BotState.Stopped, Now));
            Assert.True(BotStates.StoppedTooLong(Row(fiveMinutes.AddMilliseconds(-1), stopped: fiveMinutes.AddMilliseconds(-1)), BotState.Stopped, Now));

            Assert.False(BotStates.DiscordDownTooLong(Row(Now, discord: DiscordState.Disconnected, discordSince: fiveMinutes), BotState.Online, Now));
            var down = Row(Now, discord: DiscordState.Connecting, discordSince: fiveMinutes.AddMilliseconds(-1));
            Assert.True(BotStates.DiscordDownTooLong(down, BotState.Online, Now));
            Assert.False(BotStates.DiscordDownTooLong(down, BotState.Late, Now));
            Assert.False(BotStates.DiscordDownTooLong(down, BotState.Offline, Now));
            Assert.False(BotStates.DiscordDownTooLong(Row(Now, discordSince: Now.AddDays(-1)), BotState.Online, Now));
        }

        // The token's days left are UTC days, and the warning starts 30 days before its end
        [Theory]
        [InlineData(196, false)]
        [InlineData(31, false)]
        [InlineData(30, true)]
        [InlineData(1, true)]
        [InlineData(0, true)]
        [InlineData(-1, true)]
        public void TokenWarningStartsThirtyDaysBefore(int days, bool soon)
        {
            var row = Row(Now, tokenEnds: Now.Date.AddDays(days));

            Assert.Equal(days, BotStates.TokenDaysLeft(row, Now));
            Assert.Equal(soon, BotStates.TokenEndsSoon(row, Now));
        }

        [Fact]
        public void NoTokenEndIsNoWarning()
        {
            Assert.Null(BotStates.TokenDaysLeft(Row(Now), Now));
            Assert.False(BotStates.TokenEndsSoon(Row(Now), Now));
            Assert.False(BotStates.TokenEndsSoon(null, Now));
        }

        // A link only from a commit's hex digits, as the bot writes them
        [Theory]
        [InlineData(StatusSteps.Commit, "https://github.com/24kConnectLLC/MinerBot/commit/" + StatusSteps.Commit)]
        [InlineData("5a62c9f", "https://github.com/24kConnectLLC/MinerBot/commit/5a62c9f")]
        [InlineData("5a62c9", null)]
        [InlineData("5A62C9F", null)]
        [InlineData("5a62c9f/../../x", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void CommitLinksOnlyFromHex(string? version, string? link)
        {
            Assert.Equal(link, BotStates.CommitLink(version));
        }

        // Where the record begins: the first minute kept, unless a day before it was totalled
        [Fact]
        public void RecordStartsAtTheFirstMinuteUnlessADayCameBefore()
        {
            var first = new DateTime(2026, 11, 15, 11, 26, 0, DateTimeKind.Utc);

            Assert.Null(UptimeDays.RecordStart(new UptimeStartRow()));
            Assert.Equal(first, UptimeDays.RecordStart(new UptimeStartRow { first_minute = first }));
            Assert.Equal(first, UptimeDays.RecordStart(new UptimeStartRow { first_minute = first, first_day = first.Date }));
            Assert.Equal(first.Date.AddDays(-1), UptimeDays.RecordStart(new UptimeStartRow { first_minute = first, first_day = first.Date.AddDays(-1) }));
            Assert.Equal(first.Date, UptimeDays.RecordStart(new UptimeStartRow { first_day = first.Date }));
        }

        static UptimeDayRow Day(int month, int day, int sampled, int discord, int steam) =>
            new() { day = new DateTime(2026, month, day, 0, 0, 0, DateTimeKind.Utc), sampled_minutes = sampled, discord_minutes = discord, steam_minutes = steam };

        // The live days: 2026-11-15 counts from 11:26, the days before it are no record rather than down, and today stops at
        // the current minute
        [Fact]
        public void DaysCountFromTheFirstMinuteToTheCurrentOne()
        {
            var start = new DateTime(2026, 11, 15, 11, 26, 0, DateTimeKind.Utc);
            var totalled = new[] { Day(11, 15, 754, 753, 753), Day(11, 16, 1440, 1440, 1440), Day(11, 17, 1440, 1439, 1422), Day(11, 18, 1440, 1439, 1437) };
            var sampled = new[] { Day(11, 19, 843, 843, 843) };

            var days = UptimeDays.Days(Now, start, totalled, sampled);

            Assert.Equal(UptimeDays.StripDays, days.Count);
            Assert.Equal(new DateTime(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc), days[0].Day);
            Assert.Equal(Now.Date, days[^1].Day);
            Assert.All(days.Where(day => day.Day < start.Date), day => Assert.False(day.HasRecord));
            Assert.All(days.Where(day => day.Day < start.Date), day => Assert.Equal("none", UptimeDays.Band(day)));

            Assert.Equal(new UptimeDay(start.Date, 754, 753, 753), days.Single(day => day.Day == start.Date));
            Assert.Equal(new UptimeDay(new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc), 1440, 1439, 1422), days[^3]);

            // 14:03:30 is 843 whole minutes into the day: the minute still being sampled isn't counted yet
            Assert.Equal(new UptimeDay(Now.Date, 14 * 60 + 3, 843, 843), days[^1]);

            Assert.Equal("99.93%", UptimeDays.Percent(1439, 1440));
            Assert.Equal("top", UptimeDays.Band(days[^3]));
            Assert.Equal(new UptimePeriod("Last 7 days", 754 + 1440 * 3 + 843, 753 + 1440 + 1439 + 1439 + 843, 753 + 1440 + 1422 + 1437 + 843),
                UptimeDays.Period("Last 7 days", days, 7));
        }

        // A day after the record began with no samples was a day the bot didn't run: down all day, never no record
        [Fact]
        public void DayWithoutSamplesIsDown()
        {
            var start = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
            var days = UptimeDays.Days(Now, start, [Day(11, 16, 1440, 1440, 1440)], []);

            var missing = days.Single(day => day.Day == new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(new UptimeDay(missing.Day, 1440, 0, 0), missing);
            Assert.Equal("poor", UptimeDays.Band(missing));
            Assert.Equal("0.00%", UptimeDays.Percent(missing.DiscordMinutes, missing.Minutes));
        }

        // A totalled day wins over its samples, and no count can go over the day's minutes
        [Fact]
        public void TotalsWinAndCountsStayInTheDay()
        {
            var start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
            var days = UptimeDays.Days(Now, start, [Day(11, 18, 1440, 1400, 1400)], [Day(11, 18, 1440, 1440, 1440), Day(11, 19, 900, 900, 900)]);

            Assert.Equal(1400, days[^2].DiscordMinutes);
            Assert.Equal(843, days[^1].DiscordMinutes);
        }

        // Each period is the strip's last days, today among them: the day just inside each one counts, and the day before it
        // doesn't. The record began long before the strip, and every day was up apart from a few minutes on the days either
        // side of each period's first, counting back from today as the first: 1 on the 7th, 2 on the 8th, 4 on the 30th, 8
        // on the 31st and 16 on the 90th, the strip's first.
        [Fact]
        public void PeriodsAreTheirOwnLastDays()
        {
            var down = new Dictionary<int, int> { [7] = 1, [8] = 2, [30] = 4, [31] = 8, [90] = 16 };
            var totalled = Enumerable.Range(2, UptimeDays.StripDays - 1).Select(back => new UptimeDayRow
            {
                day = Now.Date.AddDays(1 - back),
                sampled_minutes = 1440,
                discord_minutes = 1440 - down.GetValueOrDefault(back),
                steam_minutes = 1440
            }).ToList();

            var days = UptimeDays.Days(Now, Now.Date.AddDays(-200), totalled, [Day(11, 19, 843, 843, 843)]);

            // Today's 843 minutes and the whole days before it
            static UptimePeriod Expected(string label, int count, int minutesDown) =>
                new(label, (count - 1) * 1440 + 843, (count - 1) * 1440 + 843 - minutesDown, (count - 1) * 1440 + 843);

            Assert.Equal(Expected("Last 7 days", 7, 1), UptimeDays.Period("Last 7 days", days, 7));
            Assert.Equal(Expected("Last 30 days", 30, 1 + 2 + 4), UptimeDays.Period("Last 30 days", days, 30));
            Assert.Equal(Expected("Last 90 days", 90, 1 + 2 + 4 + 8 + 16), UptimeDays.Period("Last 90 days", days, UptimeDays.StripDays));
        }

        // The samples are read from the day after the last one totalled, or from the strip's start
        [Fact]
        public void SamplesAreReadAfterTheLastTotal()
        {
            Assert.Equal(UptimeDays.StripStart(Now), UptimeDays.SampleDaysFrom(Now, []));
            Assert.Equal(new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc), UptimeDays.SampleDaysFrom(Now, [Day(11, 17, 1, 1, 1), Day(11, 18, 1, 1, 1)]));
            Assert.Equal(UptimeDays.StripStart(Now), UptimeDays.SampleDaysFrom(Now, [Day(1, 1, 1, 1, 1)]));
        }

        // The last 24 hours end at the current minute, and start at the record's start when that's later
        [Fact]
        public void LastDayIsTheSamplesUpToTheCurrentMinute()
        {
            var samples = new UptimeTotalRow { sampled_minutes = 1440, discord_minutes = 1439, steam_minutes = 1440 };

            Assert.Equal(new UptimePeriod("Last 24 hours", 1440, 1439, 1440), UptimeDays.LastDay(Now, Now.AddDays(-3), samples));
            Assert.Equal(new UptimePeriod("Last 24 hours", 63, 63, 63), UptimeDays.LastDay(Now, new DateTime(2026, 11, 19, 13, 0, 0, DateTimeKind.Utc), samples));
            Assert.False(UptimeDays.LastDay(Now, null, new UptimeTotalRow()).HasRecord);
        }

        // Cut to two places, so 100.00% means not one minute down
        [Theory]
        [InlineData(1440, 1440, "100.00%")]
        [InlineData(129599, 129600, "99.99%")]
        [InlineData(1, 3, "33.33%")]
        [InlineData(2, 3, "66.66%")]
        [InlineData(0, 1440, "0.00%")]
        [InlineData(0, 0, "no record")]
        public void PercentIsCutNotRounded(int up, int minutes, string shown)
        {
            Assert.Equal(shown, UptimeDays.Percent(up, minutes));
        }

        // The strip's bands, each line on its upper side
        [Theory]
        [InlineData(1000, 1000, "top")]
        [InlineData(999, 1000, "top")]
        [InlineData(998, 1000, "good")]
        [InlineData(990, 1000, "good")]
        [InlineData(989, 1000, "fair")]
        [InlineData(950, 1000, "fair")]
        [InlineData(949, 1000, "poor")]
        [InlineData(0, 1000, "poor")]
        [InlineData(0, 0, "none")]
        public void StripBandsByDiscordUptime(int up, int minutes, string band)
        {
            var day = new UptimeDay(Now.Date, minutes, up, minutes);

            Assert.Equal(band, UptimeDays.Band(day));
            Assert.False(string.IsNullOrEmpty(UptimeDays.BandLabel(band)));
        }

        [Fact]
        public void TooltipHoldsTheNumbers()
        {
            Assert.Equal("2026-11-17: Discord 99.93% (1,439 of 1,440 minutes), Steam 98.75% (1,422 of 1,440 minutes)",
                UptimeDays.Tooltip(new UptimeDay(new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc), 1440, 1439, 1422)));
            Assert.Equal("2026-08-22: no record", UptimeDays.Tooltip(new UptimeDay(new DateTime(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc), 0, 0, 0)));
        }

        // Discord asking the bot to reconnect is routine, exactly that detail on exactly that kind; any other drop stays as
        // the bot recorded it
        [Theory]
        [InlineData("discord.disconnected", "GatewayReconnectException", true)]
        [InlineData("discord.disconnected", "WebSocketException", false)]
        [InlineData("discord.disconnected", "WebSocketException, close code 1001", false)]
        [InlineData("discord.disconnected", "gatewayreconnectexception", false)]
        [InlineData("discord.disconnected", "GatewayReconnectException ", false)]
        [InlineData("discord.disconnected", null, false)]
        [InlineData("discord.connected", "GatewayReconnectException", false)]
        public void RoutineIsDiscordsReconnectOnly(string kind, string? detail, bool routine)
        {
            Assert.Equal(routine, EventWords.IsRoutine(kind, detail));
            Assert.Equal(routine ? "Routine" : "Warning", EventWords.LevelLabel(EventLevel.Warning, kind, detail));
            Assert.Equal(routine ? EventWords.RoutineLabel : EventWords.Kind(kind)!.Label, EventWords.Label(kind, detail));
        }

        // Every kind the bot can record has a label of its own, read from the bot's own lists without referencing the bot,
        // and the list holds nothing else
        [Fact]
        public void EveryKindTheBotRecordsHasALabel()
        {
            var kinds = BotSource.EventKinds();

            // The bot records by BotEvents' list, which also names other classes' constants, as SteamEvents'. A kind it
            // names from a file not read here would otherwise go without a label and without a failure.
            var listed = BotSource.ListedKinds();
            Assert.True(listed.Count == kinds.Count, $"BotEvents' list names {listed.Count} kinds ({string.Join(", ", listed)}), and BotEvents.cs and " +
                $"SteamStatus.cs declare {kinds.Count}: a kind declared in another file needs that file among the bot-source items in " +
                "MinerBot.Dashboard.Tests.csproj, and a list written another way needs BotSource to read it");

            Assert.Equal(25, kinds.Count);
            Assert.Equal(kinds, EventWords.Kinds.Select(word => word.Kind).Order(StringComparer.Ordinal));
            Assert.Equal(EventWords.Kinds.Count, EventWords.Kinds.Select(word => word.Label).Distinct().Count());
            Assert.All(EventWords.Kinds, word => Assert.NotEqual(word.Kind, EventWords.Label(word.Kind, null)));
            Assert.Equal(RoutineReconnect.Kind, EventWords.DiscordDisconnected);
        }

        // A kind or a level a later bot records is shown as it's stored, which the page encodes
        [Fact]
        public void UnknownWordsAreShownAsStored()
        {
            Assert.Null(EventWords.Kind("bot.renamed"));
            Assert.Equal("bot.renamed", EventWords.Label("bot.renamed", null));
            Assert.Equal("critical", EventWords.LevelLabel("critical", "bot.renamed", null));
            Assert.Equal(["Info", "Warning", "Error"], EventLevel.All.Select(level => EventWords.LevelLabel(level, "bot.started", null)));
        }

        // The seven schedules the bot has, and the forms around them, in Eastern time as the bot runs them
        [Theory]
        [InlineData("0 * * * *", "every hour, on the hour")]
        [InlineData("21 4 * * *", "every day at 04:21 Eastern")]
        [InlineData("*/5 * * * *", "every 5 minutes")]
        [InlineData("30 4 * * *", "every day at 04:30 Eastern")]
        [InlineData("0 12 * * *", "every day at 12:00 Eastern")]
        [InlineData("2-59/5 * * * *", "every 5 minutes, from 2 minutes past the hour")]
        [InlineData("33 4 * * *", "every day at 04:33 Eastern")]
        [InlineData("* * * * *", "every minute")]
        [InlineData("*/1 * * * *", "every minute")]
        [InlineData("15 * * * *", "every hour at 15 minutes past")]
        [InlineData("0 0 * * *", "every day at 00:00 Eastern")]
        [InlineData("0-59/5 * * * *", "every 5 minutes")]
        [InlineData("1-59/10 * * * *", "every 10 minutes, from 1 minute past the hour")]
        [InlineData("7-59/1 * * * *", "every minute, from 7 minutes past the hour")]
        [InlineData("0 0 1 * *", null)]
        [InlineData("0 4 * * 1", null)]
        [InlineData("*/0 * * * *", null)]
        [InlineData("60 * * * *", null)]
        [InlineData("0 24 * * *", null)]
        [InlineData("+5 * * * *", null)]
        [InlineData("2-58/5 * * * *", null)]
        [InlineData("60-59/5 * * * *", null)]
        [InlineData("2-59/0 * * * *", null)]
        [InlineData("-59/5 * * * *", null)]
        [InlineData("2-59/5/5 * * * *", null)]
        [InlineData("2-59/5 4 * * *", null)]
        [InlineData("0 * * *", null)]
        [InlineData("", null)]
        public void SchedulesInWords(string cron, string? words)
        {
            Assert.Equal(words, JobWords.Schedule(cron));
        }

        // Every job the bot runs has a name of its own on the Bot page and its schedule in words, read from the bot's own job
        // classes without referencing the bot, so a job the bot gains without them fails here
        [Fact]
        public void EveryJobTheBotRunsHasANameAndASchedule()
        {
            var jobs = BotSource.Jobs();

            Assert.Equal(
            [
                ("ArmorJob.AddColorArrays", "0 * * * *"),
                ("GuildMembersJob.CountMembers", "21 4 * * *"),
                ("GuildNotificationJob.NotifyAllGuilds", "*/5 * * * *"),
                ("HousekeepingJob.ApplyRetention", "30 4 * * *"),
                ("PostingRetentionJob.ApplyRetention", "33 4 * * *"),
                ("SteamTokenJob.RenewSteamToken", "0 12 * * *"),
                ("SubmissionAlertJob.AlertOwner", "2-59/5 * * * *")
            ], jobs);
            Assert.All(jobs, job => Assert.NotEqual(job.Job, JobWords.Name(job.Job)));
            Assert.All(jobs, job => Assert.NotNull(JobWords.Schedule(job.Cron)));
            Assert.Equal(jobs.Count, jobs.Select(job => JobWords.Name(job.Job)).Distinct().Count());
            Assert.Equal(["New-submission alerts", "Post records clean-up"], new[] { "SubmissionAlertJob.AlertOwner", "PostingRetentionJob.ApplyRetention" }.Select(JobWords.Name));
        }

        [Theory]
        [InlineData(0, "0 seconds")]
        [InlineData(59_999, "59 seconds")]
        [InlineData(60_000, "1 minute")]
        [InlineData(3_599_999, "59 minutes")]
        [InlineData(3_600_000, "1 hour")]
        [InlineData(86_399_999, "23 hours")]
        [InlineData(86_400_000, "1 day")]
        [InlineData(259_200_000, "3 days")]
        public void SpansInTheirLargestWholeUnit(long milliseconds, string words)
        {
            Assert.Equal(words, TimeWords.Span(TimeSpan.FromMilliseconds(milliseconds)));
        }

        [Fact]
        public void AgesAndDurations()
        {
            Assert.Equal("just now", TimeWords.Ago(Now.AddMilliseconds(-999), Now));
            Assert.Equal("just now", TimeWords.Ago(Now.AddSeconds(5), Now));
            Assert.Equal("12 seconds ago", TimeWords.Ago(Now.AddSeconds(-12), Now));
            Assert.Equal("in 4 minutes", TimeWords.Until(Now.AddMinutes(4), Now));
            Assert.Equal("2 hours ago", TimeWords.Until(Now.AddHours(-2), Now));
            Assert.Equal("2026-11-19 14:03:30 UTC", TimeWords.Utc(Now));

            Assert.Equal("999 ms", TimeWords.Duration(999));
            Assert.Equal("1.0 s", TimeWords.Duration(1000));
            Assert.Equal("1.2 s", TimeWords.Duration(1299));
            Assert.Equal("59.9 s", TimeWords.Duration(59_999));
            Assert.Equal("1 min 0 s", TimeWords.Duration(60_000));
            Assert.Equal("4 min 30 s", TimeWords.Duration(270_000));
        }

        // Nothing needs anyone while everything is up
        [Fact]
        public void NoBannersWhileEverythingIsUp()
        {
            var row = Row(Now.AddSeconds(-10), tokenEnds: Now.Date.AddDays(196));

            Assert.Empty(Banners.Of(Box(row), [], new PendingArmor(0, null), 0));
        }

        // Steam drops the bot now and then, and the bot logs on again by itself: offline says so in the status box, and only
        // a needed sign-in is a banner
        [Fact]
        public void SteamOfflineIsNoBanner()
        {
            var row = Row(Now.AddSeconds(-10), steam: SteamState.Offline, tokenEnds: Now.Date.AddDays(196));

            Assert.Equal("Offline", BotStates.SteamLabel(row.steam_state));
            Assert.False(BotStates.SteamNeedsSignIn(row));
            Assert.Empty(Banners.Of(Box(row), [], new PendingArmor(0, null), 0));
        }

        [Fact]
        public void OfflineMissingAndStoppedBanners()
        {
            Assert.Contains("MinerBot hasn't checked in: there's no status row.", Assert.Single(Banners.Of(Box(null), [], null, null)).Text);

            var offline = Assert.Single(Banners.Of(Box(Row(Now.AddMinutes(-11))), [], null, null));
            Assert.Equal("MinerBot is offline: it last checked in 11 minutes ago, at 2026-11-19 13:52:30 UTC. Check it on the server with ./status.sh.", offline.Text);
            Assert.Equal(Banners.DeployNotes, offline.Link);

            Assert.Empty(Banners.Of(Box(Row(Now.AddMinutes(-9))), [], null, null));
            Assert.Empty(Banners.Of(Box(Row(Now.AddMinutes(-5), stopped: Now.AddMinutes(-5))), [], null, null));
            Assert.StartsWith("MinerBot stopped 6 minutes ago", Assert.Single(Banners.Of(Box(Row(Now.AddMinutes(-6), stopped: Now.AddMinutes(-6))), [], null, null)).Text);
        }

        // The website's banner names every page that needs that database, and sends the owner to what can put it right:
        // status.sh never asks the website's database anything (its look asks MariaDB nothing, and its nightly --check only
        // looks for triggers, events and routines in MinerBot's), and this banner only shows while MinerBot's own database
        // answers. A token near its end is to be renewed before then; one past its end can only be replaced.
        [Fact]
        public void WebsiteDiscordAndSteamBanners()
        {
            var website = Assert.Single(Banners.Of(Box(Row(Now), website: false), [], null, null));
            Assert.Equal("The website's database isn't answering the dashboard, so pending armor and the servers' settings can't be shown here, and the armor, blacklist and servers pages won't open. If the website itself works, the dashboard's login may have lost its rights there, which ./setup-dashboard.sh --no-questions applies again; the deploy notes say what to do when a database's name changes.",
                website.Text);
            Assert.DoesNotContain("status.sh", website.Text);
            Assert.Equal(Banners.DeployNotes, website.Link);

            Assert.StartsWith("MinerBot has been disconnected from Discord since 2026-11-19 13:57:30 UTC, 6 minutes ago.",
                Assert.Single(Banners.Of(Box(Row(Now, discord: DiscordState.Disconnected, discordSince: Now.AddMinutes(-6))), [], null, null)).Text);

            var steam = Row(Now, steam: SteamState.NeedsSignIn);
            steam.steam_last_result = "InvalidPassword";
            var signIn = Assert.Single(Banners.Of(Box(steam), [], null, null));
            Assert.Equal("Steam needs a sign-in, since 2026-11-19 13:03:30 UTC. Steam's last answer was InvalidPassword. Sign in again as the Steam Sign-In steps say.", signIn.Text);
            Assert.Equal(Banners.SteamSignIn, signIn.Link);

            var token = Assert.Single(Banners.Of(Box(Row(Now, tokenEnds: Now.Date.AddDays(30))), [], null, null));
            Assert.Equal("The Steam sign-in token ends on 2026-12-19, in 30 days. The daily renewal usually keeps it further off than 30 days, so it may not be renewing: sign in again before then, as the Steam Sign-In steps say.", token.Text);
            Assert.Equal(Banners.SteamSignIn, token.Link);
            Assert.Empty(Banners.Of(Box(Row(Now, tokenEnds: Now.Date.AddDays(31))), [], null, null));
            Assert.StartsWith("The Steam sign-in token ends today, 2026-11-19. The daily renewal", Assert.Single(Banners.Of(Box(Row(Now, tokenEnds: Now.Date)), [], null, null)).Text);

            var ended = Assert.Single(Banners.Of(Box(Row(Now, tokenEnds: Now.Date.AddDays(-1))), [], null, null));
            Assert.Equal("The Steam sign-in token ended on 2026-11-18, so the bot can't log on to Steam with it again: sign in again, as the Steam Sign-In steps say.", ended.Text);
            Assert.Equal(Banners.SteamSignIn, ended.Link);
        }

        // The website's database answered the check, but a read after it failed: the banner names that read and the pages
        // that make it, while one that doesn't answer keeps its own banner, which stands for every read
        [Fact]
        public void WebsiteReadFailedBanners()
        {
            static string Words(WebsiteReads failed) => Assert.Single(Banners.Of(Box(Row(Now)), [], null, null, failed)).Text;

            Assert.Equal("The website's database answers the dashboard, but reading pending armor from it failed, so it can't be shown here, and the armor pages may fail the same way. If it goes on, the dashboard's login may have lost its rights there, which ./setup-dashboard.sh --no-questions applies again.",
                Words(WebsiteReads.Armor));
            Assert.StartsWith("The website's database answers the dashboard, but reading the servers' settings from it failed, so this page can't check them, and the servers pages may fail the same way. If it goes on,",
                Words(WebsiteReads.Settings));
            Assert.StartsWith("The website's database answers the dashboard, but reading pending armor and the servers' settings from it failed, so neither can be shown here, and the armor and servers pages may fail the same way. If it goes on,",
                Words(WebsiteReads.Armor | WebsiteReads.Settings));
            Assert.Equal(Banners.DeployNotes, Assert.Single(Banners.Of(Box(Row(Now)), [], null, null, WebsiteReads.Settings)).Link);
            Assert.Empty(Banners.Of(Box(Row(Now)), [], null, null, WebsiteReads.None));

            Assert.StartsWith("The website's database isn't answering the dashboard",
                Assert.Single(Banners.Of(Box(Row(Now), website: false), [], null, null, WebsiteReads.Armor)).Text);
        }

        // A job whose last run failed, was cut off, or was skipped because the cut-off one still runs, with its detail
        [Fact]
        public void JobBanners()
        {
            JobStatusRow Job(string job, string? result, string? detail = null) => new()
            {
                job = job,
                schedule = "0 * * * *",
                last_started_at = Now.AddMinutes(-3),
                last_result = result,
                last_detail = detail
            };

            var banners = Banners.Of(Box(Row(Now)),
            [
                Job("ArmorJob.AddColorArrays", JobResult.Ok),
                Job("GuildMembersJob.CountMembers", JobResult.Failed, "TimeoutException"),
                Job("HousekeepingJob.ApplyRetention", JobResult.CutOff, "still running when the time limit of 270 s cut it off"),
                Job("GuildNotificationJob.NotifyAllGuilds", JobResult.Skipped, "the run that was cut off still runs"),
                Job("SteamTokenJob.RenewSteamToken", null)
            ], null, null);

            Assert.Equal(
            [
                "The last run of Member counts failed at 2026-11-19 14:00:30 UTC: TimeoutException.",
                "The last run of Clean-up was cut off by the time limit at 2026-11-19 14:00:30 UTC: still running when the time limit of 270 s cut it off.",
                "The last run of Notifications was skipped at 2026-11-19 14:00:30 UTC: the run that was cut off still runs."
            ], banners.Select(banner => banner.Text));
            Assert.All(banners, banner => Assert.Equal(Banners.JobsLink, banner.Link));
        }

        // Armor that has waited 3 days by the website's clock, which is US Eastern, and servers MinerBot can't post in
        [Fact]
        public void ArmorAndServerBanners()
        {
            var threeDays = WebsiteClock.FromUtc(Now.AddDays(-3));
            Assert.NotNull(threeDays);

            var armor = Assert.Single(Banners.Of(Box(Row(Now)), [], new PendingArmor(7, threeDays), null));
            Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"7 armor are waiting for a review, the oldest since {threeDays.Value:yyyy-MM-dd HH:mm} by the website's clock, 3 days ago."), armor.Text);
            Assert.Equal(Banners.PendingLink, armor.Link);
            Assert.Empty(Banners.Of(Box(Row(Now)), [], new PendingArmor(7, threeDays.Value.AddMinutes(1)), null));

            var servers = Assert.Single(Banners.Of(Box(Row(Now)), [], null, 2));
            Assert.Equal("2 servers have a setting MinerBot can't post in.", servers.Text);
            Assert.Equal(Banners.ProblemsLink, servers.Link);
            Assert.Equal("1 server has a setting MinerBot can't post in.", Assert.Single(Banners.Of(Box(Row(Now)), [], null, 1)).Text);
        }

        // The website's clock is US Eastern: an armor saved at 09:00 there in November was saved at 14:00 UTC
        [Fact]
        public void WebsiteClockIsEastern()
        {
            Assert.Equal(new DateTime(2026, 11, 19, 14, 0, 0, DateTimeKind.Utc), WebsiteClock.ToUtc(new DateTime(2026, 11, 19, 9, 0, 0)));
            Assert.Equal(new DateTime(2026, 7, 1, 13, 0, 0, DateTimeKind.Utc), WebsiteClock.ToUtc(new DateTime(2026, 7, 1, 9, 0, 0)));

            // 02:30 on the day the clocks go forward isn't on the clock, and reads as the hour after
            Assert.Equal(new DateTime(2026, 3, 8, 7, 30, 0, DateTimeKind.Utc), WebsiteClock.ToUtc(new DateTime(2026, 3, 8, 2, 30, 0)));
        }
    }
}
