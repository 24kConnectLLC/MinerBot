using DataAccessLibrary.Requests.Models;
using MinerBot.Shared.Requests;
using System.Globalization;
using System.Text;

namespace MinerBot_2._0.Requests
{
    // Why a row isn't one the dashboard signed for this bot, for its request.rejected event: the first of each reason in a
    // minute is recorded at once, and the rest are counted into one event at the minute's end
    public enum Rejection
    {
        Form,
        Signature,
        Kind,
        Role,
        TooLong,
        Future,
        Details
    }

    // Every text the request worker writes, in one place, in the bot's own words: the results it writes back to each row,
    // which the dashboard shows, the details of its events, which Admins read, and the owner's messages. A result or an
    // event never holds the request's own text, a name, or a library's message. The only text from outside the bot goes in
    // the owner's messages, in a code span, so Markdown and masked links in it show as they are. Times are UTC.
    internal static class RequestTexts
    {
        // The checks, in their order

        // None, or one in another form: requests.off and the bot's log say which, and status.sh's line covers both
        public const string NoKey = "The bot has no usable request-signing key, so it takes no requests. Run ./setup-dashboard.sh --no-questions on the server.";

        public const string NotInForm = "This request isn't in the form the dashboard writes, so the bot didn't act on it.";

        public const string BadSignature = "This request's signature didn't match the bot's key, so the bot didn't act on it: the row was written or changed without "
            + "the key, or the two apps hold different keys. ./status.sh --check on the server says which.";

        public const string AlreadyUsed = "This request was already used, so the bot didn't act on it again.";

        public const string UnknownKind = "The bot doesn't know this kind of request, so it didn't act on it.";

        public static string RoleRefused(RequestKind kind) => $"Only {kind.WhoMayAsk} can ask for this, so the bot didn't act on it.";

        public static string TooLong(RequestKind kind) => Text($"This request was made to last longer than this kind may ({kind.Expiry.TotalMinutes:0} minutes), so the bot didn't act on it.");

        public const string FromTheFuture = "This request was made later than the bot's clock says it is now, so the bot didn't act on it.";

        public static string Expired(DateTime expiresAt) => Text($"The bot saw this request only after it expired at {Time(expiresAt)} UTC, so it didn't act on it.");

        public static string DetailsDontFit(string rule) => $"This request's details don't fit its kind: {rule}. The bot didn't act on it.";

        // The limit, counted from the table, with when the oldest request it counted leaves the window
        public static string OverLimit(RequestKind kind, long count, DateTime oldest)
        {
            var window = kind.Window == TimeSpan.FromHours(1) ? "an hour"
                : kind.Window == TimeSpan.FromMinutes(1) ? "a minute"
                : Text($"every {kind.Window.TotalMinutes:0} minutes");
            var after = Time(oldest + kind.Window);

            return kind.Limit == 1
                ? Text($"The bot takes this at most once {window}{(kind.PerServer ? ", for each server" : "")}, and last took one at {Time(oldest)} UTC. Try again after {after} UTC.")
                : Text($"The bot takes this at most {kind.Limit} times {window}{(kind.PerServer ? ", for each server," : "")} and has taken {count} since {Time(oldest)} UTC. Try again after {after} UTC.");
        }

        public const string JobsDidntStart = "The bot's scheduled jobs didn't start, so no job can run now. The bot's log says why, and a restart tries again.";

        // A row that can't be read, or whose handling threw. The type only: a message can quote the row.
        public static string Unreadable(string type) => $"The bot couldn't read this request ({type}), so it didn't act on it.";

        public static string Unconfirmed(RequestKind? kind, DateTime? claimedAt) =>
            "MinerBot's database didn't confirm that the bot took this request, so the bot didn't act on it. " + AskAgain(kind, claimedAt);

        // A claim that landed counts toward its kind's limit, though the bot never acted on it, so a kind taken at most once in
        // its window says when another can be taken: the time the limit gives
        static string AskAgain(RequestKind? kind, DateTime? claimedAt) =>
            kind is { Limit: 1 } && claimedAt is { } at ? Text($"Ask again after {Time(at + kind.Window)} UTC.") : "Ask again.";

        public const string Interrupted = "The bot stopped while this ran, so it may or may not have happened.";

        // caches.clear

        public const string CachesCleared = "Cleared the armor-name and latest-maps caches.";

        // job.notifications and job.colors

        public static string JobName(RequestKind kind) => kind.Word == RequestKinds.JobColors ? "colour job" : "notification job";

        public static string JobRunning(string job) => $"The {job} is running now, so it wasn't started again.";

        public const string PausedNoRun = "Posts are paused, so the notification job didn't run. Resume posts first.";

