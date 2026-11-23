using MySql.Data.MySqlClient;
using System.Data;

namespace DataAccessLibrary
{
    public enum DBConnectionName
    {
        Armor,
        Blacklist,
        Guild,
        Bot,

        // The website's database again, for the tables of the feed the bot writes for the website's pages
        Feed
    }

    public sealed class SQLConnectionFactory : ISQLConnectionFactory
    {
        private readonly IDictionary<DBConnectionName, string> _connectionDict;
        public SQLConnectionFactory(IDictionary<DBConnectionName, string> connectionDict)
        {
            _connectionDict = connectionDict;
        }

        public IDbConnection Create(DBConnectionName connectionName)
        {
            string connectionString = null;
            if (_connectionDict.TryGetValue(connectionName, out connectionString))
            {
                return new MySqlConnection(connectionString);
            }
            throw new ArgumentNullException();
        }
    }
}
