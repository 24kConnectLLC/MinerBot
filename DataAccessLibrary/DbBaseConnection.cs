using Dapper;
using MySql.Data.MySqlClient;
using System.Data;
using System.Data.Common;
using System.Net.Sockets;

namespace DataAccessLibrary
{
    public abstract class DbBaseConnection
    {
        internal ISQLConnectionFactory _sqlConnection { get; private set; }
        internal DBConnectionName ConnectionName { get; private set; }

        // MySql.Data's async calls don't end when the server stops answering, whatever CommandTimeout, Connection Timeout
        // or a token say, so every call has this limit on top. Shorter in tests.
        internal TimeSpan CallLimit = TimeSpan.FromSeconds(15);

        // The most rows one page of a list can ask for
        internal const int MaxPageSize = 100;

        const string Utf8mb4 = "utf8mb4";

        // The tables are utf8mb4, but a connection string without CharSet makes MySql.Data send text in the server's
        // default character set. On a server left at latin1 that turns an emoji or a zero-width space into a question mark
        // without an error, so a write loses it and a name check misses a clash. So every repository sends utf8mb4 whatever
        // its connection string says, apart from the bot's first ones, which keep theirs exactly as configured so the
        // running bot doesn't change.
        internal bool SendsUtf8mb4 { get; init; } = true;

        internal DbBaseConnection(ISQLConnectionFactory sqlConnection, DBConnectionName dBConnectionName)
        {
            this._sqlConnection = sqlConnection;
            this.ConnectionName = dBConnectionName;
        }

        internal IDbConnection CreateConnection(ISQLConnectionFactory connectionFactory)
        {
            var connection = connectionFactory.Create(ConnectionName);

            // Read before the connection opens, so it still holds the password. A string that already names utf8mb4 is left
            // as it is, so it shares its pool with the repositories that don't change theirs.
            if (SendsUtf8mb4 && connection is MySqlConnection mySql)
            {
                var settings = new MySqlConnectionStringBuilder(mySql.ConnectionString);

                if (!string.Equals(settings.CharacterSet, Utf8mb4, StringComparison.OrdinalIgnoreCase))
                {
                    settings.CharacterSet = Utf8mb4;
                    mySql.ConnectionString = settings.ConnectionString;
                }
            }

            return connection;
        }

        internal async Task<T> LoadFirstData<T, U>(string sql, U parameters)
        {
            var data = await Read(connection => connection.QueryAsync<T>(sql, parameters)).ConfigureAwait(false);
            return data.FirstOrDefault();
        }

        // Inside the transaction when it's given one, where it sees the transaction's own changes and isn't tried again
        internal async Task<T?> LoadFirstData<T, U>(string sql, U parameters, DataTransaction? transaction)
        {
            if (transaction != null)
                return await transaction.QueryFirst<T>(sql, parameters).ConfigureAwait(false);

            return await LoadFirstData<T, U>(sql, parameters).ConfigureAwait(false);
        }

        internal Task<IEnumerable<T>> LoadDataEnum<T, U>(string sql, U parameters)
        {
            return Read(connection => connection.QueryAsync<T>(sql, parameters));
        }

        // The rows of one page and the total on every page, from one call, so it gets the same one more try as other reads
        internal Task<Page<T>> LoadPage<T, U>(string sql, U parameters)
        {
            return Read(async connection =>
            {
                using var results = await connection.QueryMultipleAsync(sql, parameters).ConfigureAwait(false);

                var rows = (await results.ReadAsync<T>().ConfigureAwait(false)).ToList();
                var total = await results.ReadSingleAsync<long>().ConfigureAwait(false);

                return new Page<T>(rows, total);
            });
        }

        // Never tried again, because the first try may have gone through
        internal Task SaveData<T>(string sql, T parameters)
        {
            return WithLimit((connection, _) => connection.ExecuteAsync(sql, parameters));
        }

        // Like SaveData, but says how many rows the statement matched, inside a transaction when it's given one.
        // MySql.Data counts the rows a statement matched, not the ones it changed (UseAffectedRows is false), so a
        // conditional update puts the old value in its WHERE to tell whether it applied.
        internal Task<int> SaveDataCount<T>(string sql, T parameters, DataTransaction? transaction = null)
        {
            if (transaction != null)
                return transaction.Execute(sql, parameters);

            return WithLimit((connection, _) => connection.ExecuteAsync(sql, parameters));
        }

