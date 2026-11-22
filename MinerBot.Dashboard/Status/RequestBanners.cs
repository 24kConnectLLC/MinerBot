using System.Globalization;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Settings;
using MinerBot.Shared.Requests;
using MinerBot.Shared.Text;

namespace MinerBot.Dashboard.Status
{
    // What the request banners are drawn from, read once for a page: whether the dashboard can sign requests, the bot's
    // settings with the name of whoever paused posts, the bot's status row, the latest of each of the bot's two request
    // events, its last direct message to the owner, the latest sign-in message it refused, whatever sign-ins came after,
    // and, when the latest refusal was of a request the dashboard signed, the latest before it of a row it didn't
    public sealed record BannerFacts(bool RequestsOn, BotSettingsRow? Settings, string? PausedBy, BotStatusRow? Status, BotEventRow? RequestsOff,
        BotEventRow? Rejected, RequestPageRow? LatestDm, RequestPageRow? RefusedAlert, BotEventRow? UnsignedRejected = null);

    // The banners about requests to the bot, after the Overview's own, and at the top of the controls page: posts paused,
    // requests off at either end, requests the bot refused as not the dashboard's or as not fitting its version's rules, a
    // stored status text the rules refuse, and, for the Owner alone, a direct message to them that failed and a sign-in
    // message the bot refused. Names and reasons go into the text with each hidden character as its code, and the page
    // encodes the rest. A refusal for a limit is that request's own result, never a banner.
    internal static class RequestBanners
    {
        public const string ControlsLink = "/admin/bot/controls";
        public const string SessionsLink = "/admin/sessions";

        // How long a request the bot's checks refused, or a sign-in message it refused, stays a banner
        public static readonly TimeSpan RefusalsFor = TimeSpan.FromDays(1);

        public static IReadOnlyList<Banner> Of(BannerFacts facts, DashboardRole role, DateTime now)
        {
            ArgumentNullException.ThrowIfNull(facts);

            var banners = new List<Banner>();
            var owner = role == DashboardRole.Owner;

            // 1. Posts paused, which the Owner alone can resume
            if (facts.Settings is { posts_paused: true } settings)
            {
                var since = settings.pause_changed_at is { } at ? $", since {TimeWords.Utc(at)}, {TimeWords.Ago(at, now)}" : "";
                var by = (facts.PausedBy, settings.pause_request_id) switch
                {
                    ({ } name, { } id) => $", by {TextInspector.Reveal(name)} (request {Number(id)})",
                    (null, { } id) => $", on request {Number(id)}",
                    _ => ""
                };
                var because = settings.pause_reason is { } reason ? $", because: {TextInspector.Reveal(reason)}" : "";
                var text = $"MinerBot's armor, map and lobby posts are paused{since}{by}{because}. What the bot finds waits and goes out when posts are resumed, unless "
                    + "the bot restarts first, which loses it.";

                banners.Add(owner
                    ? new Banner(text, "Resume them on the controls page", ControlsLink + "#posts")
                    : new Banner(text + " The Owner can resume them.", "The controls page", ControlsLink + "#posts"));
            }

            // 2. The dashboard can't sign requests
            if (!facts.RequestsOn)
                banners.Add(new Banner(DashboardOff, "The deploy notes", Banners.DeployNotes));

            // 3. The bot can't check them
            if (BotOff(facts.Status, facts.RequestsOff) is { } botOff)
                banners.Add(new Banner(botOff, "The deploy notes", Banners.DeployNotes));

            // 4. A request the bot's checks refused, in the last day: one the dashboard didn't sign, or one it signed that
            // doesn't fit the bot's rules
            if (Rejected(facts.Rejected, now, facts.UnsignedRejected) is { } rejected)
                banners.Add(new Banner(rejected, "Those refusals on the Bot page", "/admin/bot?kind=" + EventWords.RequestRejected));

            // 5. A stored status text the rules refuse, which Discord doesn't show: the rule, never the text. Only the Owner sets
            // one, so an Admin's says so, as banner 1 does.
            if (SettingsWords.StatusLine(facts.Settings).Rule is { } rule)
            {
                var text = $"Discord shows MinerBot's default status, {SettingsWords.DefaultStatus}, because the stored status text breaks the rules ({rule}).";

                banners.Add(new Banner(owner ? text + " Set another, or go back to the default, on the controls page." : text + " The Owner can set another or go back to the default.",
                    "The controls page", ControlsLink + "#status-text"));
            }

            if (!owner)
                return banners;

            // 6. The owner's direct messages aren't reaching them
            if (facts.LatestDm is { dm_state: RequestDmStates.Failed } dm)
                banners.Add(new Banner($"The bot's last direct message to you failed, at {RequestWords.When(dm.ended_at)} (request {Number(dm.id)}), so its messages about "
                    + "sign-ins and Owner-level requests aren't reaching you. Discord refuses them while you don't allow direct messages from members of a server you "
                    + "share with MinerBot.", "That request", "/admin/requests/" + Number(dm.id)));

            // 7. A sign-in's message the bot refused, in the last day, as past its limit of 20 an hour, though later sign-ins'
            // messages went. One whose time can't be read is shown, since it may be recent.
            if (facts.RefusedAlert is { state: RequestStates.Refused } alert)
            {
                var when = alert.ended_at ?? alert.created_at;

                if (when == null || now - when.Value <= RefusalsFor)
                    banners.Add(new Banner($"The bot refused a sign-in message at {RequestWords.When(when)}: {Sentence(alert.result ?? "no reason is recorded")} If those "
                        + "sign-ins weren't all yours and your admins', check the Sessions page.", "Sessions", SessionsLink));
            }

            return banners;
        }

