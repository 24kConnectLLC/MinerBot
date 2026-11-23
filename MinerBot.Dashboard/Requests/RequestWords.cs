using System.Globalization;
using DataAccessLibrary.Requests.Models;
using MinerBot.Dashboard.Hosting;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Requests
{
    // What a page says when the dashboard didn't send a request, or can't tell whether it did, in fixed words that never
    // repeat what the form held: the rules' problems never quote the text, and a name or a reason is only ever shown back
    // in its own box. Also the status each answer goes with, and the bot's limits in words.
    internal static class RequestWords
    {
        public const string Forbidden = "You can't open that page.";

        // The error page's own words, for a form someone made up
        public const string MadeUp = "That form had expired or was incomplete.";

        public const string Off = "Requests to the bot are off: the dashboard has no request-signing key, so nothing was sent. On the server, "
            + "./setup-dashboard.sh --no-questions makes one.";

        public const string NotConfirmedServer = "Type the server's name as it shows above, or its ID, to confirm. Nothing was sent.";

        // An empty box in the status text's form, which is never sent as no text: that's Back to the default's own form
        public const string NoStatusText = "Type the status text, or use Back to the default.";

        public const string KeyTaken = "This form's key belongs to another request, so nothing was sent. The form below is new: send it again.";

        // Above a page whose address names a form's notice that's gone: past its 30 minutes, from before a sign-in again, or
        // replaced by a newer one
        public const string NoticeGone = "The message this address showed after a form isn't kept any more, so this is the page as it is now.";

        public const string Unsure = "The dashboard lost touch with its database while it was saving this request, so it can't tell whether it was sent. "
            + "Sending it again from this form is safe: if the first one was saved, the dashboard shows it instead of sending another.";

        // The same for a form that isn't drawn again, since the sign-in passed its 15 minutes while the save was awaited. The
        // control's last use says it, once signed in again: "Last asked for by ...", with a link to the request, or, for a
        // pause the bot took, whose control isn't drawn then, the line under Posts.
        public const string UnsureNotFresh = "The dashboard lost touch with its database while it was saving this request, so it can't tell whether it was sent. "
            + "Once you've signed in again, this control's last use, or for a pause the line under Posts, shows whether it was: look there before you send it again.";

        // The same for a pause or a resume whose form isn't drawn again, since posts already are as it would leave them: the
        // line under Posts names the request that last changed them.
        public const string UnsurePostsLine = "The dashboard lost touch with its database while it was saving this request, so it can't tell whether it was sent. "
            + "The line under Posts shows posts as they are now and the request that last changed them: look there before you send another.";

        // The same for a form whose page doesn't draw it again for another reason, as once MinerBot has left the server, a
        // notification run has started since the approval, the dashboard has started again with requests off, or the lists
        // no longer give the person a role that can ask for it: the audit log lists the request, if it was saved.
        public const string UnsureNotOffered = "The dashboard lost touch with its database while it was saving this request, so it can't tell whether it was sent. "
            + "The form isn't offered now, and the audit log shows whether it was: look there before you send another.";

        // The details of the refusals the audit log records: for an Admin's try at an Owner control, "Only the Owner can ask
        // for this"
        public static string ForbiddenDetail(RequestKind kind) => $"Only {kind.WhoMayAsk} can ask for this";
        public const string NotFreshDetail = "The sign-in was older than 15 minutes";
        public const string PersonLimitDetail = "Over the dashboard's limit for one person";

        public static string NotFresh(DateTimeOffset? signedInAt, bool typedText) =>
            (signedInAt is { } at
                ? $"This needs a sign-in from the last 15 minutes, and yours is from {Clock(at.UtcDateTime)}, so nothing was sent."
                : "This needs a sign-in from the last 15 minutes, and the dashboard can't tell when yours was, so nothing was sent.")
            + " Sign in again with Discord, then send it again." + (typedText ? " What you typed is below, to copy." : "");

        public static string NotConfirmed(string word) => $"Type {word} in the box to confirm. Nothing was sent.";

        // The same, and NotConfirmedServer, for a form whose page doesn't draw it again, so there's no box to type in: once
        // the bot has taken the pause sent after it, or MinerBot has left the server, or the sign-in has passed its 15 minutes
        public const string NotConfirmedNotOffered = "What was typed to confirm it didn't match, so nothing was sent.";

        public static string Rule(string problem) => $"The bot would refuse this, so nothing was sent: {problem}.";

        // Over a person's share of one kind, or over their hourly total
        public static string PersonLimit(PersonLimitCheck check) => check.ForKind
            ? $"You've asked for this {Count(check.Count)} times in the last hour, the most the dashboard sends for one person, so nothing was sent. "
              + (check.After is { } after ? $"You can ask again after {Clock(after)}." : "You can ask again later.")
            : $"You've sent {Count(check.Count)} requests to the bot in the last hour, the most the dashboard sends for one person, so nothing was sent. "
              + (check.After is { } next ? $"You can send another after {Clock(next)}." : "You can send another later.");

        // The bot's own words for its limit (MinerBot 2.0/Requests/RequestTexts.cs, OverLimit), with what the dashboard did
        public static string BotLimit(RequestKind kind, BotLimitCheck check)
        {
            var server = kind.PerServer ? ", for each server" : "";
            var oldest = When(check.Oldest);
            var after = check.After is { } at ? $"Try again after {Clock(at)}." : "Try again later.";

            return kind.Limit == 1
                ? $"The bot takes this at most once {Window(kind)}{server}, and last took one at {oldest}, so nothing was sent. {after}"
                : $"The bot takes this at most {Count(kind.Limit)} times {Window(kind)}{(kind.PerServer ? server + "," : "")} and has taken {Count(check.Count)} since {oldest}, so nothing was sent. {after}";
        }

        // A request of the same kind, of the same server for a kind counted by server, that the bot will count once it
        // takes it
        public static string Waiting(WaitingRow waiting) =>
            $"A request like this is already waiting for the bot: request {waiting.id.ToString(CultureInfo.InvariantCulture)}, asked for at {When(waiting.created_at)}. "
            + $"The bot takes it within seconds while it's on Discord, or it expires at {When(waiting.expires_at)}. Nothing more was sent.";

        public static string SentBefore(ulong id) => SentBeforeNotOffered(id) + " The form below is new: send it again to ask with these.";

        // The same for a form whose page doesn't draw it again, as a pause's once the bot has taken the first, or a test
        // message's once MinerBot has left the server: the request named, the page's own lines and banners say the rest
        public static string SentBeforeNotOffered(ulong id) =>
            $"This form was sent already, as request {id.ToString(CultureInfo.InvariantCulture)}, with other details, so nothing more was sent.";

        // The status of the page that draws a form's notice: 409 for what's refused as things stand, 400 for a field to
        // correct, 403 for a sign-in that's too old, 429 for a person's limit, and 503 when it can't tell what happened. Sent
        // is the form's redirect, Forbidden its Forbid() and MadeUp its BadRequest(), never a notice.
        public static int Status(SendOutcome outcome) => outcome switch
        {
            SendOutcome.Unsure => StatusCodes.Status503ServiceUnavailable,
            SendOutcome.SentBefore => StatusCodes.Status409Conflict,
            SendOutcome.Refused { Why: SendRefusal.Forbidden } => StatusCodes.Status403Forbidden,
            SendOutcome.Refused { Why: SendRefusal.NotFresh } => StatusCodes.Status403Forbidden,
            SendOutcome.Refused { Why: SendRefusal.MadeUp or SendRefusal.NotConfirmed or SendRefusal.Rule } => StatusCodes.Status400BadRequest,
            SendOutcome.Refused { Why: SendRefusal.PersonLimit } => StatusCodes.Status429TooManyRequests,
            SendOutcome.Refused => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status302Found
        };

        // Whole seconds until a person may ask again, at least 1, for the 429's Retry-After: the hour itself when the rows
        // hold no time that can be read
        public static int RetryAfter(DateTime? after, DateTime now) =>
            after is { } at ? Math.Max(1, (int)Math.Ceiling((at - now).TotalSeconds)) : (int)PersonLimits.Window.TotalSeconds;

        // The answer of the GET that draws a form's notice: the notice's status, and for a person's limit Retry-After,
        // worked out now, when the page is drawn
        public static void Answer(HttpResponse response, FormNotice notice, DateTime now)
        {
            response.StatusCode = notice.Status;

            if (notice.Status == StatusCodes.Status429TooManyRequests)
                response.Headers.RetryAfter = RetryAfter(notice.After, now).ToString(CultureInfo.InvariantCulture);
        }

        // As in "an hour", "a minute" or "every 10 minutes", the bot's own words for a kind's window
        public static string Window(RequestKind kind) =>
            kind.Window == TimeSpan.FromHours(1) ? "an hour"
            : kind.Window == TimeSpan.FromMinutes(1) ? "a minute"
            : string.Create(CultureInfo.InvariantCulture, $"every {kind.Window.TotalMinutes:0} minutes");

        // As in "At most 10 times an hour, with posts.resume", for request.sh's list and the pages
        public static string Limit(RequestKind kind)
        {
            var times = kind.Limit == 1 ? "once" : kind.Limit.ToString(CultureInfo.InvariantCulture) + " times";
            var others = kind.CountedWith.Where(word => word != kind.Word).ToList();

            return $"At most {times} {Window(kind)}{(kind.PerServer ? ", for each server" : "")}{(others.Count > 0 ? ", with " + string.Join(" and ", others) : "")}";
        }

        // HH:mm:ss UTC, as the bot's own words give a time
        public static string Clock(DateTime time) => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

        // A time a read gave back, or the words for one MinerBot's database couldn't
        public static string When(DateTime? time) => time is { } at ? Clock(at) : UnreadableTime;

        public const string UnreadableTime = "an unreadable time";

        // The bot's nightly clean-up deletes a request 90 days on (HousekeepingJob's KeepEvents), while its audit row stays
        // 2 years: a control's last use with no request left, and the page of one that's gone. Nothing else deletes one, so
        // a newer one that isn't there never landed: a send whose commit the database didn't confirm and that rolled back,
        // whose audit row, written on its own, reads Not sure it was sent and links to that page all the same.
        public const string NotAskedFor = "Not asked for in the last 90 days.";

        public static string Gone(ulong id) => $"MinerBot's database has no bot request {id.ToString(CultureInfo.InvariantCulture)}. It keeps requests for 90 "
            + "days, so an older one is gone, while its row in the audit log, kept for 2 years, still says what was asked. A newer one whose save the "
            + "database didn't confirm, as its row there says, never landed, so the bot never had it.";

        static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);
    }
}