        // The work's statements run in one transaction on one connection, inside the same limit as any call, and are never
        // tried again, like every write. A caller that stopped waiting at the limit is told the outcome is unknown, and
        // committing after that would land a change nobody is waiting for, so disposing the transaction rolls it back instead.
        internal Task<T> InTransaction<T>(Func<DataTransaction, Task<T>> work)
        {
            return WithLimit(async (connection, abandoned) =>
            {
                var dbConnection = (DbConnection)connection;
                await dbConnection.OpenAsync(abandoned).ConfigureAwait(false);

                // Named, because MySql.Data's own async default is READ COMMITTED, where a locking read lets go of the rows it
                // passed without matching, and a check such as IsApprovedNameTaken has to hold the whole table until the commit
                await using var transaction = await dbConnection.BeginTransactionAsync(IsolationLevel.RepeatableRead, abandoned).ConfigureAwait(false);
                var result = await work(new DataTransaction(dbConnection, transaction, abandoned)).ConfigureAwait(false);

                abandoned.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);

                return result;
            });
        }

        // A read that met a passing problem gets one more try. A timeout isn't one, so a silent server costs one limit, not two.
        async Task<T> Read<T>(Func<IDbConnection, Task<T>> query)
        {
            try
            {
                return await WithLimit((connection, _) => query(connection)).ConfigureAwait(false);
            }
            catch (MySqlException e) when (IsTransient(e))
            {
                return await WithLimit((connection, _) => query(connection)).ConfigureAwait(false);
            }
        }

        // Run on the thread pool, because a driver that blocks before it hands back a task would hold the caller
        // before the limit starts. The connection is closed in there too, so a call that was given up on keeps it
        // until it ends, and closing it can't hold the caller either.
        // The token is cancelled when the caller stops waiting, so work that checks it, such as a transaction, ends there.
        // It isn't disposed, because a call that was given up on may still read it.
        async Task<T> WithLimit<T>(Func<IDbConnection, CancellationToken, Task<T>> call)
        {
            var abandoned = new CancellationTokenSource();

            var running = Task.Run(async () =>
            {
                using var connection = CreateConnection(_sqlConnection);
                return await call(connection, abandoned.Token).ConfigureAwait(false);
            });

            try
            {
                return await running.WaitAsync(CallLimit).ConfigureAwait(false);
            }
            catch (TimeoutException e) when (!running.IsCompleted)
            {
                // Not awaited, so a callback that stops a command on the server can't hold the caller
                _ = abandoned.CancelAsync();
                throw new TimeoutException($"The database didn't answer within {CallLimit.TotalSeconds:0.#} seconds", e);
            }
        }

        // A refused or unreachable server, a lock that timed out or deadlocked, or a dropped connection, which MySql.Data
        // reports with the stream's error inside. Its own IsFatal flag is internal.
        static bool IsTransient(MySqlException e)
        {
            if (e.Number is (int)MySqlErrorCode.UnableToConnectToHost or (int)MySqlErrorCode.LockWaitTimeout or (int)MySqlErrorCode.LockDeadlock)
                return true;

            for (var inner = e.InnerException; inner != null; inner = inner.InnerException)
            {
                if (inner is IOException or SocketException)
                    return true;
            }

            return false;
        }

        // Pages count from 0, like /armor list's, and give the rows to take and to skip
        internal static (int Take, long Skip) RequirePage(int page, int pageSize)
        {
            if (page < 0)
                throw new ArgumentOutOfRangeException(nameof(page), page, "Pages count from 0");

            if (pageSize is < 1 or > MaxPageSize)
                throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, $"A page holds from 1 to {MaxPageSize} rows");

            return (pageSize, (long)page * pageSize);
        }

        // For LIKE ... ESCAPE '!', so a search for 100% or a_b finds just that. An escape character of its own works
        // whatever the server's NO_BACKSLASH_ESCAPES says.
        internal static string EscapeLike(string text) => text.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");

        // The bot's tables hold UTC, and a local time would put a row hours off without a word
        internal static DateTime RequireUtc(DateTime time, string name)
        {
            if (time.Kind == DateTimeKind.Local)
                throw new ArgumentException("Times are saved in UTC, and this one is local", name);

            return time;
        }

        // MariaDB counts code points, and its strict mode refuses a value that's too long rather than cutting it.
        // A character outside the Basic Multilingual Plane is kept whole or left out.
        internal static string? Clip(string? text, int maxCodePoints)
        {
            if (text == null)
                return null;

            var end = 0;

            for (var count = 0; count < maxCodePoints && end < text.Length; count++)
                end += char.IsSurrogatePair(text, end) ? 2 : 1;

            return end >= text.Length ? text : text[..end];
        }

        internal static int CodePoints(string text) => text.EnumerateRunes().Count();
    }
}
