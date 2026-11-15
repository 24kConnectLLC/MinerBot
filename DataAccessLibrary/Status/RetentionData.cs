using System.Globalization;

namespace DataAccessLibrary.Status
{
    // The rules of the bot's daily clean-up, which keeps the bot's history and the dashboard's records only as long as the
    // owner chose. One method for each rule, with its table and column in its own SQL. Each delete or change takes at most
    // the rows it's given, oldest first, so no statement holds its locks for long, and the caller runs it again until it
    // comes back short. The times are cutoffs the caller works out from its own clock, never the server's.
    public class RetentionData : DbBaseConnection, IRetentionData
    {
        // The most rows one statement takes
        public const int ChunkLimit = 5000;

        private readonly DatabaseNames _names;

        public RetentionData(ISQLConnectionFactory connectionFactory, DatabaseNames names) : base(connectionFactory, DBConnectionName.Bot)
        {
            _names = names;
        }

        // MariaDB's own limits for one statement, so the server ends one that runs or waits for a lock too long: MySql.Data
        // doesn't stop a statement it gave up on, and the bot's login has no limit of its own
        string Limits => string.Create(CultureInfo.InvariantCulture,
            $"SET STATEMENT max_statement_time = {TransactionStatementSeconds}, lock_wait_timeout = {TransactionStatementSeconds}, innodb_lock_wait_timeout = {TransactionStatementSeconds} FOR ");

        // Audit rows from before the cutoff
        public Task<int> DeleteAuditRows(DateTime before, int take)
        {
            RequireChunk(before, take);

            string sql = $@"{Limits}DELETE FROM {_names.Bot}.audit_log
                            WHERE at < @before
                            ORDER BY at, id
                            LIMIT @take";

            return SaveDataCount(sql, new { before, take });
        }

        // The visitor's address on audit rows from before the cutoff. Only rows that still have one, because MySql.Data
        // counts the rows matched, and a chunk of rows already cleared would never come back short.
        public Task<int> ClearAuditAddresses(DateTime before, int take)
        {
            RequireChunk(before, take);

            string sql = $@"{Limits}UPDATE {_names.Bot}.audit_log
                            SET ip = NULL
                            WHERE ip IS NOT NULL AND at < @before
                            ORDER BY at, id
                            LIMIT @take";

            return SaveDataCount(sql, new { before, take });
        }

        public Task<int> DeleteEvents(DateTime before, int take)
        {
            RequireChunk(before, take);

            string sql = $@"{Limits}DELETE FROM {_names.Bot}.bot_events
                            WHERE at < @before
                            ORDER BY at, id
                            LIMIT @take";

            return SaveDataCount(sql, new { before, take });
        }

        public Task<int> DeleteJobRuns(DateTime before, int take)
        {
            RequireChunk(before, take);

            string sql = $@"{Limits}DELETE FROM {_names.Bot}.job_runs
                            WHERE started_at < @before
                            ORDER BY started_at, id
                            LIMIT @take";

            return SaveDataCount(sql, new { before, take });
        }

        // Each UTC day from the first given up to the second that has samples, totalled into uptime_daily, and how many days
        // that was. The caller gives the first day it keeps the samples of, so a day is only ever totalled while all of its
        // samples are there, and a total written again counts the samples that arrived late.
        public async Task<int> TotalUptimeDays(DateTime from, DateTime to)
        {
            RequireDay(from, nameof(from));
            RequireDay(to, nameof(to));

            if (to < from)
                throw new ArgumentException("The days to total end before they start", nameof(to));

            // The count is apart, because MySql.Data counts an upsert's rows as 1 for each new row and 2 for each changed one
            string total = $@"{Limits}INSERT INTO {_names.Bot}.uptime_daily (day, sampled_minutes, discord_minutes, steam_minutes)
                              SELECT DATE(minute), COUNT(*), SUM(discord_up), SUM(steam_up)
                              FROM {_names.Bot}.uptime_samples
                              WHERE minute >= @from AND minute < @to
                              GROUP BY DATE(minute)
                              ON DUPLICATE KEY UPDATE sampled_minutes = VALUES(sampled_minutes), discord_minutes = VALUES(discord_minutes),
                              steam_minutes = VALUES(steam_minutes)";

            string count = $@"{Limits}SELECT COUNT(DISTINCT DATE(minute))
                              FROM {_names.Bot}.uptime_samples
                              WHERE minute >= @from AND minute < @to";

            await SaveDataCount(total, new { from, to }).ConfigureAwait(false);

            return (int)await LoadFirstData<long, dynamic>(count, new { from, to }).ConfigureAwait(false);
        }

        // Samples from before the cutoff, which the caller gives as a day's start, so only whole days go
        public Task<int> DeleteSamples(DateTime before, int take)
        {
            RequireChunk(before, take);
            RequireDay(before, nameof(before));

            string sql = $@"{Limits}DELETE FROM {_names.Bot}.uptime_samples
                            WHERE minute < @before
                            ORDER BY minute
                            LIMIT @take";

            return SaveDataCount(sql, new { before, take });
        }

        // Dashboard sessions that ended, or were last used, before the cutoff
        public Task<int> DeleteSessions(DateTime before, int take)
        {
            RequireChunk(before, take);

            string sql = $@"{Limits}DELETE FROM {_names.Bot}.dashboard_sessions
                            WHERE expires_at < @before OR last_seen_at < @before
                            ORDER BY expires_at, id_hash
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