        public const string PauseUnknownNoRun = "The bot hasn't read yet whether posts are paused, so the notification job didn't run. Try again in a minute.";

        public static string JobRan(string job, TimeSpan took, string? detail) =>
            detail == null ? Text($"The {job} ran in {Seconds(took)} s.") : Text($"The {job} ran in {Seconds(took)} s: {detail}.");

        public static string JobStillRunning(RequestKind kind) =>
            Text($"The {JobName(kind)} is still running after {kind.ActionLimit.TotalSeconds:0} seconds. The Bot page shows how its run ends.");

        public static string JobFailed(string job, TimeSpan took, string? detail) => Text($"The {job} failed after {Seconds(took)} s: {detail}.");

        // steam.renew

        public const string NothingToRenew = "Steam isn't logged on, so there's no token to renew. The Bot page says why.";

        public static string TokenRenewed(DateOnly? ends) => ends == null ? "Steam renewed the token." : Text($"Steam renewed the token, which now ends on {Date(ends.Value)}.");

        public static string TokenKept(DateOnly? ends) => ends == null ? "Steam kept the token." : Text($"Steam kept the token, which ends on {Date(ends.Value)}.");

        public static string TokenNotRenewed(string? type, DateOnly? ends) =>
            Text($"Steam didn't renew the token ({type}).{(ends == null ? "" : $" It still ends on {Date(ends.Value)}.")}");

        public const string TokenNotSaved = "MinerBot couldn't save its Steam token first, so it didn't ask Steam. The owner was sent why.";

        public static string RenewalUnanswered(RequestKind kind) => Text($"Steam didn't answer within {kind.ActionLimit.TotalSeconds:0} seconds, so the token may or may not have been renewed.");

        // posts.pause and posts.resume

        public static string AlreadyPaused(DateTime? since) =>
            since is { } at ? Text($"Posts are already paused, since {Time(at)} UTC on {at:yyyy-MM-dd}.") : "Posts are already paused.";

        public const string NotPaused = "Posts aren't paused.";

        public const string Paused = "Posts are paused. New armor, maps and lobbies wait until they're resumed, and then go out.";

        public const string Resumed = "Posts are resumed. The next notification run, within 5 minutes, posts what waited.";

        public static string PauseNotSaved(bool pausing) => $"MinerBot's database didn't take the change, so posts weren't {(pausing ? "paused" : "resumed")}.";

        public const string ChangeUnconfirmed = "MinerBot's database didn't confirm the change within 15 seconds. The bot reads it back and follows what it holds.";

        public const string ChangeAnswerLost = "MinerBot's database didn't confirm the change: the call failed before its answer came. The bot reads it back and follows what it holds.";

        // status.set

        public const string StatusSet = "The status text is set. Discord shows it while the bot is connected.";

        public const string StatusReset = "The status is back to Playing " + BotPresence.DefaultGame + ".";

        public const string StatusNotSaved = "MinerBot's database didn't take the change, so the status text wasn't changed.";

        // steam.reconnect

        public const string NothingToReconnect = "Steam isn't logged on, so there's nothing to reconnect: MinerBot is already trying to.";

        public const string SteamSigningIn = "A Steam sign-in is under way, so MinerBot doesn't reconnect now.";

        public static string SteamBack(TimeSpan after) => Text($"Steam disconnected and logged on again after {Seconds(after)} s.");

        public static string SteamNotBack(RequestKind kind) =>
            Text($"Steam disconnected, and MinerBot hasn't logged on again within {kind.ActionLimit.TotalSeconds:0} seconds. It keeps trying, and the Bot page shows when it does.");

        // A server and a channel, from the cache

        public const string NotInServer = "The bot isn't in that server.";

        public const string ServerUnavailable = "Discord reports that server unavailable, so the bot can't use it now.";

        public const string ServerNotSent = "Discord hasn't sent that server to the bot yet, so the bot can't use it now.";

        public const string NotATextChannel = "That channel isn't a text or announcement channel of that server.";

        public static string MissingPermissions(IEnumerable<string> names) => $"MinerBot can't post in that channel: it's missing {string.Join(", ", names)}.";

        // guild.test_message: the embed is fixed, so nothing from the request reaches the channel

        public const string TestTitle = "MinerBot test message";

        public const string TestDescription = "MinerBot can post in this channel. This test was sent from MinerBot's dashboard.";

        public const string TestMessageSent = "Sent a test message.";

        public static string TestMessageRefused(int code) => Text($"Discord refused the test message (error {code}).");

        public static string TestMessageUnanswered(RequestKind kind) =>
            Text($"Discord didn't answer within {kind.ActionLimit.TotalSeconds:0} seconds, so the test message may or may not have been sent.");

