using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using Discord;
using Discord.WebSocket;
using MinerBot.Shared.Delivery;
using MinerBot_2._0.Services;
using MinerBot_2._0.Status;
using System.Security.Cryptography;

namespace MinerBot_2._0.Posting
{
    // What a run's delivery did with one kind: the items it tried to post, and in how many servers they went, failed or
    // weren't answered
    public sealed class KindTally
    {
        public HashSet<ulong> Items { get; } = new();
        public int Tried, Posted, Failed, Unanswered;
    }

    // Why a run stopped posting before it was through: Discord rate-limited the bot or refused its token, the bot couldn't
    // connect to Discord, Discord didn't answer two posts, MinerBot's database didn't take a claim, a settle, a refusal or a
    // drop, the run reached its limit of messages or of time, or the bot began to stop
    public enum RunStop
    {
        RateLimited,
        Unauthorized,
        Unreachable,
        Unanswered,
        Database,
        MessageLimit,
        TimeLimit,
        Stopping
    }

    // What a run's delivery did, for its detail: each kind's tally, the rows still waiting when it ended, and why it stopped
    // posting early, when it did
    public sealed class DeliveryReport
    {
        readonly Dictionary<ulong, QueueRow> waiting = new();

        public Dictionary<string, KindTally> Kinds { get; } = new(StringComparer.Ordinal);

        public RunStop? Stopped { get; set; }

        // What MinerBot's database answered, when it stopped the run: the exception's type and MySQL's number
        public string? Problem { get; set; }

        public IEnumerable<QueueRow> Waiting => waiting.Values;

        public KindTally Kind(string source) => Kinds.TryGetValue(source, out var tally) ? tally : Kinds[source] = new KindTally();

        // Rows that wait, and rows that don't any more: dropped, or taken by a message
        public void Wait(IEnumerable<QueueRow> rows)
        {
            foreach (var row in rows)
                waiting[row.id] = row;
        }

        public void Leave(IEnumerable<ulong> queueIds)
        {
            foreach (var id in queueIds)
                waiting.Remove(id);
        }
    }

