using System.Data;

namespace DataAccessLibrary.Tests
{
    // Stands in for a database a test must not reach: it counts the connections asked for and never makes one
    internal sealed class NoDatabase : ISQLConnectionFactory
    {
        int created;

        public int Created => Volatile.Read(ref created);

        // Made up and never connected to, but enough for the bot's tables to have a database name
        public static DatabaseNames Names { get; } = new(new Dictionary<DBConnectionName, string>
        {
            [DBConnectionName.Bot] = "Server=127.0.0.1;Database=bot_db;Uid=user;Pwd=password"
        });

        public IDbConnection Create(DBConnectionName connectionName)
        {
            Interlocked.Increment(ref created);
            throw new InvalidOperationException("The test expected no connection");
        }
    }
}
