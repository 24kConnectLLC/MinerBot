using DataAccessLibrary.Posting.Models;
using DataAccessLibrary.Status;
using MinerBot.Shared.Delivery;
using MySql.Data.MySqlClient;
using System.Globalization;

namespace DataAccessLibrary.Posting
{
    // The posting store in MinerBot's own database, with the bot's own login, which has rights there and nowhere else: no
    // statement here names a website table. INotificationData says the rules; MemoryNotificationData keeps the same ones in
    // memory. A write that failed or timed out may still have landed, so each is safe to repeat, and each that decides
    // something does it with a conditional change that MySql.Data counts by the rows it matched.
    public class NotificationData : DbBaseConnection, INotificationData
    {
        // The most rows a run reads of each: every server's switches, every status (three kinds a server), the items that
        // wait, the messages not settled yet, and one source's items looked up
        public const int SwitchLimit = 2000;
        public const int StatusLimit = SwitchLimit * 3;
        public const int OpenLimit = 5000;
        public const int UnsettledLimit = 1000;
        public const int IdLimit = 5000;

        // The most messages of one channel the look in its history checks against, far more than a run sends there
        public const int MessageLimit = 1000;

        // The most rows one write takes: a detection, a message's items, a drop
        public const int DetectionLimit = 10000;
        public const int ClaimLimit = 1000;
        public const int DropLimit = 5000;

        // The most rows one statement writes, and IDs one statement names, so no statement grows large
        public const int BatchLimit = 100;
        public const int IdChunk = 1000;

        internal const string SwitchColumns = "guild_id, armor_on, maps_on, lobbies_on, version, changed_at, changed_by, changed_by_role";
        internal const string BaselineColumns = "source, taken_at, taken_by_run, reason, item_count, baseline_time, high_id, alerted_at, checked_at, checked_by_run";
        internal const string SeenColumns = "source, item_id, state, first_seen_at, last_seen_at, updated_at, queued_at";
        internal const string QueueColumns = "id, guild_id, source, item_id, revision, is_new, queued_at, state, delivery_id, reason, settled_at";

        internal const string DeliveryColumns = @"id, attempt_key, run_id, run_started_at, guild_id, channel_id, source, part, item_count, attempted_at, state, message_id,
                                                  settled_at, reason, missing, http_status, discord_code, checks";

        internal const string StatusColumns = @"guild_id, source, last_sent_at, last_sent_channel_id, last_message_id, last_failed_at, last_failed_channel_id, last_reason,
                                                last_missing, last_http_status, last_discord_code, failing_since, failures, next_try_at, waiting_on, updated_at";

        // A run of this job by a bot that keeps its posts only in memory: PR 9's runs that posted return no detail, or the maps
        // that waited and weren't found again, and JobHandler puts "asked for on request N" in front of a requested run's.
        // This bot's runs always say what they did, so they never match.
        internal const string AskedFor = "^asked for on request [0-9]+$";
        internal const string LostMaps = "^(asked for on request [0-9]+: )?[0-9]+ maps? that waited (wasn't|weren't) in Steam";

        static readonly string[] OpenStates = [DeliveryState.Sending, DeliveryState.InDoubt];

        // Why a server's waiting items are dropped when it isn't owed their kind any more: no website row, its channel 0, its
        // switch off, the bot not in it, or the bot left it
        internal static readonly string[] ServerOff = [DeliveryReasons.NoSettings, DeliveryReasons.ChannelOff, DeliveryReasons.SwitchedOff, DeliveryReasons.NotInServer,
            DeliveryReasons.Left];

        private readonly DatabaseNames _names;

        public NotificationData(ISQLConnectionFactory connectionFactory, DatabaseNames names) : base(connectionFactory, DBConnectionName.Bot)
        {
            _names = names;
        }