    // Delivers what waits, a server's kind at a time, oldest row first, so the ones a run doesn't reach go first next time.
    // For each, in this order: a server that's off for the kind now has what waits dropped, with the reason, even behind a
    // message of it not settled yet; such a message holds the rest back; a server Discord reports unavailable, or
    // hasn't sent, waits, since an outage isn't a failure; rows past their kind's time limit are dropped as too old; a kind
    // waiting after failures in a row waits on, unless what could fix it changed (Backoff); a channel that isn't one the bot
    // can post in, or a permission the post needs missing there, is a refusal from the cache, which costs Discord nothing;
    // the items gone are dropped; then each message is claimed, sent and settled.
    //
    // A message is sent only once its claim has committed: its delivery row as sending and its rows taken from pending, with
    // the server's rows for the maps' updates it shows (MessagePlan), in one transaction, which another run can't share. The
    // send leaves Discord.Net to wait out a rate limit, which Discord didn't act on, within the request's own limit of 15 s,
    // and never to send again after a timeout or a 502, which it may have acted on. Then the settle: sent, with Discord's
    // message ID; failed, with Discord's numbers, its rows back to pending, counting against the server's kind, with the wait
    // before its next try, when it was that server's own refusal, and counting nothing when the send never left the bot; or
    // in doubt, when Discord didn't answer, its rows held until the bot finds out. What MinerBot's database doesn't take of a
    // claim or a settle waits in PendingSettlements.
    //
    // The run stops posting at a rate limit that reaches the bot, a refused token or a connection to Discord that wouldn't
    // open, which are about every post, after two posts Discord didn't answer, and at a write MinerBot's database didn't
    // take, whose rows wait for the next run; it claims at most 50 messages, none after 200 s, and none once the host begins
    // to stop. The run's detail says why it stopped.
    public sealed class Deliverer(DiscordSocketClient client, INotificationData store, MessagePlan plan, GroupCheck check, IReadOnlyCollection<(ulong GuildId, string Source)> unsettled,
        IReadOnlyDictionary<(ulong GuildId, string Source), DeliveryStatusRow> statuses, PostingGate gate, PendingSettlements pending, PostingProcess process, ulong runId,
        DateTime runStartedAt, TimeProvider clock)
    {
        // Every request of the posts, a message or a read of a channel's messages: Discord.Net may wait out a rate limit, and
        // never sends again after a timeout or a 502. It checks Timeout only before each try, so the token given, from
        // PostingGate.Limit, is what ends a request at its own limit of 15 seconds, or when the stop abandons it.
        public static RequestOptions SendOptions(CancellationToken limit) => new()
        {
            RetryMode = RetryMode.RetryRatelimit,
            Timeout = 15000,
            CancelToken = limit
        };

        readonly DeliveryReport report = new();
        bool stopped;
        int claimed, unanswered;

        // A server's kind's failures in a row before this run's try, and what its wait would start from
        readonly record struct GroupWait(uint Failures, ulong WaitingOn);

        DateTime Now() => clock.GetUtcNow().UtcDateTime;

        public async Task<DeliveryReport> DeliverAsync(IReadOnlyList<QueueRow> open)
        {
            report.Wait(open);

            foreach (var group in open.GroupBy(row => (row.guild_id, row.source)))
            {
                if (!CanClaim())
                    break;

                await GroupAsync(group.Key.guild_id, group.Key.source, group.ToList());
            }

            return report;
        }

        // Whether another message may be claimed: not once the database or Discord stopped the run, past its limits, or once
        // the host is stopping
        bool CanClaim()
        {
            if (stopped)
                return false;

            if (!gate.IsOpen)
                return Stop(RunStop.Stopping);

            if (claimed >= PostingLimits.MessagesPerRun)
                return Stop(RunStop.MessageLimit);

            if (Now() - runStartedAt >= PostingLimits.ClaimTime)
                return Stop(RunStop.TimeLimit);

            return true;
        }

        // The run posts nothing more, and its detail says why
        bool Stop(RunStop stop, string? problem = null)
        {
            stopped = true;

            if (report.Stopped == null)
            {
                report.Stopped = stop;
                report.Problem = problem;
                Console.WriteLine(PostingTexts.StoppedPosting(stop, problem));
            }

            return false;
        }

        async Task GroupAsync(ulong guildId, string source, List<QueueRow> rows)
        {
            // Off for the kind now, even behind a message of it not settled yet: what waits is dropped there, so what was found
            // before the off never goes once the kind is on again. Only pending rows are dropped, so the message's own rows wait
            // for the look, and nothing is sent.
            if (check.OffReason(guildId, source) is { } off)
            {
                await DropAsync(rows, off);
                return;
            }

            // A message of it isn't settled yet, so nothing more goes there until it is
            if (unsettled.Contains((guildId, source)))
                return;

            var channelId = check.ChannelOf(guildId, source);

            if (DiscordService.UsableGuild(client, guildId, out var problem) is not { } guild)
            {
                Console.WriteLine(PostingTexts.CouldntPost(source, channelId, guildId, problem switch
                {
                    PostProblem.ServerUnavailable => PostingTexts.ServerUnavailable,
                    PostProblem.NotInServer => PostingTexts.NotInServer,
                    _ => PostingTexts.ServerNotSent
                }));

                return;
            }

            // By the run's start, so what this run found is never too old in it
            var tooOld = rows.Where(row => GroupCheck.TooOld(row, runStartedAt)).ToList();

            if (tooOld.Count > 0)
            {
                await DropAsync(tooOld, DeliveryReasons.TooOld);
                rows = rows.Except(tooOld).ToList();

                if (rows.Count == 0 || stopped)
                    return;
            }

            // After failures in a row it waits, with no row, unless its time has come or something that could fix it changed
            var status = statuses.GetValueOrDefault((guildId, source));
            var wait = new GroupWait(status?.failures ?? 0, Backoff.WaitingOn(guild, channelId, check.SwitchesVersion(guildId)));

            if (Backoff.Waits(status, runStartedAt, wait.WaitingOn))
                return;

            // The cache first: a channel of this server that Discord.Net counts as a text channel, by DiscordService.PostChannel's
            // rule, so voice and stage channels' chats still get posts, and a forum or a deleted channel is a refusal
            if (guild.GetTextChannel(channelId) is not { } channel)
            {
                Console.WriteLine(PostingTexts.CouldntPost(source, channelId, guildId, PostingTexts.ChannelNotInServer));
                await RefuseAsync(guildId, channelId, source, rows, DeliveryReasons.NoChannel, null, wait);
                return;
            }

            if (guild.CurrentUser is not { } bot)
                return;

            var missing = GroupCheck.MissingMask(bot, channel);

            if (missing != 0)
            {
                Console.WriteLine(PostingTexts.CouldntPost(source, channelId, guildId, PostingTexts.Missing(missing)));
                await RefuseAsync(guildId, channelId, source, rows, DeliveryReasons.MissingPermissions, (byte)missing, wait);
                return;
            }

            GroupPlan post;

            try
            {
                post = await plan.PlanAsync(source, rows);
            }
            catch (Exception e)
            {
                Console.WriteLine(PostingTexts.NotBuilt(source, guildId, e.GetType().Name));
                return;
            }

            foreach (var reason in post.Drops.GroupBy(drop => drop.Reason))
            {
                var ids = reason.Select(drop => drop.QueueId).ToHashSet();
                await DropAsync(rows.Where(row => ids.Contains(row.id)).ToList(), reason.Key);
            }

            if (post.Messages.Count == 0)
                return;

            var itemOf = rows.ToDictionary(row => row.id, row => row.item_id);
            var items = post.Messages.SelectMany(message => message.QueueIds).Select(id => itemOf[id]).ToHashSet();
            var outcome = Sent.Yes;
            var part = 1;

            for (; part <= post.Messages.Count && outcome == Sent.Yes; part++)
            {
                outcome = await SendAsync(guildId, channel, source, (byte)part, post.Messages[part - 1], rows, wait);

                // A message that went ends the failures in a row
                if (outcome == Sent.Yes)
                    wait = wait with { Failures = 0 };
            }

            // Its first message taken by another run, or never claimed: nothing of it was sent, and it isn't this server's
            // failure
            if (outcome == Sent.NotClaimed && part == 2)
                return;

            var tally = report.Kind(source);
            tally.Tried++;
            tally.Items.UnionWith(items);

            // A post whose later messages weren't claimed went in part, and what's left of it waits
            switch (outcome)
            {
                case Sent.Failed:
                    tally.Failed++;
                    break;
                case Sent.InDoubt:
                    tally.Unanswered++;
                    break;
                default:
                    tally.Posted++;
                    break;
            }
        }

        enum Sent
        {
            Yes,
            Failed,
            InDoubt,
            NotClaimed
        }

        // Claim, send and settle one message, counted as in flight for the stop
        async Task<Sent> SendAsync(ulong guildId, SocketTextChannel channel, string source, byte part, PlannedMessage message, List<QueueRow> rows, GroupWait wait)
        {
            if (!CanClaim())
                return Sent.NotClaimed;

            if (!gate.TryEnter())
            {
                Stop(RunStop.Stopping);
                return Sent.NotClaimed;
            }

            try
            {
                return await ClaimAndSendAsync(guildId, channel, source, part, message, rows, wait);
            }
            finally
            {
                gate.Leave();
            }
        }

        async Task<Sent> ClaimAndSendAsync(ulong guildId, SocketTextChannel channel, string source, byte part, PlannedMessage message, List<QueueRow> rows, GroupWait wait)
        {
            var attemptKey = NewKey();

            try
            {
                await store.ClaimAsync(new DeliveryClaim
                {
                    AttemptKey = attemptKey,
                    RunId = runId,
                    RunStartedAt = runStartedAt,
                    GuildId = guildId,
                    ChannelId = channel.Id,
                    Source = source,
                    Part = part,
                    AttemptedAt = Now(),
                    QueueIds = message.QueueIds,
                    Shows = message.Shows
                });
            }
            catch (ClaimLostException)
            {
                Console.WriteLine(PostingTexts.ClaimLost(source, guildId));
                Taken(message);
                return Sent.NotClaimed;
            }
            catch (Exception e)
            {
                // It may have landed, and nothing was sent for it, so its rows go back to pending at the next run, or once the
                // look in the channel's history finds it wasn't made, after a restart: they still wait
                var problem = FailureLog.Describe(e);
                Console.WriteLine(PostingTexts.ClaimFailed(source, guildId, problem));
                pending.Add(new Settlement { AttemptKey = attemptKey, Outcome = DeliveryState.Failed, At = Now(), Reason = DeliveryReasons.NotSent });
                Stop(RunStop.Database, problem);
                return Sent.NotClaimed;
            }

            claimed++;

            // Its rows aren't waiting any more, whatever comes of the send
            Taken(message);

            Settlement settlement;
            Sent sent;

            try
            {
                using var limit = gate.Limit();
                var posted = await channel.SendMessageAsync(text: message.Text, embeds: message.Embeds, options: SendOptions(limit.Token), allowedMentions: AllowedMentions.None);

                process.Answered(Now());
                settlement = new Settlement { AttemptKey = attemptKey, Outcome = DeliveryState.Sent, At = Now(), MessageId = posted.Id };
                sent = Sent.Yes;
            }
            catch (Exception e)
            {
                var failure = DiscordFailures.ClassifyPost(e, stopping: gate.Abandon.IsCancellationRequested);

                // The server's own refusal is Discord's answer too, which the look goes by past a message's hour
                if (failure.Counts)
                    process.Answered(Now());

                // The server's own refusal waits before its next try, from what could fix it
                settlement = new Settlement
                {
                    AttemptKey = attemptKey,
                    Outcome = failure.State,
                    At = Now(),
                    Reason = failure.Reason,
                    HttpStatus = failure.HttpStatus,
                    DiscordCode = failure.DiscordCode,
                    Counts = failure.Counts,
                    NextTryAt = failure.Counts ? Backoff.NextTry(runStartedAt, wait.Failures + 1) : null,
                    WaitingOn = failure.Counts ? wait.WaitingOn : null
                };

                if (failure.InDoubt)
                {
                    Console.WriteLine(PostingTexts.Unanswered(source, channel.Id, guildId, failure.HttpStatus));
                    sent = Sent.InDoubt;

                    // The stop that abandoned it, or Discord not answering, which two unanswered posts in a run take it to be
                    if (gate.Abandon.IsCancellationRequested)
                        Stop(RunStop.Stopping);
                    else if (++unanswered >= PostingLimits.UnansweredPerRun)
                        Stop(RunStop.Unanswered);
                }
                else
                {
                    Console.WriteLine(PostingTexts.Refused(source, channel.Id, guildId, failure.Reason!, failure.HttpStatus, failure.DiscordCode));
                    sent = Sent.Failed;

                    // About every post, not this channel's, or the stop that came before the send: nothing more is sent this
                    // run. A send that never left the bot, not at the stop, is a connection to Discord that won't open.
                    if (failure.Reason == DeliveryReasons.RateLimited)
                        Stop(RunStop.RateLimited);
                    else if (failure.Reason == DeliveryReasons.Unauthorized)
                        Stop(RunStop.Unauthorized);
                    else if (gate.Abandon.IsCancellationRequested)
                        Stop(RunStop.Stopping);
                    else if (failure.Reason == DeliveryReasons.NotSent)
                        Stop(RunStop.Unreachable);
                }
            }

            try
            {
                await store.SettleAsync(settlement);
            }
            catch (Exception e)
            {
                // The message went, or didn't, and the database doesn't know yet: its rows stay sending, so none goes twice, and
                // the next run writes it first
                var problem = FailureLog.Describe(e);
                Console.WriteLine(PostingTexts.SettleFailed(source, channel.Id, guildId, problem));
                pending.Add(settlement);
                Stop(RunStop.Database, problem);
                return sent;
            }

            // A refusal puts its rows back to pending, for a later run
            if (settlement.Outcome == DeliveryState.Failed)
            {
                var carried = message.QueueIds.ToHashSet();
                report.Wait(rows.Where(row => carried.Contains(row.id)));
            }

            return sent;
        }

        // A refusal the cache saw first, which counts against the server's kind, with the wait before its next try: its rows
        // stay pending
        async Task RefuseAsync(ulong guildId, ulong channelId, string source, List<QueueRow> rows, string reason, byte? missing, GroupWait wait)
        {
            if (!CanClaim())
                return;

            var tally = report.Kind(source);
            tally.Tried++;
            tally.Failed++;

            foreach (var row in rows)
                tally.Items.Add(row.item_id);

            try
            {
                await store.RecordRefusalAsync(new Refusal
                {
                    AttemptKey = NewKey(),
                    RunId = runId,
                    RunStartedAt = runStartedAt,
                    GuildId = guildId,
                    ChannelId = channelId,
                    Source = source,

                    // Its items, as a message counts them: a map's rows for two of its updates are one map
                    ItemCount = (ushort)Math.Min(rows.Select(row => row.item_id).Distinct().Count(), ushort.MaxValue),
                    At = Now(),
                    Reason = reason,
                    Missing = missing,
                    NextTryAt = Backoff.NextTry(runStartedAt, wait.Failures + 1),
                    WaitingOn = wait.WaitingOn
                });
            }
            catch (Exception e)
            {
                var problem = FailureLog.Describe(e);
                Console.WriteLine(PostingTexts.SettleFailed(source, channelId, guildId, problem));
                Stop(RunStop.Database, problem);
            }
        }

        async Task DropAsync(List<QueueRow> rows, string reason)
        {
            if (rows.Count == 0)
                return;

            try
            {
                await store.DropAsync(rows.Select(row => row.id).ToList(), reason, Now());
                report.Leave(rows.Select(row => row.id));
            }
            catch (Exception e)
            {
                var problem = FailureLog.Describe(e);
                Console.WriteLine(PostingTexts.DropFailed(rows[0].source, rows[0].guild_id, problem));
                Stop(RunStop.Database, problem);
            }
        }

        void Taken(PlannedMessage message) => report.Leave(message.QueueIds);

        // The bot's own random number for an attempt, by which its writes land once
        static ulong NewKey()
        {
            while (true)
            {
                var key = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(sizeof(ulong)));

                if (key != 0)
                    return key;
            }
        }
    }
}
