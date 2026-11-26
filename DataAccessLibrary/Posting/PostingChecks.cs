using DataAccessLibrary.Posting.Models;
using MinerBot.Shared.Delivery;

namespace DataAccessLibrary.Posting
{
    // What both posting stores refuse before they change anything, so a write that can't be right fails the same way in
    // memory as in MariaDB, and in MariaDB before any SQL
    internal static class PostingChecks
    {
        public static void Require(PostingLoad load)
        {
            ArgumentNullException.ThrowIfNull(load);
            RequireIds(load.ArmorIds, nameof(load.ArmorIds));
            RequireIds(load.LobbyIds, nameof(load.LobbyIds));
        }

        public static void RequireIds(IReadOnlyCollection<ulong> ids, string name)
        {
            ArgumentNullException.ThrowIfNull(ids, name);

            if (ids.Count > NotificationData.IdLimit)
                throw new ArgumentException($"One read looks up at most {NotificationData.IdLimit} items of a source", name);
        }

        public static void RequireSource(string? source)
        {
            if (!PostSource.IsKnown(source))
                throw new ArgumentException($"A source is one of: {string.Join(", ", PostSource.All)}", nameof(source));
        }

        public static void RequirePosted(string? source)
        {
            if (!PostSource.IsPosted(source))
                throw new ArgumentException($"Only these kinds are posted: {string.Join(", ", PostSource.Posted)}", nameof(source));
        }

        public static void Require(Detection detection)
        {
            ArgumentNullException.ThrowIfNull(detection);
            DbBaseConnection.RequireUtc(detection.At, nameof(detection.At));

            if (detection.Baselines.Count + detection.Seen.Count + detection.Queue.Count + detection.ReopenArmor.Count + detection.ReopenMaps.Count > NotificationData.DetectionLimit)
                throw new ArgumentException($"One detection writes at most {NotificationData.DetectionLimit} rows", nameof(detection));

            foreach (var row in detection.Baselines)
            {
                ArgumentNullException.ThrowIfNull(row, nameof(detection));
                RequireSource(row.source);

                if (!BaselineReason.IsKnown(row.reason))
                    throw new ArgumentException($"A baseline's reason is one of: {string.Join(", ", BaselineReason.All)}", nameof(detection));

                if (row.baseline_time != null && row.source != PostSource.Maps)
                    throw new ArgumentException("Only the maps' baseline has a time", nameof(detection));

                if ((row.high_id != null || row.alerted_at != null) && row.source != PostSource.Submissions)
                    throw new ArgumentException("Only the submissions' baseline has a floor and a last try", nameof(detection));

                DbBaseConnection.RequireUtc(row.taken_at, nameof(row.taken_at));
                DbBaseConnection.RequireUtc(row.checked_at, nameof(row.checked_at));
                DbBaseConnection.RequireUtcOrNull(row.baseline_time, nameof(row.baseline_time));
                DbBaseConnection.RequireUtcOrNull(row.alerted_at, nameof(row.alerted_at));
            }

            if (detection.Baselines.Select(row => row.source).Distinct().Count() != detection.Baselines.Count)
                throw new ArgumentException("A detection writes each source's baseline once", nameof(detection));

            foreach (var row in detection.Seen)
            {
                ArgumentNullException.ThrowIfNull(row, nameof(detection));
                RequireSource(row.source);

                if (!SeenState.IsKnown(row.state))
                    throw new ArgumentException($"An item's state is one of: {string.Join(", ", SeenState.All)}", nameof(detection));

                // Only a submission is told about, and a submission is only ever a baseline or told about
                if ((row.source == PostSource.Submissions) != (row.state is SeenState.Alerted or SeenState.AlertFailed) && row.state != SeenState.Baseline)
                    throw new ArgumentException("Only a submission is alerted, and a submission is only taken as a baseline or alerted", nameof(detection));

                if (row.item_id == 0)
                    throw new ArgumentException("An item's ID isn't 0", nameof(detection));

                DbBaseConnection.RequireUtc(row.first_seen_at, nameof(row.first_seen_at));
                DbBaseConnection.RequireUtc(row.last_seen_at, nameof(row.last_seen_at));
                DbBaseConnection.RequireUtcOrNull(row.updated_at, nameof(row.updated_at));
                DbBaseConnection.RequireUtcOrNull(row.queued_at, nameof(row.queued_at));
            }

            if (detection.Seen.Select(row => (row.source, row.item_id)).Distinct().Count() != detection.Seen.Count)
                throw new ArgumentException("A detection writes each item's seen row once", nameof(detection));

            foreach (var item in detection.Queue)
            {
                ArgumentNullException.ThrowIfNull(item, nameof(detection));
                RequirePosted(item.source);

                if (item.guild_id == 0 || item.item_id == 0)
                    throw new ArgumentException("A queued item's server and ID aren't 0", nameof(detection));

                if (item.revision != 0 && item.source != PostSource.Maps)
                    throw new ArgumentException("Only a map's queued item has a revision", nameof(detection));

                DbBaseConnection.RequireUtc(item.queued_at, nameof(item.queued_at));
            }

            if (detection.Queue.Select(item => (item.guild_id, item.source, item.item_id, item.revision)).Distinct().Count() != detection.Queue.Count)
                throw new ArgumentException("A detection queues each item for a server once", nameof(detection));

            if (detection.ReopenArmor.Contains(0UL) || detection.ReopenArmor.Distinct().Count() != detection.ReopenArmor.Count)
                throw new ArgumentException("The armor approved again are distinct armor IDs", nameof(detection));

            if (detection.ReopenMaps.Contains(0UL) || detection.ReopenMaps.Distinct().Count() != detection.ReopenMaps.Count)
                throw new ArgumentException("The rows of the maps shown again are distinct queue rows", nameof(detection));
        }