        // guild.refresh

        public static string Rewritten(DateTime at, int channels) => Text($"Rewrote the copy of this server and its channels at {Time(at)} UTC: {(channels == 1
            ? "1 text or announcement channel"
            : $"{channels} text and announcement channels")}.");

        public static string CopyNotTaken(string? problem) => $"MinerBot's database didn't take the copy ({problem}).";

        public const string LeftBeforeRewrite = "The bot left that server before its copy was rewritten.";

        public static string CopyNotRewritten(RequestKind kind) =>
            Text($"The copy wasn't rewritten within {kind.ActionLimit.TotalSeconds:0} seconds. The servers page shows when it last was.");

        // steam.lookup

        public static string LookedUp(uint account) => Text($"That link is account [U:1:{account}].");

        public const string NoSuchProfile = "Steam has no profile with that custom link.";

        public const string NotAPlayer = "Steam's answer for that link isn't an individual player's account.";

        // The type only: the message can hold the request, the Web API key included
        public static string LookupFailed(string type) => $"Steam didn't answer the lookup ({type}).";

        // guild.leave

        public const string Left = "Left the server. Discord then tells the bot, which deletes the server's channel settings, as when a server removes the bot.";

        public static string LeaveRefused(int code) => Text($"Discord refused to let the bot leave (error {code}).");

        public static string LeaveUnanswered(RequestKind kind) =>
            Text($"Discord didn't answer within {kind.ActionLimit.TotalSeconds:0} seconds, so the bot may or may not have left. The servers page shows which.");

        // The server's history, with the leave
        public static string LeftOnRequest(ulong id) => Text($"left on dashboard request {id}");

        // bot.restart

        public static string RestartTooSoon(TimeSpan up, DateTime after) =>
            Text($"The bot started {Seconds(up)} seconds ago, and restarts only from 5 minutes after a start, so a restart can't upset a deploy's checks. Try again after {Time(after)} UTC.");

        public const string RestartDuringNotifications = "The notification job is running, so MinerBot doesn't restart now. Try again in a minute.";

        public const string Restarting = "The bot is restarting. It's usually back within a minute.";

        public static string RestartNotSaved(RequestKind kind, DateTime claimedAt) => "The bot couldn't save the restart first, so it didn't restart. " + AskAgain(kind, claimedAt);

        // owner.signin_alert, which is a direct message itself

        public const string SigninSent = "The owner was sent a direct message about this sign-in.";

        public static string SigninFailed(string type) => $"The direct message to the owner failed ({type}).";

        public static string SigninUnanswered(RequestKind kind) =>
            Text($"The direct message to the owner didn't go within {kind.ActionLimit.TotalSeconds:0} seconds. It may still arrive.");

        // The owner's messages: who asked, by the name the dashboard had for them, in a code span, and their Discord ID. What
        // the bot's history keeps instead holds no name, no ID and no text, because Admins read it.

        public static string OwnerMessage(RequestKind kind, BotRequestRow row, string result, string? extra) =>
            Text($"{kind.Label}, asked for on the MinerBot dashboard by `{Name(row.actor_name)}` (Discord ID {row.actor_id}), request {row.id}: {result}{extra}");

        public static string OwnerRecord(RequestKind kind, ulong id, string state) => Text($"request {id}: {kind.Label}, {state}");

        public static string ReasonExtra(string? reason) => Text($" The reason given: `{CodeSpan(reason, RequestRules.ReasonLimit, "no reason")}`.");

        public static string StatusExtra(string? text) => text == null ? "" : Text($" The status text: `{CodeSpan(text, RequestRules.StatusTextLimit, "no text")}`.");

        public static string ServerExtra(string? name, ulong guildId) => Text($" The server: `{CodeSpan(name, ServerNameLimit, "no name")}` (ID {guildId}).");

        public static string ToldAfterRestart(BotRequestRow row) =>
            Text($"MinerBot restarted, as `{Name(row.actor_name)}` (Discord ID {row.actor_id}) asked on the dashboard (request {row.id}), and is back. It couldn't tell you before it stopped.");

        public static string ToldAfterRestartRecord(ulong id) => Text($"request {id}: Restart the bot, told after the restart");

        // The time is the request's own, the sign-in's, and there's no address: the request takes no text at all
        public static string SigninMessage(BotRequestRow row) =>
            Text($"Dashboard sign-in: `{Name(row.actor_name)}` (Discord ID {row.actor_id}) signed in as {(row.actor_role == RequestRoles.Owner ? "Owner" : "Admin")} at {row.created_at:HH:mm} UTC on {row.created_at:yyyy-MM-dd} (request {row.id}). If that wasn't them, take them off the lists with ./setup-dashboard.sh, which ends their sessions at their next click, and check the dashboard's audit log.");