        // Banners 2 to 4's words, which the Sessions page's check of requests to the bot says too
        public const string DashboardOff = "Requests to the bot are off: the dashboard has no request-signing key, so its controls, Announce now, test messages, "
            + "refreshes, custom-link lookups and sign-in messages don't work. On the server, ./setup-dashboard.sh --no-questions makes one.";

        // The bot says it can't check requests once a run, so only an event from this run counts. The events carry no run, so
        // it's one at or after this run's start.
        public static string? BotOff(BotStatusRow? status, BotEventRow? off) => status != null && off != null && off.at >= status.started_at
            ? $"The bot takes no requests: when this run started, at {RequestWords.Clock(status.started_at)}, it found that "
              + $"{off.detail ?? "its request-signing key wasn't usable"}. On the server, ./setup-dashboard.sh --no-questions puts the key in bot.env and "
              + "recreates the bot, then ./status.sh --check says whether both have it."
            : null;

        // The bot records request.rejected for a row in another form and one whose signature didn't match, the rows the
        // dashboard didn't sign, and for the checks it makes once the signature passed: a kind it doesn't know, a role, an
        // expiry, a time or details that don't fit. Those rows the dashboard signed, so the two apps run different versions,
        // as after a deploy or a rollback of one of them. The bot's words for the first two, which its event's detail holds,
        // as RequestBannerTests reads them from its source.
        public static readonly IReadOnlyList<string> UnsignedReasons = ["the form the dashboard writes", "signature didn't match"];

