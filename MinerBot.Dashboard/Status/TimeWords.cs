using System.Globalization;

namespace MinerBot.Dashboard.Status
{
    // Times and lengths of time as the status pages write them: UTC, as the audit log shows them, and how long ago in the
    // largest whole unit. Each takes now as an argument, from the page's clock.
    internal static class TimeWords
    {
        public static string Utc(DateTime time) => time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

        public static string Day(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Such as 12 seconds ago. A time a little ahead of the page's clock, which another clock can give, is just now.
        public static string Ago(DateTime then, DateTime now) => now - then < TimeSpan.FromSeconds(1) ? "just now" : Span(now - then) + " ago";

        // Such as in 3 minutes, for a time still to come, or how long ago one that has passed was due
        public static string Until(DateTime then, DateTime now) => then - now < TimeSpan.FromSeconds(1) ? Ago(then, now) : "in " + Span(then - now);

        // Rounded down to the largest whole unit: 45 seconds, 1 minute, 12 minutes, 3 hours, 2 days
        public static string Span(TimeSpan span)
        {
            if (span < TimeSpan.Zero)
                span = TimeSpan.Zero;

            if (span < TimeSpan.FromMinutes(1))
                return Count((long)span.TotalSeconds, "second");

            if (span < TimeSpan.FromHours(1))
                return Count((long)span.TotalMinutes, "minute");

            if (span < TimeSpan.FromDays(1))
                return Count((long)span.TotalHours, "hour");

            return Count((long)span.TotalDays, "day");
        }

        // A job's run: 45 ms, 1.2 s, or 4 min 30 s
        public static string Duration(uint milliseconds)
        {
            if (milliseconds < 1000)
                return milliseconds.ToString(CultureInfo.InvariantCulture) + " ms";

            if (milliseconds < 60_000)
                return (Math.Floor(milliseconds / 100.0) / 10).ToString("0.0", CultureInfo.InvariantCulture) + " s";

            var seconds = milliseconds / 1000;
            return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60} min {seconds % 60} s");
        }

        // A number with its noun, such as 1 day or 3 days
        public static string Count(long count, string noun) =>
            count.ToString("N0", CultureInfo.InvariantCulture) + " " + (count == 1 ? noun : noun + "s");
    }
}