        public static string SigninRecord(ulong id) => Text($"request {id}: a dashboard sign-in");

        // A row the dashboard didn't sign for this bot. One in another form, or one that can't be read, was never checked
        // against the key, and a dashboard on another key still writes its form, so only a signature that doesn't match has
        // two causes.
        public static string RejectionMessage(ulong id, Rejection reason) => reason == Rejection.Form
            ? Text($"MinerBot refused dashboard request {id}: it isn't in the form the dashboard writes, so something other than the dashboard wrote it. Further refusals within the hour aren't sent; the Bot page lists every one.")
            : Text($"MinerBot refused dashboard request {id}: its signature didn't match the bot's key. Either something other than the dashboard wrote it, or the request key changed and the bot wasn't recreated. ./status.sh --check on the server says which. Further refusals within the hour aren't sent; the Bot page lists every one.");

        public static string RejectionRecord(ulong id, Rejection reason) =>
            Text($"request {id}: refused, {(reason == Rejection.Form ? "not in the dashboard's form" : "its signature didn't match")}");

        // Events, with request numbers, and a kind only once the signature passed

        public static string RejectedEvent(ulong id, string? kind, Rejection reason) =>
            Text($"request {id}{(kind == null ? "" : $" ({kind})")} refused: {One(reason)}");

        public static string RejectedEvents(ulong first, ulong last, int count, Rejection reason) =>
            Text($"requests {first} to {last} refused: {Many(reason)} ({count} in one minute)");

        public static string RequestsOff(string problem) => $"the request-signing key {problem}, so the bot acts on no request from the dashboard";

        public static string InterruptedEvent(ulong id, string kind) => Text($"request {id} ({kind}) was running when the bot stopped, so it may or may not have happened");

        public static string ByRequest(ulong id) => Text($"by request {id}");

        public static string StillPaused(DateTime? since, ulong? id) =>
            "still paused" + (since is { } at ? Text($" since {at:yyyy-MM-dd HH:mm:ss} UTC") : "") + (id is { } request ? Text($", by request {request}") : "");

        static string One(Rejection reason) => reason switch
        {
            Rejection.Form => "it isn't in the form the dashboard writes",
            Rejection.Signature => "its signature didn't match",
            Rejection.Kind => "its kind isn't on the bot's list",
            Rejection.Role => "its role may not ask for its kind",
            Rejection.TooLong => "it was made to last longer than its kind may",
            Rejection.Future => "it was made later than the bot's clock says it is now",
            _ => "its details don't fit its kind"
        };

        static string Many(Rejection reason) => reason switch
        {
            Rejection.Form => "they aren't in the form the dashboard writes",
            Rejection.Signature => "their signature didn't match",
            Rejection.Kind => "their kind isn't on the bot's list",
            Rejection.Role => "their role may not ask for their kind",
            Rejection.TooLong => "they were made to last longer than their kind may",
            Rejection.Future => "they were made later than the bot's clock says it is now",
            _ => "their details don't fit their kind"
        };

        // The longest name Discord gives a server
        const int ServerNameLimit = 100;

        // The name the dashboard had for who asked, shortened for a message
        const int NameLimit = 32;

        static string Name(string? name) => CodeSpan(name, NameLimit, "no name");

        // Text from outside the bot, for inside a code span: no backticks, control or format characters, or line breaks, so
        // it can't end the span, break the line, or turn around, with runs of white space made one, and cut to the limit's
        // code points with "…". What's left of nothing is the empty text given.
        internal static string CodeSpan(string? text, int limit, string empty)
        {
            var kept = new List<Rune>();
            var space = false;

            foreach (var rune in (text ?? "").EnumerateRunes())
            {
                var category = Rune.GetUnicodeCategory(rune);

                if (rune.Value == '`' || Rune.IsControl(rune) || category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                    continue;

                if (Rune.IsWhiteSpace(rune))
                {
                    space = kept.Count > 0;
                    continue;
                }

                if (space)
                    kept.Add(new Rune(' '));

                space = false;
                kept.Add(rune);
            }

            if (kept.Count == 0)
                return empty;

            if (kept.Count > limit)
                kept = [.. kept.Take(limit - 1), new Rune('…')];

            return string.Concat(kept.Select(rune => rune.ToString()));
        }

        static string Time(DateTime time) => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        static string Seconds(TimeSpan time) => Math.Max(0, time.TotalSeconds).ToString("0", CultureInfo.InvariantCulture);

        static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }
}