        // In one transaction, so what the run reads is one moment's state
        public Task<PostingState> LoadAsync(PostingLoad load)
        {
            PostingChecks.Require(load);

            var bot = _names.Bot;

            string baselines = $@"SELECT {BaselineColumns}
                                  FROM {bot}.notification_baseline
                                  ORDER BY source";

            string switches = $@"SELECT {SwitchColumns}
                                 FROM {bot}.guild_settings
                                 ORDER BY guild_id
                                 LIMIT {SwitchLimit}";

            string statuses = $@"SELECT {StatusColumns}
                                 FROM {bot}.guild_delivery_status
                                 ORDER BY guild_id, source
                                 LIMIT {StatusLimit}";

            string unsettled = $@"SELECT {DeliveryColumns}
                                  FROM {bot}.notification_deliveries
                                  WHERE state IN @open
                                  ORDER BY attempted_at, id
                                  LIMIT {UnsettledLimit}";

            string seen = SeenSql(bot);

            string withdrawn = $@"SELECT {QueueColumns}
                                  FROM {bot}.notification_queue
                                  WHERE source = @armor AND item_id IN @ids AND state = @dropped AND reason = @notApproved
                                  ORDER BY item_id, guild_id, id";

            string open = OpenSql(bot);

            return InTransaction(async transaction =>
            {
                var seenRows = new List<SeenRow>();

                (string Source, IReadOnlyCollection<ulong> Ids)[] wanted = [(PostSource.Armor, load.ArmorIds), (PostSource.Lobbies, load.LobbyIds)];

                foreach (var (source, ids) in wanted)
                {
                    foreach (var chunk in Sorted(ids).Chunk(IdChunk))
                        seenRows.AddRange(await transaction.Query<SeenRow>(seen, new { source, ids = chunk }).ConfigureAwait(false));
                }

                var withdrawnRows = new List<QueueRow>();

                foreach (var chunk in Sorted(load.ArmorIds).Chunk(IdChunk))
                {
                    withdrawnRows.AddRange(await transaction.Query<QueueRow>(withdrawn,
                        new { armor = PostSource.Armor, ids = chunk, dropped = QueueState.Dropped, notApproved = DeliveryReasons.NotApproved }).ConfigureAwait(false));
                }

                return new PostingState
                {
                    Baselines = await transaction.Query<BaselineRow>(baselines, null).ConfigureAwait(false),
                    Switches = await transaction.Query<GuildSwitches>(switches, null).ConfigureAwait(false),
                    Statuses = await transaction.Query<DeliveryStatusRow>(statuses, null).ConfigureAwait(false),
                    Open = await transaction.Query<QueueRow>(open, new { pending = QueueState.Pending }).ConfigureAwait(false),
                    Unsettled = await transaction.Query<DeliveryRow>(unsettled, new { open = OpenStates }).ConfigureAwait(false),
                    Seen = [.. seenRows.OrderBy(row => row.source, StringComparer.Ordinal).ThenBy(row => row.item_id)],
                    Withdrawn = withdrawnRows
                };
            });
        }

        public async Task<IReadOnlyList<QueueRow>> ListOpenAsync()
        {
            var rows = await LoadDataEnum<QueueRow, object>(OpenSql(_names.Bot), new { pending = QueueState.Pending }).ConfigureAwait(false);
            return rows.ToList();
        }

        public async Task<IReadOnlyList<BaselineRow>> ListBaselinesAsync()
        {
            string sql = $@"SELECT {BaselineColumns}
                            FROM {_names.Bot}.notification_baseline
                            ORDER BY source";

            return (await LoadDataEnum<BaselineRow, object?>(sql, null).ConfigureAwait(false)).ToList();
        }

        public async Task<IReadOnlyList<SeenRow>> ListSeenAsync(string source, IReadOnlyCollection<ulong> itemIds)
        {
            PostingChecks.RequireSource(source);
            PostingChecks.RequireIds(itemIds, nameof(itemIds));

            string sql = SeenSql(_names.Bot);
            var rows = new List<SeenRow>();

            foreach (var chunk in Sorted(itemIds).Chunk(IdChunk))
                rows.AddRange(await LoadDataEnum<SeenRow, object>(sql, new { source, ids = chunk }).ConfigureAwait(false));

            return [.. rows.OrderBy(row => row.item_id)];
        }

