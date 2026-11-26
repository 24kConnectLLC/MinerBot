using System.Globalization;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;

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
            "SubmissionAlertJob.AlertOwner" => "New-submission alerts",
            "PostingRetentionJob.ApplyRetention" => "Post records clean-up",
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

        // Whether a run that started at the time given, with no end recorded, as the bot records a run the time limit cut off,
        // ended with the bot: one that started before the bot's current run, which a restart ended, or before a clean stop
        // the bot hasn't started again since. Otherwise it may still run in the background.
        public static bool EndedWithTheBot(DateTime started, BotStatusRow? status) => status != null && (started < status.started_at || StoppedSince(started, status));

        // Whether the bot stopped cleanly once a run had started: a clean stop sets stopped_at, which the row keeps until the
        // next run's first write, and no job runs while the bot is stopped
        public static bool StoppedSince(DateTime started, BotStatusRow status) => status.stopped_at is { } stopped && started <= stopped;

        // The forms the bot's jobs use, in words: every minute, every N minutes, every N minutes from a minute past the hour,
        // every hour at a minute, and every day at a time. Null for any other form, which the page shows as the cron alone.
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
                    return Every(every);

                // N-59/M, as the new-submission alerts' 2-59/5: every M minutes, from N minutes past the hour
                if (minute.Split('/') is [var range, var step] && range.Split('-') is [var first, "59"]
                    && Number(first, 0, 59) is { } from && Number(step, 1, 59) is { } each)
                    return from == 0 ? Every(each) : string.Create(CultureInfo.InvariantCulture, $"{Every(each)}, from {from} {(from == 1 ? "minute" : "minutes")} past the hour");

                if (Number(minute, 0, 59) is { } past)
                    return past == 0 ? "every hour, on the hour" : string.Create(CultureInfo.InvariantCulture, $"every hour at {past} minutes past");

                return null;
            }

            if (Number(hour, 0, 23) is { } h && Number(minute, 0, 59) is { } m)
                return string.Create(CultureInfo.InvariantCulture, $"every day at {h:00}:{m:00} Eastern");

            return null;
        }

        static string Every(int minutes) => minutes == 1 ? "every minute" : string.Create(CultureInfo.InvariantCulture, $"every {minutes} minutes");

        // Digits only, in the range
        static int? Number(string text, int min, int max) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= min && number <= max ? number : null;
    }
}
