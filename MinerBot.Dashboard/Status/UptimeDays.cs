using System.Globalization;
using DataAccessLibrary.Status.Models;

namespace MinerBot.Dashboard.Status
{
    // One UTC day of the strip: the minutes it had from the record's start, or its midnight, to its end, or to the current
    // minute for today, and in how many of them the bot sampled Discord and Steam up. A day before the record began has no
    // minutes at all, which the pages show as no record, never as down.
    public sealed record UptimeDay(DateTime Day, int Minutes, int DiscordMinutes, int SteamMinutes)
    {
        public bool HasRecord => Minutes > 0;
    }

    // The same for a stretch of time, such as the last 7 days
    public sealed record UptimePeriod(string Label, int Minutes, int DiscordMinutes, int SteamMinutes)
    {
        public bool HasRecord => Minutes > 0;
    }

    // Uptime from the bot's minutes. A minute the bot ran has a sample, and a minute without one is a minute it wasn't
    // running, so it counts as down for Discord and Steam both, from the first minute ever recorded on. The clean-up
    // totals each day at 04:30 Eastern the next day, so a day comes from uptime_daily once it's totalled and from the
    // samples before that, today always among them.
    internal static class UptimeDays
    {
        public const int StripDays = 90;

        // The current minute is still being sampled, so a period ends at its start
        public static DateTime Minute(DateTime now) => new(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

        // Where the record begins: the first minute sampled, unless a day was totalled before it, whose samples the
        // clean-up has since deleted, so recording began on or before that day. Null while there's no record at all.
        public static DateTime? RecordStart(UptimeStartRow start)
        {
            if (start.first_day is { } day && (start.first_minute is not { } first || day < first.Date))
                return day;

            return start.first_minute;
        }

        // The first day to count from the samples: the day after the last one totalled, or the strip's first
        public static DateTime SampleDaysFrom(DateTime now, IReadOnlyList<UptimeDayRow> totalled)
        {
            var first = StripStart(now);

            if (totalled.Count == 0)
                return first;

            var next = totalled.Max(row => row.day).AddDays(1);
            return next > first ? next : first;
        }

        public static DateTime StripStart(DateTime now) => Minute(now).Date.AddDays(1 - StripDays);

        // The strip's 90 days, oldest first, today last
        public static IReadOnlyList<UptimeDay> Days(DateTime now, DateTime? recordStart, IReadOnlyList<UptimeDayRow> totalled, IReadOnlyList<UptimeDayRow> sampled)
        {
            var minute = Minute(now);
            var totals = totalled.ToDictionary(row => row.day);
            var samples = sampled.ToDictionary(row => row.day);
            var days = new List<UptimeDay>(StripDays);

            for (var day = StripStart(now); day <= minute.Date; day = day.AddDays(1))
            {
                var end = day == minute.Date ? minute : day.AddDays(1);
                var start = recordStart is { } begins && begins > day ? begins : day;
                var minutes = recordStart == null || end <= start ? 0 : (int)(end - start).TotalMinutes;

                // A day after the record began without a sample counts as down all day
                var row = totals.GetValueOrDefault(day) ?? samples.GetValueOrDefault(day);
                days.Add(new UptimeDay(day, minutes, Math.Min(row?.discord_minutes ?? 0, minutes), Math.Min(row?.steam_minutes ?? 0, minutes)));
            }

            return days;
        }

        // The last given days of the strip together, such as 7, 30 or 90
        public static UptimePeriod Period(string label, IReadOnlyList<UptimeDay> days, int count)
        {
            var last = days.Skip(Math.Max(0, days.Count - count)).ToList();
            return new UptimePeriod(label, last.Sum(day => day.Minutes), last.Sum(day => day.DiscordMinutes), last.Sum(day => day.SteamMinutes));
        }

        // The last 24 hours up to the current minute, from the samples, counted from the record's start when that's later
        public static UptimePeriod LastDay(DateTime now, DateTime? recordStart, UptimeTotalRow samples)
        {
            var minute = Minute(now);
            var start = minute.AddDays(-1);

            if (recordStart is { } begins && begins > start)
                start = begins;

            var minutes = recordStart == null || minute <= start ? 0 : (int)(minute - start).TotalMinutes;
            return new UptimePeriod("Last 24 hours", minutes, Math.Min(samples.discord_minutes, minutes), Math.Min(samples.steam_minutes, minutes));
        }

        // A percentage to two places, cut rather than rounded, so 100.00% means not one minute down
        public static string Percent(int up, int minutes)
        {
            if (minutes <= 0)
                return "no record";

            var hundredths = Math.Floor(up * 10000.0 / minutes) / 100;
            return hundredths.ToString("0.00", CultureInfo.InvariantCulture) + "%";
        }

        // A day's band on the strip, by its Discord uptime: at least 99.9%, at least 99%, at least 95%, below that, or no record
        public static string Band(UptimeDay day)
        {
            if (!day.HasRecord)
                return "none";

            // In whole numbers, so a day exactly on a line is on its upper side
            long up = day.DiscordMinutes, minutes = day.Minutes;

            return up * 1000 >= minutes * 999 ? "top" : up * 100 >= minutes * 99 ? "good" : up * 100 >= minutes * 95 ? "fair" : "poor";
        }

        // The band in words, for the strip's key and its table
        public static string BandLabel(string band) => band switch
        {
            "top" => "99.9% or more",
            "good" => "99% or more",
            "fair" => "95% or more",
            "poor" => "below 95%",
            _ => "no record"
        };

        // The numbers a day's cell shows when pointed at
        public static string Tooltip(UptimeDay day) => day.HasRecord
            ? string.Create(CultureInfo.InvariantCulture,
                $"{TimeWords.Day(day.Day)}: Discord {Percent(day.DiscordMinutes, day.Minutes)} ({day.DiscordMinutes:N0} of {day.Minutes:N0} minutes), Steam {Percent(day.SteamMinutes, day.Minutes)} ({day.SteamMinutes:N0} of {day.Minutes:N0} minutes)")
            : $"{TimeWords.Day(day.Day)}: no record";
    }
}
