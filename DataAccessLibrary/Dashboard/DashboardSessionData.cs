using DataAccessLibrary.Dashboard.Models;
using System.Security.Cryptography;
using System.Text;

namespace DataAccessLibrary.Dashboard
{
    // The dashboard's sign-ins, in MinerBot's own database. Every method takes the key from the cookie and hashes it here,
    // so only the hash is ever stored, and someone who can read the table can't sign in with what they find. The Sessions
    // page lists sessions and ends one by that hash, which signs nobody in either.
    public class DashboardSessionData : DbBaseConnection, IDashboardSessionData
    {
        // The dashboard's keys are 256 random bits, which take more characters than this however they're written
        const int MinKeyLength = 32;

        // A key's SHA-256, as id_hash holds it
        public const int HashLength = 32;

        // The most sessions the Sessions page lists. Each person keeps at most 5.
        public const int ListLimit = 200;

        // Every column but the ticket, which no page reads
        const string ListColumns = "id_hash, user_id, user_name, created_at, last_seen_at, expires_at, ip, user_agent";

        const int UserNameLimit = 100;
        const int IpLimit = 45;

        // The visitor's browser sends it, so it can be any length, and strict mode would fail the sign-in over it
        const int UserAgentLimit = 200;

        private readonly DatabaseNames _names;

        public DashboardSessionData(ISQLConnectionFactory connectionFactory, DatabaseNames names) : base(connectionFactory, DBConnectionName.Bot)
        {
            _names = names;
        }

        public Task CreateSession(string key, DashboardSession session, DataTransaction? transaction = null)
        {
            var hash = HashOf(key);
            RequireUtc(session.created_at, nameof(session.created_at));
            RequireUtc(session.last_seen_at, nameof(session.last_seen_at));
            RequireUtc(session.expires_at, nameof(session.expires_at));
            ArgumentNullException.ThrowIfNull(session.ticket);

            string sql = $@"INSERT INTO {_names.Bot}.dashboard_sessions (id_hash, user_id, user_name, created_at, last_seen_at, expires_at, ip, user_agent, ticket)
                            VALUES (@hash, @user_id, @user_name, @created_at, @last_seen_at, @expires_at, @ip, @user_agent, @ticket)";

            return SaveDataCount(sql, new
            {
                hash,
                session.user_id,
                user_name = Clip(session.user_name, UserNameLimit),
                session.created_at,
                session.last_seen_at,
                session.expires_at,
                ip = Clip(session.ip, IpLimit),
                user_agent = Clip(session.user_agent, UserAgentLimit),
                session.ticket
            }, transaction);
        }

        // Null when there's no such session, or it has ended
        public Task<DashboardSession?> GetSession(string key, DateTime now)
        {
            var hash = HashOf(key);
            RequireUtc(now, nameof(now));

            string sql = $@"SELECT user_id, user_name, created_at, last_seen_at, expires_at, ip, user_agent, ticket
                            FROM {_names.Bot}.dashboard_sessions
                            WHERE id_hash = @hash AND expires_at > @now";

            return LoadFirstData<DashboardSession?, dynamic>(sql, new { hash, now });
        }

        // True while the session exists, even when the time is the same, because MySql.Data counts the rows matched
        public async Task<bool> TouchSession(string key, DateTime lastSeenAt)
        {
            var hash = HashOf(key);
            RequireUtc(lastSeenAt, nameof(lastSeenAt));

            string sql = $@"UPDATE {_names.Bot}.dashboard_sessions
                            SET last_seen_at = @lastSeenAt
                            WHERE id_hash = @hash";

            return await SaveDataCount(sql, new { hash, lastSeenAt }).ConfigureAwait(false) == 1;
        }

        // A ticket that the dashboard's sign-in renewed. The session's end stays where CreateSession put it, because the
        // sign-in's sliding expiry hands over a later time on every renewal, and expires_at is the latest a session can last.
        public async Task<bool> RenewSession(string key, byte[] ticket)
        {
            var hash = HashOf(key);
            ArgumentNullException.ThrowIfNull(ticket);

            string sql = $@"UPDATE {_names.Bot}.dashboard_sessions
                            SET ticket = @ticket
                            WHERE id_hash = @hash";

            return await SaveDataCount(sql, new { hash, ticket }).ConfigureAwait(false) == 1;
        }

        public async Task<bool> DeleteSession(string key, DataTransaction? transaction = null)
        {
            var hash = HashOf(key);

            string sql = $@"DELETE FROM {_names.Bot}.dashboard_sessions
                            WHERE id_hash = @hash";

            return await SaveDataCount(sql, new { hash }, transaction).ConfigureAwait(false) == 1;
        }

        // Signs a user out everywhere but the session that asked
        public Task<int> DeleteOtherSessions(ulong userId, string keepKey, DataTransaction? transaction = null)
        {
            var keepHash = HashOf(keepKey);

            string sql = $@"DELETE FROM {_names.Bot}.dashboard_sessions
                            WHERE user_id = @userId AND id_hash <> @keepHash";

            return SaveDataCount(sql, new { userId, keepHash }, transaction);
        }