        public static void RequireDrop(IReadOnlyCollection<ulong> queueIds, string reason, DateTime at)
        {
            ArgumentNullException.ThrowIfNull(queueIds);

            if (!DeliveryReasons.IsDrop(reason))
                throw new ArgumentException($"A drop's reason is one of: {string.Join(", ", DeliveryReasons.Drops)}", nameof(reason));

            if (queueIds.Count > NotificationData.DropLimit || queueIds.Distinct().Count() != queueIds.Count)
                throw new ArgumentException($"One drop takes at most {NotificationData.DropLimit} distinct rows", nameof(queueIds));

            DbBaseConnection.RequireUtc(at, nameof(at));
        }

        public static void Require(DeliveryClaim claim)
        {
            ArgumentNullException.ThrowIfNull(claim);
            RequirePosted(claim.Source);
            DbBaseConnection.RequireUtc(claim.RunStartedAt, nameof(claim.RunStartedAt));
            DbBaseConnection.RequireUtc(claim.AttemptedAt, nameof(claim.AttemptedAt));

            if (claim.AttemptKey == 0 || claim.GuildId == 0 || claim.ChannelId == 0)
                throw new ArgumentException("A claim's attempt, server and channel aren't 0", nameof(claim));

            if (claim.Part == 0)
                throw new ArgumentException("A message's place in its post counts from 1", nameof(claim));

            if (claim.QueueIds == null || claim.QueueIds.Count is 0 or > NotificationData.ClaimLimit || claim.QueueIds.Distinct().Count() != claim.QueueIds.Count)
                throw new ArgumentException($"A message carries 1 to {NotificationData.ClaimLimit} distinct rows", nameof(claim));

            if (claim.Shows == null || claim.Shows.Count > NotificationData.ClaimLimit || claim.Shows.Any(update => update == null || update.MapId == 0 || update.Revision == 0)
                || claim.Shows.Select(update => update.MapId).Distinct().Count() != claim.Shows.Count)
                throw new ArgumentException($"A message shows at most {NotificationData.ClaimLimit} maps' updates, one for each map, none of them 0", nameof(claim));

            if (claim.Shows.Count > 0 && claim.Source != PostSource.Maps)
                throw new ArgumentException("Only a map post shows a map's update", nameof(claim));
        }