        public async Task<IReadOnlyList<QueueRow>> ListHiddenAsync(IReadOnlyCollection<ulong> mapIds)
        {
            PostingChecks.RequireIds(mapIds, nameof(mapIds));

            string sql = $@"SELECT {QueueColumns}
                            FROM {_names.Bot}.notification_queue
                            WHERE source = @maps AND item_id IN @ids AND state = @dropped AND reason = @hidden";

            var rows = new List<QueueRow>();

            foreach (var chunk in Sorted(mapIds).Chunk(IdChunk))
            {
                rows.AddRange(await LoadDataEnum<QueueRow, object>(sql, new { maps = PostSource.Maps, ids = chunk, dropped = QueueState.Dropped, hidden = DeliveryReasons.Hidden })
                    .ConfigureAwait(false));
            }

            return [.. rows.OrderBy(row => row.item_id).ThenBy(row => row.guild_id).ThenBy(row => row.id)];
        }

        public async Task<int> CountOlderBotRunsAsync(string job, DateTime since)
        {
            PostingChecks.RequireJob(job, since);

            string sql = $@"SELECT COUNT(*)
                            FROM {_names.Bot}.job_runs
                            WHERE job = @job AND result = @ok AND started_at > @since
                            AND (detail IS NULL OR detail REGEXP @askedFor OR detail REGEXP @lostMaps)";

            return (int)await LoadFirstData<long, object>(sql, new { job, ok = JobResult.Ok, since, askedFor = AskedFor, lostMaps = LostMaps }).ConfigureAwait(false);
        }

