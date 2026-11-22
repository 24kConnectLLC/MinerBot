using System.Globalization;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Blacklist;
using MinerBot.Dashboard.Status;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Requests
{
    // Where a request stands, as the result box shows it: waiting for the bot, taken, how the bot ended it, or, by the
    // page's own clock, past the time the bot had for it
    public enum RequestStage
    {
        Queued,
        Running,
        Done,
        Failed,
        Refused,
        Expired,
        Unknown,
        NoAnswer,
        Unreadable
    }

    // One request as its result box, a control's last-use line and its own page show it: what was asked, by whom and when,
    // where it stands in fixed words, the bot's own words for how it ended, and what follows from that. Final once nothing
    // on it can change, when the box stops looking; until then, Next is the seconds before its next look.
    public sealed record RequestView(ulong Id, string Kind, string Label, ulong ActorId, string AskedBy, string Role, DateTime? AskedAt, RequestStage Stage,
        string StageLabel, string Marker, string Says, string? Result, string? Then, string? Dm, string? Link, string? LinkText, bool Final, int Next)
    {
        public string IdText => Id.ToString(CultureInfo.InvariantCulture);
        public string ActorIdText => ActorId.ToString(CultureInfo.InvariantCulture);

        // The box as the request's own page draws it (RequestViews.OnItsOwnPage), whose fragment leaves out what only the
        // page that sent it needs
        public bool OwnPage { get; init; }

        // Its own page, and the box alone, which refresh.js gets again, as the page it's drawn on needs it
        public string Address => "/admin/requests/" + IdText;
        public string FragmentAddress => Address + (OwnPage ? "?handler=result&own=1" : "?handler=result");

        // As the box gives it, and as a line or a list gives it
        public string AskedAtClock => RequestWords.When(AskedAt);
        public string AskedAtUtc => AskedAt is { } at ? TimeWords.Utc(at) : RequestWords.UnreadableTime;
    }

    // A control's last use, for _RequestLine: never asked for when there's none
    public sealed record RequestLine(RequestView? Last);

    // The box for a request a page's form just sent, at #result, which the redirect after the form leads to, with the address
    // of the page itself for Check again, which is the next look without the script. None on a page drawn again after its
    // form, such as the blacklist's add page, where pressing the form's button again is the next look.
    public sealed record RequestSection(RequestView View, string? CheckAgain, string Heading = "Your request");

    // The result box's rules, in one place so every state is tested without a database. It waits as request.sh does, with
    // the same RequestWaits: a request that's still queued past its expiry might have been taken just in time, for 20
    // seconds, and one the bot took has its kind's action limit and 30 seconds; a restart has 6 minutes to be back on
    // Discord, since the bot may first wait for a notification run. The bot's result is its own words, never the request's
    // text.
    internal static class RequestViews
    {
        // The fragment's name, which refresh.js checks on each answer
        public const string Fragment = "request";

        public static readonly RequestWaits Waits = new();

        // How long after the bot ended a request the box waits to hear whether its direct message to the owner went: the bot
        // gives Discord up to 30 seconds
        public static readonly TimeSpan DmWait = TimeSpan.FromSeconds(45);

        // A request this young is looked at every 2 seconds, then every 5
        public static readonly TimeSpan Young = TimeSpan.FromSeconds(20);
        public const int SoonSeconds = 2;
        public const int LaterSeconds = 5;

        // For a kind this version doesn't know: the longest any kind has
        static readonly TimeSpan LongestAction = RequestKinds.All.Max(kind => kind.ActionLimit);

        // What a done pause, resume, status text, leave or refresh adds on the page that sent it, since the page under the box
        // shows the state before it. A leave is done once Discord answers the bot, and MinerBot's copy marks the server as
        // left when Discord's event about it comes, at the bot's next write of its servers, every 5 seconds.
        public const string ReloadForSettings = "Reload the page to see the new settings.";
        public const string ReloadForLeave = "MinerBot's copy marks the server as left a few seconds later: reload the page then to see it.";
        public const string ReloadForCopy = "Reload the page to see the new copy.";

        public static RequestView Of(RequestPageRow row, BotStatusRow? status, DateTime now, RequestWaits waits)
        {
            ArgumentNullException.ThrowIfNull(row);
            ArgumentNullException.ThrowIfNull(waits);

            var kind = RequestKinds.Find(row.kind);
            var next = row.created_at is { } created && now - created < Young ? SoonSeconds : LaterSeconds;
            var view = new RequestView(row.id, row.kind, kind?.Label ?? row.kind, row.actor_id, row.actor_name, AuditWords.RoleLabel(row.actor_role), row.created_at,
                RequestStage.Unknown, "", "", "", row.result, null, null, null, null, true, next);

            return row.state switch
            {
                // Waiting, or taken, and so timed by the page: the row's own times have to be readable
                null when row.created_at == null || row.expires_at == null => Unreadable(view),
                RequestStates.Running when row.claimed_at == null => Unreadable(view),
                null => Waiting(view, row, kind, status, now, waits),
                RequestStates.Running => Running(view, row, kind, now, waits),

                // Ended by the bot, in its own words
                RequestStates.Done => Ended(view with { Stage = RequestStage.Done, StageLabel = "Done", Marker = "marker-ok", Says = Answer("Done at " + When(row.ended_at), row.result) },
                    row, kind, status, now, waits),
                RequestStates.Failed => Ended(view with { Stage = RequestStage.Failed, StageLabel = "Failed", Marker = "marker-block", Says = Answer("Failed at " + When(row.ended_at), row.result) },
                    row, kind, status, now, waits),
                RequestStates.Unknown => Ended(view with { Stage = RequestStage.Unknown, StageLabel = "Not sure it happened", Marker = "marker-warn",
                    Says = Answer("Not sure it happened, at " + When(row.ended_at), row.result) }, row, kind, status, now, waits),
                RequestStates.Refused => view with { Stage = RequestStage.Refused, StageLabel = "Refused", Marker = "marker-warn", Says = Answer("Refused at " + When(row.ended_at), row.result) },
                RequestStates.Expired => view with { Stage = RequestStage.Expired, StageLabel = "Expired", Marker = "marker-warn", Says = Answer("Expired", row.result) },

                // A state a later version writes, as it's stored, encoded by the page
                _ => view with { StageLabel = row.state, Marker = "marker-warn", Says = Answer(row.state, row.result) }
            };
        }

        // The box as the request's own page draws it, and as its fragment gives it there: without a line that says to reload
        // the page to see a change, since that page shows no settings, server or copy, and reloading it, as the audit log's
        // link does any time later, only shows the line again. A restart's line, which follows the bot, stays.
        public static RequestView OnItsOwnPage(RequestView view) => view with
        {
            Then = view.Then is ReloadForSettings or ReloadForLeave or ReloadForCopy ? null : view.Then,
            OwnPage = true
        };

        static RequestView Unreadable(RequestView view) => view with
        {
            Stage = RequestStage.Unreadable,
            StageLabel = "Can't follow",
            Marker = "marker-warn",
            Says = "This request holds a time MinerBot's database can't hand back, so the dashboard can't follow it. The bot refuses a request it can't read."
        };

        // Queued until 20 seconds past its expiry, since the bot may have taken it just in time; then expired by the page's
        // own clock, which is the bot's too: both apps run on the same host
        static RequestView Waiting(RequestView view, RequestPageRow row, RequestKind? kind, BotStatusRow? status, DateTime now, RequestWaits waits)
        {
            var created = row.created_at!.Value;
            var expires = row.expires_at!.Value;

            if (now > expires + waits.QueuedGrace)
            {
                return view with
                {
                    Stage = RequestStage.Expired,
                    StageLabel = "Expired",
                    Marker = "marker-warn",
                    Says = $"Expired at {Clock(expires)} without the bot taking it, so nothing happened. " + (kind?.Word == RequestKinds.BotRestart
                        ? "The bot takes a restart even while it's off Discord, as long as it runs: the Bot page says whether it was running."
                        : "The bot takes requests only while it runs and is up on Discord: the Bot page says what it was doing.")
                };
            }

            var onDiscord = BotStates.Of(status, now) == BotState.Online && status!.discord_state == DiscordState.Connected;

            var says = onDiscord ? $"Queued at {Clock(created)}. The bot looks for requests every 3 seconds."
                : kind?.Word == RequestKinds.BotRestart ? $"Queued at {Clock(created)}. Restarts are taken even while the bot is off Discord, as long as it runs."
                : $"Queued at {Clock(created)}. MinerBot isn't on Discord now, so it waits, and it expires at {Clock(expires)} if the bot isn't back by then.";

            return view with { Stage = RequestStage.Queued, StageLabel = "Queued", Marker = "marker-quiet", Says = says, Final = false };
        }

        // Running until its kind's action limit and 30 seconds have passed since the bot took it. Past that, the bot's next
        // start marks it as may or may not have happened, if it stopped while it ran.
        static RequestView Running(RequestView view, RequestPageRow row, RequestKind? kind, DateTime now, RequestWaits waits)
        {
            var claimed = row.claimed_at!.Value;

            if (now > claimed + (kind?.ActionLimit ?? LongestAction) + waits.RunningGrace)
            {
                return view with
                {
                    Stage = RequestStage.NoAnswer,
                    StageLabel = "No answer",
                    Marker = "marker-warn",
                    Says = $"No answer: the bot took it at {Clock(claimed)} and it's past its time. If the bot stopped while it ran, its next start marks it as "
                        + "may or may not have happened. Reload to see."
                };
            }

            return view with { Stage = RequestStage.Running, StageLabel = "Running", Marker = "marker-quiet", Says = $"Taken at {Clock(claimed)}; the bot is acting on it.", Final = false };
        }

        // Done, failed or not sure, with what follows: a restart that's done waits to be back on Discord, then to hear whether
        // its direct message went, which may come from the next run; an Owner control or a sign-in alert waits to hear whether
        // the owner's direct message went. Only the box refreshes, so the page under it still shows what was there before the
        // bot acted: a done change to what the page shows says to reload it, on the page that sent it (OnItsOwnPage).
        static RequestView Ended(RequestView view, RequestPageRow row, RequestKind? kind, BotStatusRow? status, DateTime now, RequestWaits waits)
        {
            var done = row.state == RequestStates.Done;
            (string Words, bool Final)? dm = kind != null && (kind.OwnerOnly || kind.Word == RequestKinds.OwnerSigninAlert) ? DirectMessage(row, now) : null;
            view = view with { Dm = dm?.Words, Final = dm?.Final ?? true };

            if (done && kind?.Word == RequestKinds.BotRestart)
                return Back(view, row, status, now, waits);

            if (done && kind?.Word == RequestKinds.GuildRefresh)
                view = view with { Then = ReloadForCopy };

            // The pause, the status text and a server MinerBot has left, which the controls page and the server's page draw
            // from what the bot recorded, so the form under the box is still the one for the state before
            if (done && kind?.Word is RequestKinds.PostsPause or RequestKinds.PostsResume or RequestKinds.StatusSet)
                view = view with { Then = ReloadForSettings };

            if (done && kind?.Word == RequestKinds.GuildLeave)
                view = view with { Then = ReloadForLeave };

            if (done && row.result_number is { } number)
            {
                // A message in a server's channel, and an account by its number, built from the parsed numbers, never text
                if (kind?.Word == RequestKinds.GuildTestMessage && row.guild_id is { } guild && row.channel_id is { } channel)
                    view = view with { Link = $"https://discord.com/channels/{Number(guild)}/{Number(channel)}/{Number(number)}", LinkText = "Open the test message in Discord" };

                if (kind?.Word == RequestKinds.SteamLookup && number is >= 1 and <= uint.MaxValue)
                    view = view with { Link = SteamAccounts.EntryLink((uint)number), LinkText = "Its blacklist page, " + SteamAccounts.Steam3((uint)number) };
            }

            return view;
        }

        // Back once the bot's status row is a new run's, up on Discord and not stopping, as request.sh tells it, with the
        // time from when that run reached Discord, and final once the direct message is heard of. The old run sends it before
        // it stops, but one taken off Discord, or whose message didn't end in time, is told by the new run once it's up, which
        // can be a few seconds after its status row says so: the wait to hear counts from then. Not back 6 minutes after the
        // bot ended the request: final, with where to look.
        // The status row is the bot as it is now, while the request's own page and ?request= draw the box any time later, as
        // the audit log's link does. So past the 6 minutes the row tells how the restart went only while the new run's state
        // on Discord dates from within them: up since then, the box gives that connection, the restart's own unless a
        // reconnect or a later run within them took its place; in one state off Discord since then, or still the old run, it
        // isn't back, stopped since or not. A change of state or of run since, or the stop of a run that was up, says nothing
        // of it, so the box says where to look instead.
        static RequestView Back(RequestView view, RequestPageRow row, BotStatusRow? status, DateTime now, RequestWaits waits)
        {
            var ended = row.ended_at ?? row.claimed_at ?? row.created_at;
            var limit = ended + waits.RestartLimit;

            // A new run, stopped since or not, whose state on Discord began by the end of the 6 minutes, which it always has
            // while they last
            var within = status != null && status.run_id != row.run_id
                && (limit == null || (status.discord_since ?? status.started_at) <= limit);

            if (within && status is { stopped_at: null, discord_state: DiscordState.Connected })
            {
                var back = status.discord_since ?? status.started_at;
                var after = row.created_at is { } created ? Math.Max(0, (long)(back - created).TotalSeconds) : 0;
                var dm = DirectMessage(row, now, row.ended_at is { } endedAt && endedAt > back ? endedAt : back);

                return view with
                {
                    Then = string.Create(CultureInfo.InvariantCulture, $"Back on Discord at {Clock(back)}, {after} s after the request: its new run started at {Clock(status.started_at)}."),
                    Dm = dm.Words,
                    Final = dm.Final,
                    Next = LaterSeconds
                };
            }

            // Past them, not back while the row is still the old run's, or there's none, or the new run has been in one state
            // off Discord since within them, stopped since or not. Any other row has changed since them, or is a new run's
            // that was up and has stopped.
            if (limit == null || now > limit)
            {
                return view with
                {
                    Then = status == null || status.run_id == row.run_id || (within && status.discord_state != DiscordState.Connected)
                        ? string.Create(CultureInfo.InvariantCulture,
                            $"The bot isn't back on Discord {waits.RestartLimit.TotalMinutes:0} minutes after it restarted. ./status.sh on the server says whether it runs.")
                        : "The bot's status has changed since this restart, so it no longer shows whether and when the bot was back on Discord: the Bot page's events do.",
                    Final = true
                };
            }

            return view with { Then = "Waiting for the bot to come back on Discord.", Final = false, Next = LaterSeconds };
        }

        // Whether the bot's direct message to the owner about it went, once the bot has had time to say, counted from when it
        // ended the request, or from the later time given
        static (string Words, bool Final) DirectMessage(RequestPageRow row, DateTime now, DateTime? from = null) => row.dm_state switch
        {
            RequestDmStates.Sent => ("The owner was sent a direct message about it.", true),
            RequestDmStates.Failed => ("The direct message to the owner failed. Discord refuses MinerBot's messages while the owner doesn't allow direct messages "
                + "from members of a server they share with MinerBot; the Bot page's events say how it failed.", true),
            _ when (from ?? row.ended_at) is { } ended && now <= ended + DmWait => ("Waiting to hear whether the direct message to the owner went.", false),
            _ => ("No direct message to the owner is recorded.", true)
        };

        static string Answer(string start, string? result) => result == null ? start + "." : start + ": " + result;

        static string Clock(DateTime time) => RequestWords.Clock(time);

        static string When(DateTime? time) => RequestWords.When(time);

        static string Number(ulong number) => number.ToString(CultureInfo.InvariantCulture);
    }
}
