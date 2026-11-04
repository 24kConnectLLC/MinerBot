using Dapper;
using System.Data.Common;

namespace DataAccessLibrary
{
    // One transaction on one connection, handed to the work that a repository's RunInTransaction runs. It's only valid
    // inside that work. Nothing in it is tried again, because a deadlock rolls back the whole transaction, not one statement.
    public sealed class DataTransaction
    {
        readonly DbConnection _connection;
        readonly DbTransaction _transaction;
        readonly CancellationToken _abandoned;

        // Only DataAccessLibrary's own code runs statements in it
        internal DataTransaction(DbConnection connection, DbTransaction transaction, CancellationToken abandoned)
        {
            _connection = connection;
            _transaction = transaction;
            _abandoned = abandoned;
        }

        // Once the caller has stopped waiting, no more statements start in a transaction that won't commit
        internal async Task<int> Execute(string sql, object? parameters)
        {
            _abandoned.ThrowIfCancellationRequested();

            return await _connection.ExecuteAsync(new CommandDefinition(sql, parameters, _transaction, cancellationToken: _abandoned)).ConfigureAwait(false);
        }

        // A read that sees the transaction's own changes. With FOR UPDATE it also keeps what it read locked until the
        // transaction ends, so a check and the change that depends on it can't be split by someone else's.
        internal async Task<T?> QueryFirst<T>(string sql, object? parameters)
        {
            _abandoned.ThrowIfCancellationRequested();

            return await _connection.QueryFirstOrDefaultAsync<T>(new CommandDefinition(sql, parameters, _transaction, cancellationToken: _abandoned)).ConfigureAwait(false);
        }
    }
}
