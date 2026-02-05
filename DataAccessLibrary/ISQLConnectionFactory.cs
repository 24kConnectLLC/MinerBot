using System.Data;

namespace DataAccessLibrary
{
    public interface ISQLConnectionFactory
    {
        IDbConnection Create(DBConnectionName connectionName);
    }
}