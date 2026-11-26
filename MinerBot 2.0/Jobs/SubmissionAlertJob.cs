using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using Discord.WebSocket;
using MinerBot.Shared.Delivery;
using MinerBot_2._0.Attributes;
using MinerBot_2._0.Models;
using MinerBot_2._0.Posting;
using MinerBot_2._0.Services;
using MinerBot_2._0.Status;

namespace MinerBot_2._0.Jobs
{
    // Tells the owner in a direct message when armor is sent in for a review, at most once an hour while new armor waits.
    // New is an armor waiting for a review that the owner hasn't been told about: above the highest armor ID of any status
    // at the bot's first look, so an old armor moved back to pending is never new, and with no record in notification_seen,
    // so two sent in at once are both told about, whichever the website saved first. The first look takes the armor already
    // waiting as seen, with no message. The hour and what was told are kept in MinerBot's database, so a restart resets
    // neither, and without BotConnection the job does nothing. Each try is saved before its message goes, so a try that
    // can't be saved sends nothing; a message Discord refused, or one that never went because Discord failed before its own
    // request or answered its tries only with rate limits or 502s until its time was up, is told again after the hour, and
    // the run fails, so the Overview shows it; one whose answer never came isn't, as it may have arrived, and nor is one
    // still under way when the run stops waiting, as it may still go, even if Discord fails before its request after that:
    // only OwnerAlertService's alert.failed records it. The message holds counts, a date and the dashboard's link, never an
    // armor's name or its author's, which are the submitter's own text.
    public class SubmissionAlertJob(IArmorData armor, INotificationData store, OwnerAlertService alerts, DiscordSocketClient client, BotRecorder recorder,
        IConfiguration configuration, TimeProvider clock)
    {
        // The dashboard's list of the armor waiting for a review, at the dashboard's own public address
        public const string PendingLink = "https://bot.murderminershub.com/admin/armor?status=pending";

        // One try an hour at most, whatever became of the last
        internal static readonly TimeSpan Every = TimeSpan.FromHours(1);

        // How long a run waits for its message. Shorter in tests.
        internal TimeSpan SendLimit = TimeSpan.FromSeconds(30);

        [Job("2-59/5 * * * *")] // Every 5 minutes, 2 minutes after each notification run, so the two never share a pass
        public async Task<string> AlertOwner()
        {
            // There's nowhere to keep the hour or what was told without MinerBot's database, as for the clean-up
            if (StatusWriter.BotConnectionProblem(configuration) is { } problem)
                return SubmissionAlertTexts.Skipped(problem);

            // OwnerAlertService waits up to 2 minutes for Discord, which a run of this job never does: the next run looks
            if (client.CurrentUser == null)
                return SubmissionAlertTexts.DiscordNotReady;

            // To the millisecond, as MinerBot's database keeps it
            var started = clock.GetUtcNow().UtcDateTime;
            var now = started.AddTicks(-(started.Ticks % TimeSpan.TicksPerMillisecond));

            List<PendingSubmission> pending;

            try
            {
                pending = [.. (await armor.GetPendingSubmissions()).Where(submission => submission.ID > 0)];
            }
            catch (Exception e)
            {
                // The website's database being down is a failure of the notification run already, and of the pages: this run
                // says so in its detail rather than raise a failure every 5 minutes
                return SubmissionAlertTexts.WebsiteDidntAnswer(FailureLog.Describe(e));
            }

            var row = (await DatabaseAsync(store.ListBaselinesAsync)).FirstOrDefault(baseline => baseline.source == PostSource.Submissions);

            if (row == null)
                return await FirstLookAsync(pending, now);

            var seen = (await DatabaseAsync(() => store.ListSeenAsync(PostSource.Submissions, [.. pending.Select(submission => (ulong)submission.ID)])))
                .ToDictionary(told => told.item_id);
            var floor = row.high_id ?? 0;

            // Above the floor, and never told about, or told about in a message that failed
            var fresh = pending.Where(submission => (ulong)submission.ID > floor
                && (!seen.TryGetValue((ulong)submission.ID, out var told) || told.state == SeenState.AlertFailed)).ToList();

            if (fresh.Count == 0)
                return SubmissionAlertTexts.NoneNew(pending.Count);

            if (row.alerted_at is { } lastTry && now - lastTry < Every)
                return SubmissionAlertTexts.WaitsForTheHour(fresh.Count, lastTry);

            // The try, and what it tells about, before the message, in one transaction: a try that can't be saved sends
            // nothing, so a restart can never make two in one hour
            try
            {
                await store.SaveDetectionAsync(new Detection
                {
                    At = now,
                    Baselines = [row with { alerted_at = now, checked_at = now, checked_by_run = recorder.RunId }],
                    Seen = Told(fresh, seen, SeenState.Alerted, now)
                });
            }
            catch (Exception e)
            {
                // A save whose answer didn't come may have landed all the same, which would leave these told about with no
                // message: marked as told in a message that failed, they're told at the next run if the try didn't land, and
                // once its hour is over if it did
                try
                {
                    await MarkFailedAsync();
                }
                catch (Exception)
                {
                    // MinerBot's database answers nothing now: the run's failure says the try wasn't saved
                }

                throw new JobFailedException(SubmissionAlertTexts.TryNotSaved(FailureLog.Describe(e)));
            }

            var message = SubmissionAlertTexts.Message(fresh.Count, pending.Count, pending.Min(submission => submission.CreatedAt));

            // Whether the message's own request was made: Discord failing before it, as at the direct channel's opening, sent
            // nothing
            var requested = false;
            var sending = alerts.SendAsync(message, SubmissionAlertTexts.Record(fresh.Count, pending.Count), () => requested = true);

            try
            {
                await sending.WaitAsync(SendLimit);
            }
            catch (TimeoutException e) when (!ReferenceEquals(e, sending.Exception?.InnerException))
            {
                // The run's own wait ran out, whatever became of the send since: a TimeoutException of the send's own
                // comes through WaitAsync as the same object, and is read below. It may still go, and OwnerAlertService
                // records how it ends: what it tells about stays told, so at most once
                return SubmissionAlertTexts.Unanswered(fresh.Count, pending.Count, SendLimit);
            }
            catch (Exception e) when (requested && MayHaveArrived(e))
            {
                return SubmissionAlertTexts.NoAnswer(fresh.Count, pending.Count, FailureLog.Describe(e));
            }
            catch (Exception e)
            {
                // Discord refused it, or it never went, so these are told about again after the hour
                try
                {
                    await MarkFailedAsync();
                }
                catch (Exception notSaved)
                {
                    throw new JobFailedException(SubmissionAlertTexts.FailedNotRecorded(FailureLog.Describe(e), FailureLog.Describe(notSaved)));
                }

                throw new JobFailedException(SubmissionAlertTexts.Failed(FailureLog.Describe(e), now + Every));
            }

            return SubmissionAlertTexts.Sent(fresh.Count, pending.Count);

            // What the try tells about, as told in a message that failed, which is new again once the try's hour is over. The
            // try's time stays as saved, so the hour counts whatever became of it.
            Task MarkFailedAsync() => store.SaveDetectionAsync(new Detection { At = now, Seen = Told(fresh, seen, SeenState.AlertFailed, now) });
        }

        // The floor, the highest armor ID of any status now, and the armor waiting now as seen, with no message: the backlog
        // the Overview already shows brings none
        async Task<string> FirstLookAsync(List<PendingSubmission> pending, DateTime now)
        {
            ulong highest;

            try
            {
                highest = (ulong)Math.Max(0, await armor.GetHighestArmorId());
            }
            catch (Exception e)
            {
                return SubmissionAlertTexts.WebsiteDidntAnswer(FailureLog.Describe(e));
            }

            try
            {
                await store.SaveDetectionAsync(new Detection
                {
                    At = now,
                    Baselines =
                    [
                        new BaselineRow
                        {
                            source = PostSource.Submissions,
                            taken_at = now,
                            taken_by_run = recorder.RunId,
                            reason = BaselineReason.First,
                            item_count = (uint)pending.Count,
                            high_id = highest,
                            checked_at = now,
                            checked_by_run = recorder.RunId
                        }
                    ],
                    Seen = [.. pending.Select(submission => Seen((ulong)submission.ID, SeenState.Baseline, now, now))]
                });
            }
            catch (Exception e)
            {
                throw new JobFailedException(SubmissionAlertTexts.FirstLookNotSaved(FailureLog.Describe(e)));
            }

            return SubmissionAlertTexts.FirstLook(pending.Count, highest);
        }

        // Each submission's record as told, or as told in a message that failed, which keeps when it was first seen
        static List<SeenRow> Told(List<PendingSubmission> fresh, Dictionary<ulong, SeenRow> seen, string state, DateTime now) =>
            [.. fresh.Select(submission => Seen((ulong)submission.ID, state, seen.TryGetValue((ulong)submission.ID, out var before) ? before.first_seen_at : now, now))];

        static SeenRow Seen(ulong id, string state, DateTime firstSeen, DateTime now) => new()
        {
            source = PostSource.Submissions,
            item_id = id,
            state = state,
            first_seen_at = firstSeen,
            last_seen_at = now
        };

        // A message whose own request was made and didn't go, but may have arrived: Discord didn't answer it, or answered with
        // its own trouble, as the posts' rule reads it. Told about again, it could arrive twice. Not a TimeoutException: under
        // Discord.Net's default retries one comes only before a try, once tries Discord answered with a rate limit or a 502,
        // neither of which it acts on, used up the request's 15 s, so the message never went. Told about again after the hour,
        // it arrives twice only where a 502 went against Discord's documentation, as with Discord.Net's own resend after one.
        static bool MayHaveArrived(Exception e) => e is not TimeoutException && DiscordFailures.Classify(e) is { InDoubt: true, Reason: DeliveryReasons.NoAnswer };

        // A read of MinerBot's database this run can't go on without: it fails, saying so, with the exception's type and
        // MySQL's number, never its message
        static async Task<T> DatabaseAsync<T>(Func<Task<T>> call)
        {
            try
            {
                return await call();
            }
            catch (Exception e)
            {
                throw new JobFailedException(SubmissionAlertTexts.DatabaseDidntAnswer(FailureLog.Describe(e)));
            }
        }
    }
}