        // Banner 4's words for the latest refusal: the versions' when the dashboard signed it, the forger's when it didn't. An
        // earlier refusal in the day of a row it didn't sign, which BannerReads reads when the latest was signed, still comes
        // first, and the versions' words follow: a deploy of one app, whose refusals are of signed requests, mustn't hide a
        // row written without the key.
        public static string? Rejected(BotEventRow? rejected, DateTime now, BotEventRow? unsigned = null)
        {
            if (rejected == null || now - rejected.at > RefusalsFor)
                return null;

            if (!Signed(rejected))
                return NotSigned(rejected, now);

            if (unsigned != null && now - unsigned.at <= RefusalsFor && !Signed(unsigned))
                return NotSigned(unsigned, now) + $" It also refused a request this dashboard signed, at {When(rejected, now)}, which happens while the dashboard and the "
                    + "bot run different versions: ./status.sh on the server shows the commit each runs.";

            return $"The bot refused a request this dashboard signed at {When(rejected, now)}, as its kind, role, times or details don't fit the bot's rules. That happens "
                + "while the dashboard and the bot run different versions, as after a deploy or a rollback of one of them: ./status.sh on the server shows the commit each runs.";
        }

        // Whether the dashboard signed a request the bot refused, by the bot's words for why. A detail it can't read is taken as
        // a row it didn't sign.
        public static bool Signed(BotEventRow rejected) =>
            rejected.detail is { } detail && !UnsignedReasons.Any(reason => detail.Contains(reason, StringComparison.Ordinal));

        // When the latest refusal was of a request the dashboard signed, the latest of a row it didn't, from a day before that
        // refusal on, which holds every one still in banner 4's day, read with the (kind, at) index; otherwise none, unread
        public static Task<BotEventRow?> UnsignedBefore(IBotStatusReads status, BotEventRow? rejected) => rejected != null && Signed(rejected)
            ? status.LatestEventWith(EventWords.RequestRejected, UnsignedReasons, rejected.at - RefusalsFor)
            : Task.FromResult<BotEventRow?>(null);

        static string When(BotEventRow rejected, DateTime now) => $"{RequestWords.Clock(rejected.at)}, {TimeWords.Ago(rejected.at, now)}";

        static string NotSigned(BotEventRow rejected, DateTime now) =>
            $"The bot refused a request as not made by this dashboard at {When(rejected, now)}: something other than the dashboard wrote it, or the bot's request-signing "
            + "key isn't the dashboard's. ./status.sh --check on the server says which.";

        static string Number(ulong number) => number.ToString(CultureInfo.InvariantCulture);

        // The bot's own words as a sentence, which they usually are already
        static string Sentence(string words) => words.EndsWith('.') || words.EndsWith('!') || words.EndsWith('?') ? words : words + ".";
    }

    // The reads the request banners need, each a primary-key or indexed read on MinerBot's own database: the bot's
    // settings, the request that paused posts for its asker's name, the bot's two request events, and when its latest
    // refusal was of a request the dashboard signed, the latest of a row it didn't, its last direct message and the latest
    // sign-in message it refused. The status row is the page's, already read for its status box.
    internal sealed class BannerReads(IBotRequestQueue queue, IBotRequestReads requests, IBotStatusReads status, IOptions<DashboardOptions> settings,
        ILogger<BannerReads> logger)
    {
        public async Task<BannerFacts> LoadAsync(BotStatusRow? bot)
        {
            var stored = await DatabaseCalls.Guard(() => queue.GetSettings(), logger);
            var pausedBy = stored is { posts_paused: true, pause_request_id: { } paused }
                ? (await DatabaseCalls.Guard(() => requests.GetRequestView(paused), logger))?.actor_name
                : null;

            var off = await DatabaseCalls.Guard(() => status.LatestEvent(EventWords.RequestsOff), logger);
            var rejected = await DatabaseCalls.Guard(() => status.LatestEvent(EventWords.RequestRejected), logger);
            var unsigned = await DatabaseCalls.Guard(() => RequestBanners.UnsignedBefore(status, rejected), logger);
            var dm = await DatabaseCalls.Guard(() => requests.LatestDm(), logger);
            var refusedAlert = await DatabaseCalls.Guard(() => requests.LatestRefusedAlert(), logger);

            return new BannerFacts(RequestSigning.ParseKey(settings.Value.RequestSigningKey) != null, stored, pausedBy, bot, off, rejected, dm, refusedAlert, unsigned);
        }
    }
}
