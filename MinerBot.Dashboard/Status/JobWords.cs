using System.Globalization;
using DataAccessLibrary.Status;

namespace MinerBot.Dashboard.Status
{
    // The bot's scheduled jobs in words: what each does, how its run went, and its schedule as the bot reads its cron, in
    // US Eastern time
    internal static class JobWords
    {
        // The job whose last run the Overview shows
        public const string Notifications = "GuildNotificationJob.NotifyAllGuilds";

        // A job the list doesn't have, such as a later bot's, is shown by its name, <Class>.<Method>
        public static string Name(string job) => job switch
        {
            "ArmorJob.AddColorArrays" => "Armor save strings",
            "GuildMembersJob.CountMembers" => "Member counts",
            Notifications => "Notifications",
            "HousekeepingJob.ApplyRetention" => "Clean-up",
            "SteamTokenJob.RenewSteamToken" => "Steam token renewal",
            _ => job
        };

        public static string Result(string? result) => result switch
        {
            null => "No run yet",
            JobResult.Ok => "OK",
            JobResult.Failed => "Failed",
            JobResult.CutOff => "Cut off",
            JobResult.Skipped => "Skipped",
            _ => result
        };

        // A run that needs a look: one that failed, one the time limit cut off, and one skipped because the run that was cut
        // off still runs, which is the only reason the bot skips
        public static bool IsProblem(string? result) => result is JobResult.Failed or JobResult.CutOff or JobResult.Skipped;

        // The forms the bot's jobs use, in words: every minute, every N minutes, every hour at a minute, and every day at a
        // time. Null for any other form, which the page shows as the cron alone.
        public static string? Schedule(string cron)
        {
            var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (fields is not [var minute, var hour, "*", "*", "*"])
                return null;

            if (hour == "*")
            {
                if (minute == "*")
                    return "every minute";

                if (minute.StartsWith("*/", StringComparison.Ordinal) && Number(minute[2..], 1, 59) is { } every)
                    return every == 1 ? "every minute" : $"every {every} minutes";

                if (Number(minute, 0, 59) is { } past)
                    return past == 0 ? "every hour, on the hour" : string.Create(CultureInfo.InvariantCulture, $"every hour at {past} minutes past");

                return null;
            }

            if (Number(hour, 0, 23) is { } h && Number(minute, 0, 59) is { } m)
                return string.Create(CultureInfo.InvariantCulture, $"every day at {h:00}:{m:00} Eastern");

            return null;
        }

        // Digits only, in the range
        static int? Number(string text, int min, int max) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= min && number <= max ? number : null;
    }
}
