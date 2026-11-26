using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Status;
using MinerBot.Shared.Armor;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Armor
{
    // How the cache clear after a review went: the request it wrote, or the word the redirect carries for why there's none
    internal sealed record CacheClear(ulong? Request, string? Unsent);

    // Announce now on the review page: offered, or what stands in its place while posts are paused, requests are off, a
    // notification run that started before the approval is still under way, or the bot has stopped cleanly, or, for an older
    // bot that keeps what it finds only in memory, started again since its last run, so that its next run is its first since
    // a start, which posts none of the approved armor it finds
    public enum AnnounceState
    {
        Offered,
        Paused,
        Off,
        Running,
        Restarted,
        Stopped
    }

    // Kept: the bot that runs keeps what it finds in MinerBot's database (PostingRules), which the words in Announce now's
    // place then say
    public sealed record AnnounceView(AnnounceState State, DateTime? RunningSince = null, DateTime? StartedAt = null, bool Kept = false);

    // What follows an armor review on the bot's side. An approval, a take-down and a move back from Approved change what
    // /armor list and the name suggestions show, which the bot's name cache would hold for up to 5 minutes, so each asks the
    // bot to clear its caches, after the review's transaction committed: that transaction holds the armor table's locks and
    // may run again after a deadlock. The clear can't fail the review, which stands whatever became of it. And approved armor
    // the bot hasn't posted yet can be announced now: with a bot that keeps what it finds, until its record shows it found
    // the armor; with an older one, until a notification run has started since the approval, unless the bot has stopped
    // cleanly or started again since its last run.
    internal static class ArmorFollowUps
    {
        public static bool ClearsCaches(ArmorAction action, string? expected) =>
            action is ArmorAction.Approve or ArmorAction.TakeDown || (action == ArmorAction.BackToPending && expected == ArmorStatus.Approved);

        // As the reviewer, Owner or Admin, who may both ask for it, with a key of its own. Nobody pressed anything for it, so
        // past the person's share it's skipped without a refusal in the audit log.
        public static async Task<CacheClear> ClearCachesAsync(HttpContext context, ILogger logger)
        {
            SendOutcome outcome;

            try
            {
                outcome = await context.RequestServices.GetRequiredService<RequestSender>()
                    .SendAsync(new SendOrder(RequestKinds.CachesClear, RequestFields.NewKey(), Automatic: true), context);
            }
            catch (DashboardUnavailableException)
            {
                // Guard has logged it, and nothing was written
                return new CacheClear(null, ArmorWords.UnsentUnavailable);
            }

            switch (outcome)
            {
                case SendOutcome.Sent sent:
                    return new CacheClear(sent.Id, null);

                case SendOutcome.Unsure:
                    return new CacheClear(null, ArmorWords.UnsentUnsure);

                case SendOutcome.Refused { Why: SendRefusal.Off }:
                    return new CacheClear(null, ArmorWords.UnsentOff);

                case SendOutcome.Refused { Why: SendRefusal.PersonLimit or SendRefusal.BotLimit or SendRefusal.Waiting }:
                    return new CacheClear(null, ArmorWords.UnsentLimit);
            }

            // Nothing else can refuse a clear: both roles may ask, it takes no text and needs no word, and its key is new
            logger.LogWarning("The cache clear after an armor review was refused unexpectedly ({Outcome}), so the page says nothing about the cache", outcome.GetType().Name);
            return new CacheClear(null, null);
        }

        // The approval Announce now follows: armor that's approved, whose latest review approved it, and that the bot may
        // post, which for a bot that keeps what it finds means its record has no row of the armor yet. Null for any other, for
        // which it has no place, so the page reads nothing more for it.
        public static ArmorReview? Approval(string? status, IReadOnlyList<ArmorReview> history, bool mayNotPost) =>
            status == ArmorStatus.Approved && history.FirstOrDefault() is { to_status: ArmorStatus.Approved } approval && !mayNotPost ? approval : null;

        // Offered, or what stands in its place, or, for an older bot, null once a notification run has started since the
        // approval, which has seen the armor. The bot's status row tells a run the time limit cut off that may still run from
        // one that ended with the bot: one from before its current run, or from before a clean stop it hasn't started again
        // since. It also tells a clean stop, or a start since the last run, after either of which an older bot's next run is
        // its first since a start, which only notes the approved armor it finds and posts none of it, since that bot keeps
        // what it has seen only in memory, so that neither that run nor Announce now's would post this armor. An end without a
        // clean stop it tells only once the bot has started again, so until then Announce now is still offered.
        public static AnnounceView? Announce(ArmorReview approval, bool requestsOn, BotSettingsRow? settings, JobStatusRow? notifications, BotStatusRow? bot)
        {
            ArgumentNullException.ThrowIfNull(approval);

            // A bot that keeps what it finds posts the armor at its next run, whenever that is, restarts included, so a run
            // since the approval and a start since its last run change nothing: Announce now is offered until its record shows
            // it found the armor, which the caller checks. A stopped bot can't run now. A run with no end that ended with the
            // bot isn't under way.
            if (PostingRules.KeepsPosts(bot))
            {
                if (bot is { stopped_at: not null })
                    return new AnnounceView(AnnounceState.Stopped, Kept: true);

                if (settings is { posts_paused: true })
                    return new AnnounceView(AnnounceState.Paused, Kept: true);

                if (!requestsOn)
                    return new AnnounceView(AnnounceState.Off, Kept: true);

                if (notifications?.last_started_at is { } under && (notifications.last_ended_at is not { } over || over < under) && !JobWords.EndedWithTheBot(under, bot))
                    return new AnnounceView(AnnounceState.Running, under, Kept: true);

                return new AnnounceView(AnnounceState.Offered, Kept: true);
            }

            var approved = DateTime.SpecifyKind(approval.reviewed_at, DateTimeKind.Utc);

            // A skipped run never ran: the bot records one when a run comes due while an earlier run still runs, as one the
            // time limit cut off does 30 seconds on, so it soon takes the cut-off run's place in the status row. One that ended
            // with the bot, from before its current run or before a clean stop it hasn't started again since, tells only of a
            // run that ended with it, so it's no run since the approval, though one before it, which the status row doesn't
            // keep, may have posted the armor.
            if (notifications?.last_started_at is { } started && started > approved
                && !(notifications.last_result == JobResult.Skipped && JobWords.EndedWithTheBot(started, bot)))
                return null;

            // The bot keeps what it has seen only in memory, so its first run after a start takes the approved armor it finds as
            // seen. While the bot is stopped, or has started again with no run recorded since, that run is the next one,
            // Announce now's included, or is under way, as a run is recorded when it ends, and armor neither posted before nor
            // approved during it is lost, paused or not and with requests on or off, so this comes before those. A run that
            // ended with the bot always leads here, so a run with no end that's left is one in the bot's current run.
            if (bot is { stopped_at: not null })
                return new AnnounceView(AnnounceState.Stopped);

            if (bot != null && (notifications?.last_started_at is not { } last || last < bot.started_at))
                return new AnnounceView(AnnounceState.Restarted, StartedAt: bot.started_at);

            // The bot refuses the job while posts are paused, and posts the armor once they're resumed
            if (settings is { posts_paused: true })
                return new AnnounceView(AnnounceState.Paused);

            if (!requestsOn)
                return new AnnounceView(AnnounceState.Off);

            // A run that started before the approval, which may have seen it or not, and the bot refuses another meanwhile
            if (notifications?.last_started_at is { } running && (notifications.last_ended_at is not { } ended || ended < running))
                return new AnnounceView(AnnounceState.Running, running);

            return new AnnounceView(AnnounceState.Offered);
        }
    }
}
