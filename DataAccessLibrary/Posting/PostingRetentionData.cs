using DataAccessLibrary.Status;
using System.Globalization;

namespace DataAccessLibrary.Posting
{
    // The rules of the bot's nightly clean-up of its posts' records, apart from HousekeepingJob's: the daily totals, then the
    // log and the settled queue of maps and lobbies after 90 days, the lobbies the bot hasn't seen for a week, and the lobby
    // samples. Armor's queue is kept, as its seen rows are: it's where each armor went, which Back to pending and an armor
    // approved again go by however long ago it was posted, and it's only a row for each armor and server. Each delete
    // takes at most the rows it's given, oldest first, so no statement holds its locks for long, and the caller runs it again
    // until it comes back short. Rows still open are never deleted: a message not settled yet, and an item that still waits.
    // The times are cutoffs the caller works out from its own clock, never the server's.
    public class PostingRetentionData : DbBaseConnection, IPostingRetentionData
    {
        public const int ChunkLimit = RetentionData.ChunkLimit;

        static readonly string[] Settled = [DeliveryState.Sent, DeliveryState.Failed, DeliveryState.Unconfirmed];

        private readonly DatabaseNames _names;

        public PostingRetentionData(ISQLConnectionFactory connectionFactory, DatabaseNames names) : base(connectionFactory, DBConnectionName.Bot)
        {
            _names = names;
        }

        // MariaDB's own limits for one statement, as RetentionData's
        string Limits => string.Create(CultureInfo.InvariantCulture,
            $"SET STATEMENT max_statement_time = {TransactionStatementSeconds}, lock_wait_timeout = {TransactionStatementSeconds}, innodb_lock_wait_timeout = {TransactionStatementSeconds} FOR ");

        // Each UTC day from the first given up to the second, worked out again from all of the log and the queue that hold it:
        // the messages that went, failed or may not have arrived, by the day they were tried, with the items they carried, a
        // map once however many of its updates' rows a message took, and the items dropped, a row each, by the day they were,
        // each by source, outcome and reason. A day's totals are replaced whole, in one transaction, so a message settled late
        // counts, and an item dropped and then queued again, as an armor approved again is, stops counting. The caller gives
        // the first day it keeps all the rows of. How many days have totals.
        public Task<int> TotalDeliveryDays(DateTime from, DateTime to)
        {
            RequireDay(from, nameof(from));
            RequireDay(to, nameof(to));

            if (to < from)
                throw new ArgumentException("The days to total end before they start", nameof(to));

            var bot = _names.Bot;

            string clear = $@"DELETE FROM {bot}.delivery_daily
                              WHERE day >= @from AND day < @to";

            string messages = $@"INSERT INTO {bot}.delivery_daily (day, source, outcome, reason, messages, items)
                                 SELECT DATE(attempted_at), source, state, COALESCE(reason, ''), COUNT(*), SUM(item_count)
                                 FROM {bot}.notification_deliveries
                                 WHERE attempted_at >= @from AND attempted_at < @to AND state IN @settled
                                 GROUP BY DATE(attempted_at), source, state, COALESCE(reason, '')";

            string drops = $@"INSERT INTO {bot}.delivery_daily (day, source, outcome, reason, messages, items)
                              SELECT DATE(settled_at), source, state, COALESCE(reason, ''), 0, COUNT(*)
                              FROM {bot}.notification_queue
                              WHERE settled_at >= @from AND settled_at < @to AND state = @dropped
                              GROUP BY DATE(settled_at), source, state, COALESCE(reason, '')";

            string count = $@"SELECT COUNT(DISTINCT day)
                              FROM {bot}.delivery_daily
                              WHERE day >= @from AND day < @to";

            var parameters = new { from, to, settled = Settled, dropped = QueueState.Dropped };

            return InTransaction(async transaction =>
            {
                await transaction.Execute(clear, parameters).ConfigureAwait(false);
                await transaction.Execute(messages, parameters).ConfigureAwait(false);
                await transaction.Execute(drops, parameters).ConfigureAwait(false);

                return (int)await transaction.QueryFirst<long>(count, parameters).ConfigureAwait(false);
            });
        }

        // Messages and refusals tried before the cutoff, once settled
        public Task<int> DeleteDeliveries(DateTime before, int take)
        {
            RequireChunk(before, take);

            string sql = $@"{Limits}DELETE FROM {_names.Bot}.notification_deliveries
                            WHERE attempted_at < @before AND state IN @settled
                            ORDER BY attempted_at, id
                            LIMIT @take";

            return SaveDataCount(sql, new { before, settled = Settled, take });
        }

        // Maps and lobbies settled before the cutoff, sent or dropped. One that waits has no settled_at, so it never goes.
        // Armor's rows stay: without them an armor posted long ago reads as never posted, so Back to pending is offered and
        // allowed, and one withdrawn before its post has nothing to reopen when it's approved again.
        public Task<int> DeleteSettledQueueRows(DateTime before, int take)
        {
            RequireChunk(before, take);

            string sql = $@"{Limits}DELETE FROM {_names.Bot}.notification_queue
                            WHERE settled_at < @before AND source <> @armor
                            ORDER BY settled_at, id
                            LIMIT @take";

            return SaveDataCount(sql, new { before, armor = PostSource.Armor, take });
        }

        // Lobbies last seen before the cutoff, which closed long since. Armor, maps and submissions are kept: they're the record
        // of what the bot has posted.
        public Task<int> DeleteSeenLobbies(DateTime before, int take)
        {
            RequireChunk(before, take);

            string sql = $@"{Limits}DELETE FROM {_names.Bot}.notification_seen
                            WHERE source = @lobbies AND last_seen_at < @before
                            ORDER BY last_seen_at, item_id
                            LIMIT @take";

            return SaveDataCount(sql, new { lobbies = PostSource.Lobbies, before, take });
        }

        public Task<int> DeleteLobbySamples(DateTime before, int take)
        {
            RequireChunk(before, take);

            string sql = $@"{Limits}DELETE FROM {_names.Bot}.lobby_samples
                            WHERE minute < @before
                            ORDER BY minute
                            LIMIT @take";

            return SaveDataCount(sql, new { before, take });
        }

        static void RequireChunk(DateTime before, int take)
        {
            RequireUtc(before, nameof(before));

            if (take is < 1 or > ChunkLimit)
                throw new ArgumentOutOfRangeException(nameof(take), take, $"A chunk takes from 1 to {ChunkLimit} rows");
        }

        static void RequireDay(DateTime day, string name)
        {
            RequireUtc(day, name);

            if (day.TimeOfDay != TimeSpan.Zero)
                throw new ArgumentException("This is a day's start, 00:00 UTC", name);
        }
    }
}