        // Keeps the user's newest sessions, by when each was last used, and ends the rest, so a sign-in can cap how many one
        // person has. Keeping 0 ends all of them, such as for someone taken off the dashboard's lists.
        public Task<int> DeleteOldestSessions(ulong userId, int keep, DataTransaction? transaction = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(keep);

            // MariaDB takes a LIMIT inside a derived table, though not straight inside IN
            string sql = $@"DELETE FROM {_names.Bot}.dashboard_sessions
                            WHERE user_id = @userId AND id_hash NOT IN (
                                SELECT id_hash FROM (
                                    SELECT id_hash
                                    FROM {_names.Bot}.dashboard_sessions
                                    WHERE user_id = @userId
                                    ORDER BY last_seen_at DESC, created_at DESC, id_hash DESC
                                    LIMIT @keep) AS newest)";

            return SaveDataCount(sql, new { userId, keep }, transaction);
        }

        // Sessions that ended strictly before the first time given, or that were last used strictly before the second. The
        // dashboard refuses a session unused for too long, and this takes its row, with its address and browser, soon after.
        public Task<int> DeleteExpiredSessions(DateTime expiredBefore, DateTime unusedBefore)
        {
            RequireUtc(expiredBefore, nameof(expiredBefore));
            RequireUtc(unusedBefore, nameof(unusedBefore));

            string sql = $@"DELETE FROM {_names.Bot}.dashboard_sessions
                            WHERE expires_at < @expiredBefore OR last_seen_at < @unusedBefore";

            return SaveDataCount(sql, new { expiredBefore, unusedBefore });
        }

        // On MinerBot's own connection, so a session change and its audit row commit together. The database is named
        // first, so a missing BotConnection fails with that reason before any connection is tried.
        public Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work)
        {
            _ = _names.Bot;
            return InTransaction(work);
        }

        // The sessions in use, everyone's, most recently used first: not ended, and used at or after the second time
        // given, as the dashboard still takes them. Rows past either can stay until a sign-in's sweep or the nightly
        // clean-up takes them, so they're left out here.
        public async Task<IReadOnlyList<SessionListRow>> ListSessions(DateTime now, DateTime unusedBefore)
        {
            RequireUtc(now, nameof(now));
            RequireUtc(unusedBefore, nameof(unusedBefore));

            string sql = $@"SELECT {ListColumns}
                            FROM {_names.Bot}.dashboard_sessions
                            WHERE expires_at > @now AND last_seen_at >= @unusedBefore
                            ORDER BY last_seen_at DESC, created_at DESC
                            LIMIT {ListLimit}";

            var rows = await LoadDataEnum<SessionListRow, dynamic>(sql, new { now, unusedBefore }).ConfigureAwait(false);
            return rows.ToList();
        }

        // Ends the session whose key hashes to this, whoever's it is, and gives it as it was, or null when there's none, in
        // the caller's transaction with its audit row. The locked read keeps the row until the delete, so what this gives
        // is what it deleted.
        public async Task<SessionListRow?> DeleteSessionByHash(byte[] hash, DataTransaction transaction)
        {
            ArgumentNullException.ThrowIfNull(transaction);

            if (hash is not { Length: HashLength })
                throw new ArgumentException($"A session's hash is {HashLength} bytes", nameof(hash));

            // Every statement names the database, here, so a missing BotConnection fails before any statement runs
            var bot = _names.Bot;

            string read = $@"SELECT {ListColumns}
                             FROM {bot}.dashboard_sessions
                             WHERE id_hash = @hash
                             FOR UPDATE";

            string delete = $@"DELETE FROM {bot}.dashboard_sessions
                               WHERE id_hash = @hash";

            var session = await transaction.QueryFirst<SessionListRow>(read, new { hash }).ConfigureAwait(false);

            if (session != null)
                await transaction.Execute(delete, new { hash }).ConfigureAwait(false);

            return session;
        }

        // Ends every session but the one with this key, everyone's, the same person's other ones included, and gives them as
        // they were, in the caller's transaction with its audit row. The locked read holds exactly the rows the delete then
        // takes, and keeps a new sign-in from slipping in between.
        public async Task<IReadOnlyList<SessionListRow>> DeleteSessionsBut(string keepKey, DataTransaction transaction)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            var keepHash = HashOf(keepKey);
            var bot = _names.Bot;

            string read = $@"SELECT {ListColumns}
                             FROM {bot}.dashboard_sessions
                             WHERE id_hash <> @keepHash
                             ORDER BY created_at, user_id
                             FOR UPDATE";

            string delete = $@"DELETE FROM {bot}.dashboard_sessions
                               WHERE id_hash <> @keepHash";

            var sessions = await transaction.Query<SessionListRow>(read, new { keepHash }).ConfigureAwait(false);

            if (sessions.Count > 0)
                await transaction.Execute(delete, new { keepHash }).ConfigureAwait(false);

            return sessions;
        }

        // A key's SHA-256, as id_hash holds it, so a page can tell which listed session is its own
        public static byte[] HashOf(string key)
        {
            if (key == null || key.Length < MinKeyLength)
                throw new ArgumentException($"A session key has at least {MinKeyLength} characters", nameof(key));

            return SHA256.HashData(Encoding.UTF8.GetBytes(key));
        }
    }
}