        // Baselines and seen rows are written whole, so writing them again writes the same, and a seen row keeps when the bot
        // first saw its item. A queued item that's in the queue already stays as it is, whatever became of it, but for a map's
        // update dropped there because its server wasn't owed maps then, which waits again.
        public Task SaveDetectionAsync(Detection detection)
        {
            PostingChecks.Require(detection);

            if (detection.IsEmpty)
                return Task.CompletedTask;

            var bot = _names.Bot;
            var statements = new List<(string Sql, object Parameters)>();

            foreach (var batch in detection.Baselines.Chunk(BatchLimit))
            {
                var (values, parameters) = Rows(batch,
                    ("source", row => row.source),
                    ("taken_at", row => row.taken_at),
                    ("taken_by_run", row => row.taken_by_run),
                    ("reason", row => row.reason),
                    ("item_count", row => row.item_count),
                    ("baseline_time", row => row.baseline_time),
                    ("high_id", row => row.high_id),
                    ("alerted_at", row => row.alerted_at),
                    ("checked_at", row => row.checked_at),
                    ("checked_by_run", row => row.checked_by_run));

                statements.Add(($@"INSERT INTO {bot}.notification_baseline ({BaselineColumns})
                                   VALUES {values}
                                   ON DUPLICATE KEY UPDATE taken_at = VALUES(taken_at), taken_by_run = VALUES(taken_by_run), reason = VALUES(reason),
                                   item_count = VALUES(item_count), baseline_time = VALUES(baseline_time), high_id = VALUES(high_id), alerted_at = VALUES(alerted_at),
                                   checked_at = VALUES(checked_at), checked_by_run = VALUES(checked_by_run)", parameters));
            }

            foreach (var batch in detection.Seen.Chunk(BatchLimit))
            {
                var (values, parameters) = Rows(batch,
                    ("source", row => row.source),
                    ("item_id", row => row.item_id),
                    ("state", row => row.state),
                    ("first_seen_at", row => row.first_seen_at),
                    ("last_seen_at", row => row.last_seen_at),
                    ("updated_at", row => row.updated_at),
                    ("queued_at", row => row.queued_at));

                statements.Add(($@"INSERT INTO {bot}.notification_seen ({SeenColumns})
                                   VALUES {values}
                                   ON DUPLICATE KEY UPDATE state = VALUES(state), last_seen_at = VALUES(last_seen_at), updated_at = VALUES(updated_at),
                                   queued_at = VALUES(queued_at)", parameters));
            }

            foreach (var batch in detection.Queue.Chunk(BatchLimit))
            {
                var (values, parameters) = Rows(batch,
                    ("guild_id", item => item.guild_id),
                    ("source", item => item.source),
                    ("item_id", item => item.item_id),
                    ("revision", item => item.revision),
                    ("is_new", item => item.is_new),
                    ("queued_at", item => item.queued_at),
                    ("state", _ => QueueState.Pending));

                // Not INSERT IGNORE, which would also let a value that doesn't fit through, cut, without an error
                statements.Add(($@"INSERT INTO {bot}.notification_queue (guild_id, source, item_id, revision, is_new, queued_at, state)
                                   VALUES {values}
                                   ON DUPLICATE KEY UPDATE id = id", parameters));
            }

            // A map's update queued for a server whose row of it was dropped because the server wasn't owed maps then: a
            // detection queues each update once, so that row can only be what a post that wasn't made showed and gave back
            // (ClaimAsync), and the server, owed the update now, gets it, as one that never had such a row does. The batch's
            // maps, named on their own, keep the statement on their rows: for a list of one row, MariaDB's UPDATE finds no
            // index in it and reads every server's dropped rows, or the whole queue, holding each until the detection commits.
            foreach (var batch in detection.Queue.Where(item => item.source == PostSource.Maps && !item.is_new).Chunk(BatchLimit))
            {
                var (values, parameters) = Rows(batch,
                    ("guild_id", item => item.guild_id),
                    ("source", item => item.source),
                    ("item_id", item => item.item_id),
                    ("revision", item => item.revision));

                parameters.AddDynamicParams(new
                {
                    maps = PostSource.Maps,
                    items = batch.Select(item => item.item_id).Distinct().ToArray(),
                    pending = QueueState.Pending,
                    at = detection.At,
                    dropped = QueueState.Dropped,
                    off = ServerOff
                });

                statements.Add(($@"UPDATE {bot}.notification_queue
                                   SET state = @pending, queued_at = @at, delivery_id = NULL, reason = NULL, settled_at = NULL
                                   WHERE source = @maps AND item_id IN @items AND (guild_id, source, item_id, revision) IN ({values})
                                   AND state = @dropped AND reason IN @off", parameters));
            }

            // Only rows dropped because the armor wasn't approved any more: a server that got it never gets it again
            foreach (var chunk in detection.ReopenArmor.Chunk(IdChunk))
            {
                statements.Add(($@"UPDATE {bot}.notification_queue
                                   SET state = @pending, queued_at = @at, delivery_id = NULL, reason = NULL, settled_at = NULL
                                   WHERE source = @armor AND item_id IN @ids AND state = @dropped AND reason = @notApproved",
                    new { pending = QueueState.Pending, at = detection.At, armor = PostSource.Armor, ids = chunk, dropped = QueueState.Dropped, notApproved = DeliveryReasons.NotApproved }));
            }

            // Only the rows given that were dropped because Steam didn't show the map any more
            foreach (var chunk in detection.ReopenMaps.Chunk(IdChunk))
            {
                statements.Add(($@"UPDATE {bot}.notification_queue
                                   SET state = @pending, queued_at = @at, delivery_id = NULL, reason = NULL, settled_at = NULL
                                   WHERE id IN @ids AND source = @maps AND state = @dropped AND reason = @hidden",
                    new { pending = QueueState.Pending, at = detection.At, ids = chunk, maps = PostSource.Maps, dropped = QueueState.Dropped, hidden = DeliveryReasons.Hidden }));
            }

            return InTransaction(async transaction =>
            {
                foreach (var (sql, parameters) in statements)
                    await transaction.Execute(sql, parameters).ConfigureAwait(false);

                return true;
            });
        }

        public async Task<int> DropAsync(IReadOnlyCollection<ulong> queueIds, string reason, DateTime at)
        {
            PostingChecks.RequireDrop(queueIds, reason, at);

            if (queueIds.Count == 0)
                return 0;

            string sql = $@"UPDATE {_names.Bot}.notification_queue
                            SET state = @dropped, reason = @reason, settled_at = @at
                            WHERE id IN @ids AND state = @pending";

            var dropped = 0;

            foreach (var chunk in Sorted(queueIds).Chunk(IdChunk))
                dropped += await SaveDataCount(sql, new { dropped = QueueState.Dropped, reason, at, ids = chunk, pending = QueueState.Pending }).ConfigureAwait(false);

            return dropped;
        }

        public Task<int> ClearFailingAsync(ulong guildId, string source, DateTime at)
        {
            PostingChecks.RequirePosted(source);
            RequireUtc(at, nameof(at));

            string sql = $@"UPDATE {_names.Bot}.guild_delivery_status
                            SET failing_since = NULL, failures = 0, next_try_at = NULL, waiting_on = NULL, updated_at = @at
                            WHERE guild_id = @guildId AND source = @source
                            AND (failing_since IS NOT NULL OR failures <> 0 OR next_try_at IS NOT NULL OR waiting_on IS NOT NULL)";

            return SaveDataCount(sql, new { guildId, source, at });
        }

        public Task<ulong> ClaimAsync(DeliveryClaim claim)
        {
            PostingChecks.Require(claim);

            var bot = _names.Bot;

            // The items the message carries, which the pages show: a map's rows for two of its updates, as a message that
            // showed an update and wasn't made gives back, are one map
            string items = $@"SELECT COUNT(DISTINCT item_id)
                              FROM {bot}.notification_queue
                              WHERE id IN @ids";

            string insert = $@"INSERT INTO {bot}.notification_deliveries (attempt_key, run_id, run_started_at, guild_id, channel_id, source, part, item_count,
                               attempted_at, state, checks)
                               VALUES (@AttemptKey, @RunId, @RunStartedAt, @GuildId, @ChannelId, @Source, @Part, @count, @AttemptedAt, @sending, 0)";

            // The connection's own last insert, which the transaction's other statements leave alone
            const string NewId = "SELECT LAST_INSERT_ID()";

            string take = $@"UPDATE {bot}.notification_queue
                             SET state = @sending, delivery_id = @id
                             WHERE id IN @ids AND state = @pending AND guild_id = @guildId AND source = @source";

            return InTransaction(async transaction =>
            {
                var count = await transaction.QueryFirst<long>(items, new { ids = claim.QueueIds }).ConfigureAwait(false);

                try
                {
                    await transaction.Execute(insert, new
                    {
                        claim.AttemptKey,
                        claim.RunId,
                        claim.RunStartedAt,
                        claim.GuildId,
                        claim.ChannelId,
                        claim.Source,
                        claim.Part,
                        count,
                        claim.AttemptedAt,
                        sending = DeliveryState.Sending
                    }).ConfigureAwait(false);
                }
                catch (MySqlException e) when (e.Number == (int)MySqlErrorCode.DuplicateKeyEntry)
                {
                    throw new ClaimLostException("This attempt was claimed before, so it can't claim again");
                }

                var id = await transaction.QueryFirst<ulong>(NewId, null).ConfigureAwait(false);
                var taken = await transaction.Execute(take, new { sending = QueueState.Sending, id, ids = claim.QueueIds, pending = QueueState.Pending, guildId = claim.GuildId, source = claim.Source })
                    .ConfigureAwait(false);

                // Throwing rolls the delivery row and every row it took back
                if (taken != claim.QueueIds.Count)
                    throw new ClaimLostException(string.Create(CultureInfo.InvariantCulture,
                        $"Only {taken} of the {claim.QueueIds.Count} rows were still pending for this server and kind, so the claim took none"));

                // The updates the message shows that none of its rows is for, held by it as its rows are, by the queue's unique
                // key, so one that's in the queue for the server already stays as it is
                foreach (var batch in claim.Shows.Chunk(BatchLimit))
                {
                    var (values, parameters) = Rows(batch,
                        ("guild_id", _ => claim.GuildId),
                        ("source", _ => PostSource.Maps),
                        ("item_id", update => update.MapId),
                        ("revision", update => update.Revision),
                        ("is_new", _ => false),
                        ("queued_at", _ => claim.AttemptedAt),
                        ("state", _ => QueueState.Sending),
                        ("delivery_id", _ => id));

                    await transaction.Execute($@"INSERT INTO {bot}.notification_queue (guild_id, source, item_id, revision, is_new, queued_at, state, delivery_id)
                                                VALUES {values}
                                                ON DUPLICATE KEY UPDATE id = id", parameters).ConfigureAwait(false);
                }

                return id;
            });
        }

        // The message's row is read under its lock, and only one that's still open is settled, so a settle that's written again
        // changes nothing, and its status counts a failure once
        public Task<bool> SettleAsync(Settlement settlement)
        {
            PostingChecks.Require(settlement);

            var bot = _names.Bot;
            string lockRow = LockDelivery(bot);
            var outcome = settlement.Outcome;

            string settle = outcome switch
            {
                // A message that went has no reason, even one Discord didn't answer about at first
                DeliveryState.Sent => $@"UPDATE {bot}.notification_deliveries
                                         SET state = @outcome, message_id = @MessageId, settled_at = @At, reason = NULL, missing = NULL
                                         WHERE id = @id",
                DeliveryState.InDoubt => $@"UPDATE {bot}.notification_deliveries
                                            SET state = @outcome, reason = @reason, http_status = @HttpStatus, discord_code = @DiscordCode
                                            WHERE id = @id",
                // A message in doubt keeps Discord's numbers from the answer that left it so, unless this one has its own
                _ => $@"UPDATE {bot}.notification_deliveries
                        SET state = @outcome, settled_at = @At, reason = @reason, missing = @Missing, http_status = COALESCE(@HttpStatus, http_status),
                        discord_code = COALESCE(@DiscordCode, discord_code)
                        WHERE id = @id"
            };

            // A message that went, or that may have, carried its rows, and one that wasn't made gives them back to pending. The
            // rows of one that may not have arrived keep that as their reason, since armor's outlive the record of the message,
            // which goes after 90 days, and where an armor went is read from them then.
            string? rows = outcome switch
            {
                DeliveryState.Sent => $@"UPDATE {bot}.notification_queue
                                         SET state = @sent, settled_at = @At
                                         WHERE delivery_id = @id AND state = @sending",
                DeliveryState.Unconfirmed => $@"UPDATE {bot}.notification_queue
                                                SET state = @sent, settled_at = @At, reason = @unconfirmed
                                                WHERE delivery_id = @id AND state = @sending",
                DeliveryState.Failed => $@"UPDATE {bot}.notification_queue
                                           SET state = @pending, delivery_id = NULL
                                           WHERE delivery_id = @id AND state = @sending",
                _ => null
            };

            var reason = outcome == DeliveryState.Unconfirmed ? DeliveryReasons.Unconfirmed : settlement.Reason;

            return InTransaction(async transaction =>
            {
                var delivery = await transaction.QueryFirst<OpenDelivery>(lockRow, new { settlement.AttemptKey }).ConfigureAwait(false);

                if (delivery == null || !DeliveryState.IsOpen(delivery.state) || (outcome == DeliveryState.InDoubt && delivery.state != DeliveryState.Sending))
                    return false;

                await transaction.Execute(settle, new
                {
                    outcome,
                    settlement.MessageId,
                    settlement.At,
                    reason,
                    settlement.Missing,
                    settlement.HttpStatus,
                    settlement.DiscordCode,
                    delivery.id
                }).ConfigureAwait(false);

                if (rows != null)
                {
                    await transaction.Execute(rows, new { sent = QueueState.Sent, settlement.At, unconfirmed = DeliveryReasons.Unconfirmed, delivery.id, sending = QueueState.Sending,
                        pending = QueueState.Pending })
                        .ConfigureAwait(false);
                }

                if (outcome == DeliveryState.Sent)
                    await transaction.Execute(StatusSent(bot), new { delivery.guild_id, delivery.source, sent_at = delivery.attempted_at, delivery.channel_id, settlement.MessageId, settlement.At })
                        .ConfigureAwait(false);
                else if (settlement.Counts)
                    await transaction.Execute(StatusFailed(bot), new
                    {
                        delivery.guild_id,
                        delivery.source,
                        failed_at = delivery.attempted_at,
                        delivery.channel_id,
                        settlement.Reason,
                        settlement.Missing,
                        settlement.HttpStatus,
                        settlement.DiscordCode,
                        settlement.NextTryAt,
                        settlement.WaitingOn,
                        settlement.At
                    }).ConfigureAwait(false);

                return true;
            });
        }

        public Task<bool> RecordRefusalAsync(Refusal refusal)
        {
            PostingChecks.Require(refusal);

            var bot = _names.Bot;

            string insert = $@"INSERT INTO {bot}.notification_deliveries (attempt_key, run_id, run_started_at, guild_id, channel_id, source, part, item_count,
                               attempted_at, state, settled_at, reason, missing, checks)
                               VALUES (@AttemptKey, @RunId, @RunStartedAt, @GuildId, @ChannelId, @Source, 0, @ItemCount, @At, @failed, @At, @Reason, @Missing, 0)";

            return InTransaction(async transaction =>
            {
                try
                {
                    await transaction.Execute(insert, new
                    {
                        refusal.AttemptKey,
                        refusal.RunId,
                        refusal.RunStartedAt,
                        refusal.GuildId,
                        refusal.ChannelId,
                        refusal.Source,
                        refusal.ItemCount,
                        refusal.At,
                        failed = DeliveryState.Failed,
                        refusal.Reason,
                        refusal.Missing
                    }).ConfigureAwait(false);
                }
                catch (MySqlException e) when (e.Number == (int)MySqlErrorCode.DuplicateKeyEntry)
                {
                    return false;
                }

                await transaction.Execute(StatusFailed(bot), new
                {
                    guild_id = refusal.GuildId,
                    source = refusal.Source,
                    failed_at = refusal.At,
                    channel_id = refusal.ChannelId,
                    refusal.Reason,
                    refusal.Missing,
                    HttpStatus = (ushort?)null,
                    DiscordCode = (uint?)null,
                    refusal.NextTryAt,
                    refusal.WaitingOn,
                    refusal.At
                }).ConfigureAwait(false);

                return true;
            });
        }

        public async Task<bool> MarkCheckedAsync(ulong attemptKey, byte checks)
        {
            if (attemptKey == 0 || checks == 0)
                throw new ArgumentException("A look is of an attempt, and counts from 1", nameof(checks));

            string sql = $@"UPDATE {_names.Bot}.notification_deliveries
                            SET state = @inDoubt, checks = @checks
                            WHERE attempt_key = @attemptKey AND state IN @open AND checks < @checks";

            return await SaveDataCount(sql, new { inDoubt = DeliveryState.InDoubt, checks, attemptKey, open = OpenStates }).ConfigureAwait(false) == 1;
        }

        public Task<int> EndWaitsAsync(ulong guildId, ulong? channelId, DateTime at)
        {
            RequireUtc(at, nameof(at));

            if (channelId == 0)
                throw new ArgumentException("A channel's ID isn't 0", nameof(channelId));

            string sql = $@"UPDATE {_names.Bot}.guild_delivery_status
                            SET next_try_at = NULL, waiting_on = NULL, updated_at = @at
                            WHERE guild_id = @guildId AND (next_try_at IS NOT NULL OR waiting_on IS NOT NULL){(channelId != null ? " AND last_failed_channel_id = @channelId" : "")}";

            return SaveDataCount(sql, new { guildId, channelId, at });
        }

        public Task<GuildSwitches?> GetSwitchesAsync(ulong guildId)
        {
            string sql = $@"SELECT {SwitchColumns}
                            FROM {_names.Bot}.guild_settings
                            WHERE guild_id = @guildId";

            return LoadFirstData<GuildSwitches?, object>(sql, new { guildId });
        }

        // By the index on the server and the time tried
        public async Task<IReadOnlyList<ulong>> ListMessageIdsAsync(ulong guildId, ulong channelId, DateTime since)
        {
            RequireUtc(since, nameof(since));

            string sql = $@"SELECT message_id
                            FROM {_names.Bot}.notification_deliveries
                            WHERE guild_id = @guildId AND attempted_at >= @since AND channel_id = @channelId AND message_id IS NOT NULL
                            ORDER BY attempted_at, id
                            LIMIT {MessageLimit}";

            return (await LoadDataEnum<ulong, object>(sql, new { guildId, since, channelId }).ConfigureAwait(false)).ToList();
        }

        // By the index on the message that holds the rows
        public async Task<IReadOnlyList<ulong>> ListCarriedAsync(ulong deliveryId)
        {
            string sql = $@"SELECT item_id
                            FROM {_names.Bot}.notification_queue
                            WHERE delivery_id = @deliveryId AND state = @sending
                            ORDER BY item_id
                            LIMIT {ClaimLimit}";

            return (await LoadDataEnum<ulong, object>(sql, new { deliveryId, sending = QueueState.Sending }).ConfigureAwait(false)).ToList();
        }

        static string OpenSql(string bot) => $@"SELECT {QueueColumns}
                                                FROM {bot}.notification_queue
                                                WHERE state = @pending
                                                ORDER BY queued_at, id
                                                LIMIT {OpenLimit}";

        static string SeenSql(string bot) => $@"SELECT {SeenColumns}
                                                FROM {bot}.notification_seen
                                                WHERE source = @source AND item_id IN @ids";

        // Locked until the transaction ends, so what the settle decides from can't change under it
        static string LockDelivery(string bot) => $@"SELECT id, guild_id, channel_id, source, attempted_at, state
                                                     FROM {bot}.notification_deliveries
                                                     WHERE attempt_key = @AttemptKey
                                                     FOR UPDATE";

        // A message that went: the last post, and posts go again, with no wait
        static string StatusSent(string bot) => $@"INSERT INTO {bot}.guild_delivery_status (guild_id, source, last_sent_at, last_sent_channel_id, last_message_id, failures,
                                                   updated_at)
                                                   VALUES (@guild_id, @source, @sent_at, @channel_id, @MessageId, 0, @At)
                                                   ON DUPLICATE KEY UPDATE last_sent_at = VALUES(last_sent_at), last_sent_channel_id = VALUES(last_sent_channel_id),
                                                   last_message_id = VALUES(last_message_id), failing_since = NULL, failures = 0, next_try_at = NULL, waiting_on = NULL,
                                                   updated_at = VALUES(updated_at)";

        // A failure of the server's own: the last failure, one more try in a row that failed, failing since the first of them,
        // and the wait the caller worked out. Only ever run once for a message or a refusal, which its own row decides.
        static string StatusFailed(string bot) => $@"INSERT INTO {bot}.guild_delivery_status (guild_id, source, last_failed_at, last_failed_channel_id, last_reason,
                                                     last_missing, last_http_status, last_discord_code, failing_since, failures, next_try_at, waiting_on, updated_at)
                                                     VALUES (@guild_id, @source, @failed_at, @channel_id, @Reason, @Missing, @HttpStatus, @DiscordCode, @failed_at, 1, @NextTryAt,
                                                     @WaitingOn, @At)
                                                     ON DUPLICATE KEY UPDATE last_failed_at = VALUES(last_failed_at), last_failed_channel_id = VALUES(last_failed_channel_id),
                                                     last_reason = VALUES(last_reason), last_missing = VALUES(last_missing), last_http_status = VALUES(last_http_status),
                                                     last_discord_code = VALUES(last_discord_code), failing_since = COALESCE(failing_since, VALUES(failing_since)),
                                                     failures = failures + 1, next_try_at = VALUES(next_try_at), waiting_on = VALUES(waiting_on), updated_at = VALUES(updated_at)";

        static IEnumerable<ulong> Sorted(IReadOnlyCollection<ulong> ids) => ids.Distinct().Order();

        // The message's row as the locked read finds it
        internal sealed class OpenDelivery
        {
            public ulong id { get; set; }
            public ulong guild_id { get; set; }
            public ulong channel_id { get; set; }
            public string source { get; set; } = "";
            public DateTime attempted_at { get; set; }
            public string state { get; set; } = "";
        }
    }
}
