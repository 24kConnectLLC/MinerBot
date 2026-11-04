using Dapper;
using MySql.Data.MySqlClient;
using System.Data;
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

        internal DbBaseConnection(ISQLConnectionFactory sqlConnection, DBConnectionName dBConnectionName)
        {
            this._sqlConnection = sqlConnection;
            this.ConnectionName = dBConnectionName;
        }

        internal IDbConnection CreateConnection(ISQLConnectionFactory connectionFactory)
        {
            return connectionFactory.Create(ConnectionName);
        }

        internal async Task<T> LoadFirstData<T, U>(string sql, U parameters)
        {
            var data = await Read(connection => connection.QueryAsync<T>(sql, parameters)).ConfigureAwait(false);
            return data.FirstOrDefault();
        }

        internal Task<IEnumerable<T>> LoadDataEnum<T, U>(string sql, U parameters)
        {
            return Read(connection => connection.QueryAsync<T>(sql, parameters));
        }

        // Never tried again, because the first try may have gone through
        internal Task SaveData<T>(string sql, T parameters)
        {
            return WithLimit(connection => connection.ExecuteAsync(sql, parameters));
        }

        // A read that met a passing problem gets one more try. A timeout isn't one, so a silent server costs one limit, not two.
        async Task<T> Read<T>(Func<IDbConnection, Task<T>> query)
        {
            try
            {
                return await WithLimit(query).ConfigureAwait(false);
            }
            catch (MySqlException e) when (IsTransient(e))
            {
                return await WithLimit(query).ConfigureAwait(false);
            }
        }

        // Run on the thread pool, because a driver that blocks before it hands back a task would hold the caller
        // before the limit starts. The connection is closed in there too, so a call that was given up on keeps it
        // until it ends, and closing it can't hold the caller either.
        async Task<T> WithLimit<T>(Func<IDbConnection, Task<T>> call)
        {
            var running = Task.Run(async () =>
            {
                using var connection = CreateConnection(_sqlConnection);
                return await call(connection).ConfigureAwait(false);
            });

            try
            {
                return await running.WaitAsync(CallLimit).ConfigureAwait(false);
            }
            catch (TimeoutException e) when (!running.IsCompleted)
            {
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
    }
}
