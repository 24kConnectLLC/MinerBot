using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using MinerBot.Dashboard.Status;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Requests
{
    // One control on the controls page: the handler its form posts to, its label and what it does. The page's own map says
    // which kind each handler sends.
    public sealed record ControlText(string Handler, string Label, string Help);

    // The controls page's fixed words: what each control does, the sign-in it needs, and the bot's own conditions, which
    // the page shows as information and never enforces, since only the bot knows them at the moment it acts. Nothing here
    // repeats what a form held.
    internal static class ControlWords
    {
        public const string Title = "Bot controls";

        public const string Intro = "Each control asks MinerBot to do something. The dashboard leaves a signed request in MinerBot's database, the bot looks for "
            + "requests every 3 seconds and acts if its own checks pass, and the result shows here, usually within 5 seconds. A request waits at most 2 minutes "
            + "for the bot, or 10 for pausing and resuming posts, so nothing piles up while it's down. Each request is in the audit log.";

        public const string AdminLine = "As an Admin you can clear the bot's caches and run its notification job. Pausing posts, the status text, the Steam "
            + "controls, the colour job and restarting are the Owner's.";

        // The handlers, by the name Razor Pages gives each form's handler
        public const string ClearCaches = "ClearCaches";
        public const string RunNotifications = "RunNotifications";
        public const string RunColors = "RunColors";
        public const string Pause = "Pause";
        public const string Resume = "Resume";
        public const string SetStatus = "SetStatus";
        public const string DefaultStatus = "DefaultStatus";
        public const string ReconnectSteam = "ReconnectSteam";
        public const string RenewToken = "RenewToken";
        public const string Restart = "Restart";

        // How long what a pause holds may wait before the bot gives it up as too old to be news, by the bot's PostingLimits,
        // which ControlsPageTests reads so the two can't drift. The pause's help and the paused banner both name them: a pause
        // of more than half an hour already loses the lobbies found at its start.
        public const string PauseLimits = "armor is given up after 7 days, a map after 2 and a lobby after 30 minutes.";

        // Every control, in the page's order
        public static readonly IReadOnlyList<ControlText> Controls =
        [
            new(Pause, "Pause posts",
                "Holds the bot's armor, map and lobby posts. What it finds meanwhile waits, even across a restart, and goes out when posts are resumed, "
                + $"unless it waited too long to be news: {PauseLimits} /admin announce, the welcome message, test messages and direct messages aren't held."),
            new(Resume, "Resume posts",
                "Lets posts go again. The next notification run, within 5 minutes, posts what waited, unless it waited too long to be news."),
            new(SetStatus, "Set the status text", "Every server shows it under MinerBot's name."),
            new(DefaultStatus, "Back to the default status", "Discord shows Playing Murder Miners on my PC again."),
            new(RunNotifications, "Run the notification job now",
                "Runs the job that posts new armor, maps and lobbies, now instead of at its next five-minute run. The bot refuses while posts are paused or "
                + "while the job is running."),
            new(RunColors, "Run the colour job now",
                "Runs the job that adds colour codes to the armor that needs them, now instead of at its next run."),
            new(ClearCaches, "Clear the caches",
                "Empties the bot's cached list of approved armor names, which /armor list and the name suggestions read, and its cached list of Steam's latest "
                + "maps, which otherwise catch up within 5 minutes and 60 seconds."),
            new(ReconnectSteam, "Reconnect Steam", "Disconnects from Steam and logs on again, which can clear a stuck connection."),
            new(RenewToken, "Renew the Steam token",
                "Asks Steam for a new sign-in token now, as the bot does every day. Steam may keep the one it has."),
            new(Restart, "Restart the bot",
                "MinerBot stops and Docker starts it again. It's usually back on Discord within a minute, and this page says when. What it hasn't posted yet "
                + "waits and goes out at its first run after the start. A restart also renews the Steam token once.")
        ];

        public static ControlText Of(string handler) => Controls.Single(control => control.Handler == handler);

        // The boxes' hints, and the label of the box each typed word goes in
        public const string ReasonLabel = "Why";
        public const string ReasonHint = "On one line, at most 200 characters. Your direct message and this page show it.";
        public const string StatusTextLabel = "Status text";
        public const string StatusTextHint = "On one line, at most 128 characters, with no link, mention or hidden character.";

        public static string TypeToConfirm(string word) => $"Type {word} to confirm";

        // The sign-in from the last 15 minutes that restarting, pausing posts, the Steam controls, a status text and leaving
        // a server need, while it isn't, and while it is
        public static string NotFresh(DateTimeOffset? signedInAt) => signedInAt is { } at
            ? $"Restarting, pausing posts, the Steam controls, a status text and leaving a server need a sign-in from the last 15 minutes, and yours is from {RequestWords.Clock(at.UtcDateTime)}."
            : "Restarting, pausing posts, the Steam controls, a status text and leaving a server need a sign-in from the last 15 minutes, and the dashboard can't tell when yours was.";

        // By each control that needs one, in place of its form
        public static string NeedsFresh(DateTimeOffset? signedInAt) => signedInAt is { } at
            ? $"This needs a sign-in from the last 15 minutes, and yours is from {RequestWords.Clock(at.UtcDateTime)}."
            : "This needs a sign-in from the last 15 minutes, and the dashboard can't tell when yours was.";

        public const string SignInAgain = "Sign in again with Discord";
        public const string SignInAgainNote = "It brings you back here, and sends you the usual sign-in message on Discord.";

        public static string Fresh(DateTimeOffset signedInAt, DateTimeOffset until) =>
            $"Your sign-in is from {RequestWords.Clock(signedInAt.UtcDateTime)}, so these work until {RequestWords.Clock(until.UtcDateTime)}.";

        // What was typed into a form the page doesn't offer again until the sign-in is fresh, read-only, to copy
        public const string WhatYouTyped = "What you typed:";

        // The bot refuses a restart until this long after it starts, so it can't land in a deploy's readiness check. The
        // bot's own constant is RequestActions.MinimumUptime, which ControlsPageTests reads from its source.
        public static readonly TimeSpan RestartAfterStart = TimeSpan.FromMinutes(5);

        // A run the time limit cut off doesn't stop a restart, which is the cure for one: JobHandler.Runs leaves it out
        public static string RestartWhen(BotStatusRow? status) => status == null
            ? "MinerBot hasn't checked in, so the dashboard can't tell when its run started. The bot takes a restart from 5 minutes after it starts, and it "
              + "doesn't restart while a notification run is under way, unless the time limit cut that run off."
            : $"This run started at {TimeWords.Utc(status.started_at)}, so the bot takes a restart from {TimeWords.Utc(status.started_at + RestartAfterStart)}. "
              + "It doesn't restart while a notification run is under way, unless the time limit cut that run off.";

        // In place of "Available now." while the page's own reads show the bot refusing: posts paused, for the notification
        // job, and a run's first 5 minutes, for a restart
        public const string RefusedWhilePaused = "Posts are paused, so the bot refuses this until they're resumed.";

        public static string RestartFrom(DateTime from) => $"The bot takes a restart from {RequestWords.Clock(from)}.";

        // Steam's state and the day the sign-in token ends, as the bot last recorded them, never the token
        public static string SteamState(BotStatusRow? status, DateTime now)
        {
            if (status == null)
                return "MinerBot hasn't checked in, so Steam's state isn't known.";

            var since = status.steam_since is { } at ? $" since {TimeWords.Utc(at)}" : "";
            var token = status.steam_token_expires is { } end ? $" The sign-in token ends on {TimeWords.Day(end)}." : " No end date is recorded for the sign-in token.";
            var recorded = BotStates.Of(status, now) == BotState.Online ? "" : ", as MinerBot last recorded it";

            return $"Steam: {BotStates.SteamLabel(status.steam_state)}{since}{recorded}.{token}";
        }

        // A job's last run from job_status, in JobWords' words, by the job's name as the bot names its jobs. The bot records a
        // run when it ends or when the time limit cuts it off, so one with no end is a run that was cut off: it may still run
        // in the background, or, when it started before the bot's current run or before a clean stop the bot hasn't started
        // again since, it stopped with the bot.
        public static string LastRun(string job, JobStatusRow? row, BotStatusRow? status)
        {
            var name = JobWords.Name(job);

            if (row?.last_started_at is not { } started)
                return $"{name}: no run is recorded yet.";

            if (row.last_ended_at is not { } ended || ended < started)
            {
                if (JobWords.EndedWithTheBot(started, status))
                    return $"{name}: last run {JobWords.Result(row.last_result)}, started at {TimeWords.Utc(started)}, with no end recorded: it stopped with the bot, "
                        + (started < status!.started_at ? $"which started again at {TimeWords.Utc(status.started_at)}." : $"at {TimeWords.Utc(status.stopped_at!.Value)}.");

                return $"{name}: last run {JobWords.Result(row.last_result)}, started at {TimeWords.Utc(started)}, with no end recorded yet: it may still be running.";
            }

            var took = row.last_duration_ms is { } duration ? $", took {TimeWords.Duration(duration)}" : "";
            return $"{name}: last run {JobWords.Result(row.last_result)}, started at {TimeWords.Utc(started)}{took}.";
        }

        // Whether one more request of a kind would be sent now, in the order the sender checks: the person's limit, then
        // the bot's, then a request like it that waits. Otherwise what the page's own reads show the bot refusing, when it
        // gives one, rather than "Available now."; the button stays, since only the bot knows when it acts.
        public static AvailabilityLine Availability(PersonLimitCheck person, BotLimitCheck bot, string? botRefuses = null)
        {
            if (person.Reached)
                return new(person.After is { } again ? $"You can ask for this again after {RequestWords.Clock(again)}." : "You can ask for this again later.");

            if (bot.State == BotLimitState.Limit)
                return new(bot.After is { } after ? $"The bot takes this again after {RequestWords.Clock(after)}." : "The bot takes this again later.");

            if (bot is { State: BotLimitState.Waiting, Waiting: { } waiting })
                return new("A request like this is waiting:", waiting.id);

            return new(botRefuses ?? "Available now.");
        }
    }

    // A control's availability, with the request that waits when that's why: "A request like this is waiting: request N."
    public sealed record AvailabilityLine(string Words, ulong? Waiting = null);
}
