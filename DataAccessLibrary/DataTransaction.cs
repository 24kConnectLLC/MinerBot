using Dapper;
using System.Data.Common;
using System.Globalization;

namespace DataAccessLibrary
{
    // One transaction on one connection, handed to the work that a repository's RunInTransaction runs. It's only valid
    // inside that work. Its statements aren't tried again one by one, because a deadlock rolls back the whole transaction,
    // which then runs again from the start.
    public sealed class DataTransaction
    {
        readonly DbConnection _connection;
        readonly DbTransaction _transaction;
        readonly CancellationToken _abandoned;

        // MariaDB's own syntax for limits that last one statement, so nothing is left on the connection when it goes back to
        // the pool. A statement that waits for a lock or runs past them fails, and the transaction rolls back.
        readonly string _limits;

        // Only DataAccessLibrary's own code runs statements in it
        internal DataTransaction(DbConnection connection, DbTransaction transaction, int statementSeconds, CancellationToken abandoned)
        {
            _connection = connection;
            _transaction = transaction;
            _abandoned = abandoned;
            _limits = string.Create(CultureInfo.InvariantCulture,
                $"SET STATEMENT max_statement_time = {statementSeconds}, lock_wait_timeout = {statementSeconds}, innodb_lock_wait_timeout = {statementSeconds} FOR ");
        }

        // Once the caller has stopped waiting, no more statements start in a transaction that won't commit
        internal async Task<int> Execute(string sql, object? parameters)
        {
            _abandoned.ThrowIfCancellationRequested();

            return await _connection.ExecuteAsync(new CommandDefinition(_limits + sql, parameters, _transaction, cancellationToken: _abandoned)).ConfigureAwait(false);
        }

        // A read that sees the transaction's own changes. With FOR UPDATE it also keeps what it read locked until the
        // transaction ends, so a check and the change that depends on it can't be split by someone else's.
        internal async Task<T?> QueryFirst<T>(string sql, object? parameters)
        {
            _abandoned.ThrowIfCancellationRequested();

            return await _connection.QueryFirstOrDefaultAsync<T>(new CommandDefinition(_limits + sql, parameters, _transaction, cancellationToken: _abandoned)).ConfigureAwait(false);
        }
    }
}
