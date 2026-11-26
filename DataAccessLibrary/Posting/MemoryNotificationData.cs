using DataAccessLibrary.Posting.Models;
using DataAccessLibrary.Status;
using MinerBot.Shared.Delivery;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DataAccessLibrary.Posting
{
    // The posting store in memory, by NotificationData's rules, for a bot without BotConnection, which then posts as it always
    // has and loses what it holds at a restart, as it always did, and for the bot's tests, where one store shared by two runs
    // of the bot is a restart that keeps the database. It checks what it's given as NotificationData does, keeps times to the
    // millisecond as the tables do, and hands out copies, so nothing outside changes what it holds. It keeps everything until
    // the process ends, as the bot's memory did before; Docker's nightly restart ends it.
    public sealed class MemoryNotificationData : INotificationData
    {
        readonly Lock gate = new();
        readonly Dictionary<string, BaselineRow> baselines = new(StringComparer.Ordinal);
        readonly Dictionary<ulong, GuildSwitches> switches = new();
        readonly Dictionary<(string Source, ulong ItemId), SeenRow> seen = new();
        readonly List<QueueRow> queue = new();
        readonly List<DeliveryRow> deliveries = new();
        readonly Dictionary<(ulong GuildId, string Source), DeliveryStatusRow> statuses = new();
        readonly List<(string Job, DateTime StartedAt, string Result, string? Detail)> jobRuns = new();
        ulong lastQueueId, lastDeliveryId;

        static readonly Regex AskedFor = new(NotificationData.AskedFor, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        static readonly Regex LostMaps = new(NotificationData.LostMaps, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        public Task<PostingState> LoadAsync(PostingLoad load)
        {
            PostingChecks.Require(load);

            lock (gate)
            {
                (string Source, IReadOnlyCollection<ulong> Ids)[] wanted = [(PostSource.Armor, load.ArmorIds), (PostSource.Lobbies, load.LobbyIds)];
                var armorIds = load.ArmorIds.ToHashSet();

                return Task.FromResult(new PostingState
                {
                    Baselines = BaselineList(),
                    Switches = [.. switches.Values.OrderBy(row => row.guild_id).Take(NotificationData.SwitchLimit).Select(row => row with { })],
                    Statuses = [.. statuses.Values.OrderBy(row => row.guild_id).ThenBy(row => row.source, StringComparer.Ordinal).Take(NotificationData.StatusLimit)
                        .Select(row => row with { })],
                    Open = OpenList(),
                    Unsettled = [.. deliveries.Where(row => DeliveryState.IsOpen(row.state)).OrderBy(row => row.attempted_at).ThenBy(row => row.id)
                        .Take(NotificationData.UnsettledLimit).Select(row => row with { })],
                    Seen = [.. wanted.SelectMany(source => source.Ids.Distinct().Select(id => seen.GetValueOrDefault((source.Source, id))).OfType<SeenRow>())
                        .OrderBy(row => row.source, StringComparer.Ordinal).ThenBy(row => row.item_id).Select(row => row with { })],
                    Withdrawn = [.. queue.Where(row => row.source == PostSource.Armor && armorIds.Contains(row.item_id) && row.state == QueueState.Dropped
                        && row.reason == DeliveryReasons.NotApproved).OrderBy(row => row.item_id).ThenBy(row => row.guild_id).ThenBy(row => row.id).Select(row => row with { })]
                });
            }
        }

        public Task<IReadOnlyList<QueueRow>> ListOpenAsync()
        {
            lock (gate)
                return Task.FromResult(OpenList());
        }

        public Task<IReadOnlyList<BaselineRow>> ListBaselinesAsync()
        {
            lock (gate)
                return Task.FromResult(BaselineList());
        }

        public Task<IReadOnlyList<SeenRow>> ListSeenAsync(string source, IReadOnlyCollection<ulong> itemIds)
        {
            PostingChecks.RequireSource(source);
            PostingChecks.RequireIds(itemIds, nameof(itemIds));

            lock (gate)
            {
                IReadOnlyList<SeenRow> rows = [.. itemIds.Distinct().Select(id => seen.GetValueOrDefault((source, id))).OfType<SeenRow>().OrderBy(row => row.item_id)
                    .Select(row => row with { })];
                return Task.FromResult(rows);
            }
        }

        public Task<IReadOnlyList<QueueRow>> ListHiddenAsync(IReadOnlyCollection<ulong> mapIds)
        {
            PostingChecks.RequireIds(mapIds, nameof(mapIds));

            lock (gate)
            {
                var ids = mapIds.ToHashSet();

                IReadOnlyList<QueueRow> rows = [.. queue.Where(row => row.source == PostSource.Maps && ids.Contains(row.item_id) && row.state == QueueState.Dropped
                    && row.reason == DeliveryReasons.Hidden).OrderBy(row => row.item_id).ThenBy(row => row.guild_id).ThenBy(row => row.id).Select(row => row with { })];
                return Task.FromResult(rows);
            }
        }

        // As the SQL's, which reads job_runs: this store has no runs unless a test adds them
        public Task<int> CountOlderBotRunsAsync(string job, DateTime since)
        {
            PostingChecks.RequireJob(job, since);

            lock (gate)
            {
                return Task.FromResult(jobRuns.Count(run => string.Equals(run.Job, job, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(run.Result, JobResult.Ok, StringComparison.OrdinalIgnoreCase) && run.StartedAt > Ms(since)
                    && (run.Detail == null || AskedFor.IsMatch(run.Detail) || LostMaps.IsMatch(run.Detail))));
            }
        }

        public Task SaveDetectionAsync(Detection detection)
        {
            PostingChecks.Require(detection);

            lock (gate)
            {
                foreach (var row in detection.Baselines)
                {
                    baselines[row.source] = row with
                    {
                        taken_at = Ms(row.taken_at),
                        baseline_time = Ms(row.baseline_time),
                        alerted_at = Ms(row.alerted_at),
                        checked_at = Ms(row.checked_at)
                    };
                }

                foreach (var row in detection.Seen)
                {
                    var key = (row.source, row.item_id);
                    var first = seen.TryGetValue(key, out var before) ? before.first_seen_at : Ms(row.first_seen_at);

                    seen[key] = row with
                    {
                        first_seen_at = first,
                        last_seen_at = Ms(row.last_seen_at),
                        updated_at = Ms(row.updated_at),
                        queued_at = Ms(row.queued_at)
                    };
                }

                foreach (var item in detection.Queue)
                {
                    if (queue.Exists(row => row.guild_id == item.guild_id && row.source == item.source && row.item_id == item.item_id && row.revision == item.revision))
                        continue;

                    queue.Add(new QueueRow
                    {
                        id = ++lastQueueId,
                        guild_id = item.guild_id,
                        source = item.source,
                        item_id = item.item_id,
                        revision = item.revision,
                        is_new = item.is_new,
                        queued_at = Ms(item.queued_at),
                        state = QueueState.Pending
                    });
                }

                // As NotificationData's: a map's update for a server whose row of it was dropped because the server wasn't owed maps
                // then waits again, as what a post that wasn't made showed
                var updates = detection.Queue.Where(item => item.source == PostSource.Maps && !item.is_new).Select(item => (item.guild_id, item.item_id, item.revision)).ToHashSet();

                foreach (var row in queue.Where(row => row.source == PostSource.Maps && updates.Contains((row.guild_id, row.item_id, row.revision)) && row.state == QueueState.Dropped
                    && row.reason is { } reason && NotificationData.ServerOff.Contains(reason)))
                {
                    row.state = QueueState.Pending;
                    row.queued_at = Ms(detection.At);
                    row.delivery_id = null;
                    row.reason = null;
                    row.settled_at = null;
                }

                var reopen = detection.ReopenArmor.ToHashSet();

                foreach (var row in queue.Where(row => row.source == PostSource.Armor && reopen.Contains(row.item_id) && row.state == QueueState.Dropped
                    && row.reason == DeliveryReasons.NotApproved))
                {
                    row.state = QueueState.Pending;
                    row.queued_at = Ms(detection.At);
                    row.delivery_id = null;
                    row.reason = null;
                    row.settled_at = null;
                }

                var shown = detection.ReopenMaps.ToHashSet();

                foreach (var row in queue.Where(row => shown.Contains(row.id) && row.source == PostSource.Maps && row.state == QueueState.Dropped
                    && row.reason == DeliveryReasons.Hidden))
                {
                    row.state = QueueState.Pending;
                    row.queued_at = Ms(detection.At);
                    row.delivery_id = null;
                    row.reason = null;
                    row.settled_at = null;
                }
            }

            return Task.CompletedTask;
        }

        public Task<int> DropAsync(IReadOnlyCollection<ulong> queueIds, string reason, DateTime at)
        {
            PostingChecks.RequireDrop(queueIds, reason, at);

            lock (gate)
            {
                var ids = queueIds.ToHashSet();
                var dropped = 0;

                foreach (var row in queue.Where(row => ids.Contains(row.id) && row.state == QueueState.Pending))
                {
                    row.state = QueueState.Dropped;
                    row.reason = reason;
                    row.settled_at = Ms(at);
                    dropped++;
                }

                return Task.FromResult(dropped);
            }
        }

        public Task<int> ClearFailingAsync(ulong guildId, string source, DateTime at)
        {
            PostingChecks.RequirePosted(source);
            DbBaseConnection.RequireUtc(at, nameof(at));

            lock (gate)
            {
                if (!statuses.TryGetValue((guildId, source), out var row)
                    || (row.failing_since == null && row.failures == 0 && row.next_try_at == null && row.waiting_on == null))
                    return Task.FromResult(0);

                row.failing_since = null;
                row.failures = 0;
                row.next_try_at = null;
                row.waiting_on = null;
                row.updated_at = Ms(at);

                return Task.FromResult(1);
            }
        }

        public Task<ulong> ClaimAsync(DeliveryClaim claim)
        {
            PostingChecks.Require(claim);

            lock (gate)
            {
                if (deliveries.Exists(row => row.attempt_key == claim.AttemptKey))
                    throw new ClaimLostException("This attempt was claimed before, so it can't claim again");

                var ids = claim.QueueIds.ToHashSet();
                var rows = queue.Where(row => ids.Contains(row.id) && row.state == QueueState.Pending && row.guild_id == claim.GuildId && row.source == claim.Source).ToList();

                if (rows.Count != claim.QueueIds.Count)
                    throw new ClaimLostException(string.Create(CultureInfo.InvariantCulture,
                        $"Only {rows.Count} of the {claim.QueueIds.Count} rows were still pending for this server and kind, so the claim took none"));

                var id = ++lastDeliveryId;

                deliveries.Add(new DeliveryRow
                {
                    id = id,
                    attempt_key = claim.AttemptKey,
                    run_id = claim.RunId,
                    run_started_at = Ms(claim.RunStartedAt),
                    guild_id = claim.GuildId,
                    channel_id = claim.ChannelId,
                    source = claim.Source,
                    part = claim.Part,
                    item_count = (ushort)rows.Select(row => row.item_id).Distinct().Count(),
                    attempted_at = Ms(claim.AttemptedAt),
                    state = DeliveryState.Sending
                });

                foreach (var row in rows)
                {
                    row.state = QueueState.Sending;
                    row.delivery_id = id;
                }

                foreach (var update in claim.Shows)
                {
                    if (queue.Exists(row => row.guild_id == claim.GuildId && row.source == PostSource.Maps && row.item_id == update.MapId && row.revision == update.Revision))
                        continue;

                    queue.Add(new QueueRow
                    {
                        id = ++lastQueueId,
                        guild_id = claim.GuildId,
                        source = PostSource.Maps,
                        item_id = update.MapId,
                        revision = update.Revision,
                        is_new = false,
                        queued_at = Ms(claim.AttemptedAt),
                        state = QueueState.Sending,
                        delivery_id = id
                    });
                }

                return Task.FromResult(id);
            }
        }

        public Task<bool> SettleAsync(Settlement settlement)
        {
            PostingChecks.Require(settlement);

            lock (gate)
            {
                var delivery = deliveries.Find(row => row.attempt_key == settlement.AttemptKey);
                var outcome = settlement.Outcome;

                if (delivery == null || !DeliveryState.IsOpen(delivery.state) || (outcome == DeliveryState.InDoubt && delivery.state != DeliveryState.Sending))
                    return Task.FromResult(false);

                var at = Ms(settlement.At);
                var carried = queue.Where(row => row.delivery_id == delivery.id && row.state == QueueState.Sending).ToList();

                delivery.state = outcome;

                switch (outcome)
                {
                    case DeliveryState.Sent:
                        delivery.message_id = settlement.MessageId;
                        delivery.settled_at = at;
                        delivery.reason = null;
                        delivery.missing = null;
                        break;

                    case DeliveryState.InDoubt:
                        delivery.reason = settlement.Reason;
                        delivery.http_status = settlement.HttpStatus;
                        delivery.discord_code = settlement.DiscordCode;
                        break;

                    default:
                        delivery.settled_at = at;
                        delivery.reason = outcome == DeliveryState.Unconfirmed ? DeliveryReasons.Unconfirmed : settlement.Reason;
                        delivery.missing = settlement.Missing;
                        delivery.http_status = settlement.HttpStatus ?? delivery.http_status;
                        delivery.discord_code = settlement.DiscordCode ?? delivery.discord_code;
                        break;
                }

                foreach (var row in carried)
                {
                    if (outcome is DeliveryState.Sent or DeliveryState.Unconfirmed)
                    {
                        row.state = QueueState.Sent;
                        row.settled_at = at;

                        if (outcome == DeliveryState.Unconfirmed)
                            row.reason = DeliveryReasons.Unconfirmed;
                    }
                    else if (outcome == DeliveryState.Failed)
                    {
                        row.state = QueueState.Pending;
                        row.delivery_id = null;
                    }
                }

                if (outcome == DeliveryState.Sent)
                {
                    var status = Status(delivery.guild_id, delivery.source, at);
                    status.last_sent_at = delivery.attempted_at;
                    status.last_sent_channel_id = delivery.channel_id;
                    status.last_message_id = settlement.MessageId;
                    status.failing_since = null;
                    status.failures = 0;
                    status.next_try_at = null;
                    status.waiting_on = null;
                    status.updated_at = at;
                }
                else if (settlement.Counts)
                    CountFailure(delivery.guild_id, delivery.source, delivery.attempted_at, delivery.channel_id, settlement.Reason!, settlement.Missing, settlement.HttpStatus,
                        settlement.DiscordCode, settlement.NextTryAt, settlement.WaitingOn, at);

                return Task.FromResult(true);
            }
        }

        public Task<bool> RecordRefusalAsync(Refusal refusal)
        {
            PostingChecks.Require(refusal);

            lock (gate)
            {
                if (deliveries.Exists(row => row.attempt_key == refusal.AttemptKey))
                    return Task.FromResult(false);

                var at = Ms(refusal.At);

                deliveries.Add(new DeliveryRow
                {
                    id = ++lastDeliveryId,
                    attempt_key = refusal.AttemptKey,
                    run_id = refusal.RunId,
                    run_started_at = Ms(refusal.RunStartedAt),
                    guild_id = refusal.GuildId,
                    channel_id = refusal.ChannelId,
                    source = refusal.Source,
                    part = 0,
                    item_count = refusal.ItemCount,
                    attempted_at = at,
                    state = DeliveryState.Failed,
                    settled_at = at,
                    reason = refusal.Reason,
                    missing = refusal.Missing
                });

                CountFailure(refusal.GuildId, refusal.Source, at, refusal.ChannelId, refusal.Reason, refusal.Missing, null, null, refusal.NextTryAt, refusal.WaitingOn, at);

                return Task.FromResult(true);
            }
        }

        public Task<bool> MarkCheckedAsync(ulong attemptKey, byte checks)
        {
            if (attemptKey == 0 || checks == 0)
                throw new ArgumentException("A look is of an attempt, and counts from 1", nameof(checks));

            lock (gate)
            {
                var delivery = deliveries.Find(row => row.attempt_key == attemptKey);

                if (delivery == null || !DeliveryState.IsOpen(delivery.state) || delivery.checks >= checks)
                    return Task.FromResult(false);

                delivery.state = DeliveryState.InDoubt;
                delivery.checks = checks;

                return Task.FromResult(true);
            }
        }

        public Task<int> EndWaitsAsync(ulong guildId, ulong? channelId, DateTime at)
        {
            DbBaseConnection.RequireUtc(at, nameof(at));

            if (channelId == 0)
                throw new ArgumentException("A channel's ID isn't 0", nameof(channelId));

            lock (gate)
            {
                var ended = 0;

                foreach (var row in statuses.Values.Where(row => row.guild_id == guildId && (row.next_try_at != null || row.waiting_on != null)
                    && (channelId == null || row.last_failed_channel_id == channelId)))
                {
                    row.next_try_at = null;
                    row.waiting_on = null;
                    row.updated_at = Ms(at);
                    ended++;
                }

                return Task.FromResult(ended);
            }
        }

        public Task<GuildSwitches?> GetSwitchesAsync(ulong guildId)
        {
            lock (gate)
                return Task.FromResult(switches.TryGetValue(guildId, out var row) ? row with { } : null);
        }

        public Task<IReadOnlyList<ulong>> ListMessageIdsAsync(ulong guildId, ulong channelId, DateTime since)
        {
            DbBaseConnection.RequireUtc(since, nameof(since));

            lock (gate)
            {
                IReadOnlyList<ulong> ids = [.. deliveries.Where(row => row.guild_id == guildId && row.channel_id == channelId && row.attempted_at >= Us(since)
                    && row.message_id != null).OrderBy(row => row.attempted_at).ThenBy(row => row.id).Take(NotificationData.MessageLimit).Select(row => row.message_id!.Value)];
                return Task.FromResult(ids);
            }
        }

        public Task<IReadOnlyList<ulong>> ListCarriedAsync(ulong deliveryId)
        {
            lock (gate)
            {
                IReadOnlyList<ulong> items = [.. queue.Where(row => row.delivery_id == deliveryId && row.state == QueueState.Sending).Select(row => row.item_id).Order()
                    .Take(NotificationData.ClaimLimit)];
                return Task.FromResult(items);
            }
        }

        // What the dashboard would save, for tests: in MariaDB only the dashboard writes switches, and a leave deletes them
        public void SetSwitches(GuildSwitches row)
        {
            ArgumentNullException.ThrowIfNull(row);

            lock (gate)
                switches[row.guild_id] = row with { changed_at = Ms(row.changed_at) };
        }

        // A run of a job as job_runs would hold it, for tests of what an older bot's runs mean
        public void AddJobRun(string job, DateTime startedAt, string result, string? detail)
        {
            lock (gate)
                jobRuns.Add((job, Ms(startedAt), result, detail));
        }

        // Copies of everything held, each in the order of its table's key, for tests to read
        public IReadOnlyList<BaselineRow> Baselines
        {
            get
            {
                lock (gate)
                    return BaselineList();
            }
        }

        public IReadOnlyList<SeenRow> Seen
        {
            get
            {
                lock (gate)
                    return [.. seen.Values.OrderBy(row => row.source, StringComparer.Ordinal).ThenBy(row => row.item_id).Select(row => row with { })];
            }
        }

        public IReadOnlyList<QueueRow> Queue
        {
            get
            {
                lock (gate)
                    return [.. queue.Select(row => row with { })];
            }
        }

        public IReadOnlyList<DeliveryRow> Deliveries
        {
            get
            {
                lock (gate)
                    return [.. deliveries.Select(row => row with { })];
            }
        }

        public IReadOnlyList<DeliveryStatusRow> Statuses
        {
            get
            {
                lock (gate)
                    return [.. statuses.Values.OrderBy(row => row.guild_id).ThenBy(row => row.source, StringComparer.Ordinal).Select(row => row with { })];
            }
        }

        public IReadOnlyList<GuildSwitches> Switches
        {
            get
            {
                lock (gate)
                    return [.. switches.Values.OrderBy(row => row.guild_id).Select(row => row with { })];
            }
        }

        IReadOnlyList<BaselineRow> BaselineList() => [.. baselines.Values.OrderBy(row => row.source, StringComparer.Ordinal).Select(row => row with { })];

        IReadOnlyList<QueueRow> OpenList() =>
            [.. queue.Where(row => row.state == QueueState.Pending).OrderBy(row => row.queued_at).ThenBy(row => row.id).Take(NotificationData.OpenLimit).Select(row => row with { })];

        // The server's status of a kind, made as the table's upsert makes a new one
        DeliveryStatusRow Status(ulong guildId, string source, DateTime at)
        {
            if (!statuses.TryGetValue((guildId, source), out var row))
                statuses[(guildId, source)] = row = new DeliveryStatusRow { guild_id = guildId, source = source, updated_at = at };

            return row;
        }

        // As NotificationData's StatusFailed
        void CountFailure(ulong guildId, string source, DateTime failedAt, ulong channelId, string reason, byte? missing, ushort? httpStatus, uint? discordCode,
            DateTime? nextTryAt, ulong? waitingOn, DateTime at)
        {
            var status = Status(guildId, source, at);
            status.last_failed_at = failedAt;
            status.last_failed_channel_id = channelId;
            status.last_reason = reason;
            status.last_missing = missing;
            status.last_http_status = httpStatus;
            status.last_discord_code = discordCode;
            status.failing_since ??= failedAt;
            status.failures++;
            status.next_try_at = Ms(nextTryAt);
            status.waiting_on = waitingOn;
            status.updated_at = at;
        }

        // DATETIME(3) keeps milliseconds, and MariaDB drops what's finer
        static DateTime Ms(DateTime time) => new(time.Ticks - time.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

        static DateTime? Ms(DateTime? time) => time is { } value ? Ms(value) : null;

        // A time the SQL compares a column with, as MySql.Data sends the parameter: to the microsecond, not cut to the
        // millisecond as a stored time is
        static DateTime Us(DateTime time) => new(time.Ticks - time.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);
    }
}
