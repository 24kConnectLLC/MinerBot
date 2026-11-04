using DataAccessLibrary.Dashboard.Models;
using System.Security.Cryptography;
using System.Text;

namespace DataAccessLibrary.Dashboard
{
    // The dashboard's sign-ins, in MinerBot's own database. Every method takes the key from the cookie and hashes it here,
    // so only the hash is ever stored, and someone who can read the table can't sign in with what they find.
    public class DashboardSessionData : DbBaseConnection, IDashboardSessionData
    {
        // The dashboard's keys are 256 random bits, which take more characters than this however they're written
        const int MinKeyLength = 32;

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
            var hash = HashKey(key);
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
            var hash = HashKey(key);
            RequireUtc(now, nameof(now));

            string sql = $@"SELECT user_id, user_name, created_at, last_seen_at, expires_at, ip, user_agent, ticket
                            FROM {_names.Bot}.dashboard_sessions
                            WHERE id_hash = @hash AND expires_at > @now";

            return LoadFirstData<DashboardSession?, dynamic>(sql, new { hash, now });
        }

        // True while the session exists, even when the time is the same, because MySql.Data counts the rows matched
        public async Task<bool> TouchSession(string key, DateTime lastSeenAt)
        {
            var hash = HashKey(key);
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
            var hash = HashKey(key);
            ArgumentNullException.ThrowIfNull(ticket);

            string sql = $@"UPDATE {_names.Bot}.dashboard_sessions
                            SET ticket = @ticket
                            WHERE id_hash = @hash";

            return await SaveDataCount(sql, new { hash, ticket }).ConfigureAwait(false) == 1;
        }

        public async Task<bool> DeleteSession(string key, DataTransaction? transaction = null)
        {
            var hash = HashKey(key);

            string sql = $@"DELETE FROM {_names.Bot}.dashboard_sessions
                            WHERE id_hash = @hash";

            return await SaveDataCount(sql, new { hash }, transaction).ConfigureAwait(false) == 1;
        }

        // Signs a user out everywhere but the session that asked
        public Task<int> DeleteOtherSessions(ulong userId, string keepKey, DataTransaction? transaction = null)
        {
            var keepHash = HashKey(keepKey);

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

        // Sessions that ended strictly before the time given
        public Task<int> DeleteExpiredSessions(DateTime expiredBefore)
        {
            RequireUtc(expiredBefore, nameof(expiredBefore));

            string sql = $@"DELETE FROM {_names.Bot}.dashboard_sessions
                            WHERE expires_at < @expiredBefore";

            return SaveDataCount(sql, new { expiredBefore });
        }

        // On MinerBot's own connection, so a session change and its audit row commit together. The database is named
        // first, so a missing BotConnection fails with that reason before any connection is tried.
        public Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work)
        {
            _ = _names.Bot;
            return InTransaction(work);
        }

        internal static byte[] HashKey(string key)
        {
            if (key == null || key.Length < MinKeyLength)
                throw new ArgumentException($"A session key has at least {MinKeyLength} characters", nameof(key));

            return SHA256.HashData(Encoding.UTF8.GetBytes(key));
        }
    }
}