        public static void Require(Settlement settlement)
        {
            ArgumentNullException.ThrowIfNull(settlement);
            DbBaseConnection.RequireUtc(settlement.At, nameof(settlement.At));
            DbBaseConnection.RequireUtcOrNull(settlement.NextTryAt, nameof(settlement.NextTryAt));

            if (settlement.AttemptKey == 0)
                throw new ArgumentException("A settle's attempt isn't 0", nameof(settlement));

            var sent = settlement.Outcome == DeliveryState.Sent;

            switch (settlement.Outcome)
            {
                case DeliveryState.Sent:
                    if (settlement.MessageId is null or 0 || settlement.Reason != null)
                        throw new ArgumentException("A message that went has Discord's ID and no reason", nameof(settlement));
                    break;

                case DeliveryState.Failed:
                    if (!DeliveryReasons.IsFailure(settlement.Reason))
                        throw new ArgumentException($"A message that failed has one of these reasons: {string.Join(", ", DeliveryReasons.Failures)}", nameof(settlement));
                    break;

                case DeliveryState.InDoubt:
                    if (settlement.Reason is not (null or DeliveryReasons.NoAnswer))
                        throw new ArgumentException($"A message in doubt has no reason, or {DeliveryReasons.NoAnswer}", nameof(settlement));
                    break;

                case DeliveryState.Unconfirmed:
                    if (settlement.Reason is not (null or DeliveryReasons.Unconfirmed))
                        throw new ArgumentException($"An unconfirmed message has no reason, or {DeliveryReasons.Unconfirmed}", nameof(settlement));
                    break;

                default:
                    throw new ArgumentException("A message settles as sent, failed, in_doubt or unconfirmed", nameof(settlement));
            }

            if (!sent && settlement.MessageId != null)
                throw new ArgumentException("Only a message that went has Discord's ID", nameof(settlement));

            if (settlement.Counts && settlement.Outcome != DeliveryState.Failed)
                throw new ArgumentException("Only a message that failed counts against its server", nameof(settlement));

            if (!settlement.Counts && (settlement.NextTryAt != null || settlement.WaitingOn != null))
                throw new ArgumentException("Only a failure that counts sets a wait", nameof(settlement));

            RequireMask(settlement.Reason, settlement.Missing);
        }

        public static void Require(Refusal refusal)
        {
            ArgumentNullException.ThrowIfNull(refusal);
            RequirePosted(refusal.Source);
            DbBaseConnection.RequireUtc(refusal.RunStartedAt, nameof(refusal.RunStartedAt));
            DbBaseConnection.RequireUtc(refusal.At, nameof(refusal.At));
            DbBaseConnection.RequireUtcOrNull(refusal.NextTryAt, nameof(refusal.NextTryAt));

            if (refusal.AttemptKey == 0 || refusal.GuildId == 0 || refusal.ChannelId == 0)
                throw new ArgumentException("A refusal's attempt, server and channel aren't 0", nameof(refusal));

            if (refusal.ItemCount == 0)
                throw new ArgumentException("A refusal is of at least one item that waits", nameof(refusal));

            if (!DeliveryReasons.IsFailure(refusal.Reason))
                throw new ArgumentException($"A refusal has one of these reasons: {string.Join(", ", DeliveryReasons.Failures)}", nameof(refusal));

            RequireMask(refusal.Reason, refusal.Missing);
        }

        // The mask of missing permissions goes with that reason alone, and names at least one of the four
        static void RequireMask(string? reason, byte? missing)
        {
            if (missing != null && (reason != DeliveryReasons.MissingPermissions || missing is 0 or > 15))
                throw new ArgumentException("Only missing permissions have a mask, of 1 to 15", nameof(missing));
        }

        public static void RequireJob(string job, DateTime since)
        {
            if (string.IsNullOrEmpty(job) || job.Length > 100)
                throw new ArgumentException("A job's name has 1 to 100 characters", nameof(job));

            DbBaseConnection.RequireUtc(since, nameof(since));
        }
    }
}
