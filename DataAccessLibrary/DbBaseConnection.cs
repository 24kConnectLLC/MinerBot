using Dapper;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataAccessLibrary
{
    public abstract class DbBaseConnection
    {
        internal ISQLConnectionFactory _sqlConnection { get; private set; }
        internal DBConnectionName ConnectionName { get; private set; }

        internal DbBaseConnection(ISQLConnectionFactory sqlConnection, DBConnectionName dBConnectionName)
        {
            this._sqlConnection = sqlConnection;
            this.ConnectionName = dBConnectionName;
        }

        internal IDbConnection CreateConnection(ISQLConnectionFactory connectionFactory)
        {
            return connectionFactory.Create(ConnectionName);
        }

        internal async Task<List<T>> LoadData<T, U>(string sql, U parameters)
        {
            using (var connection = CreateConnection(_sqlConnection))
            {
                var data = await connection.QueryAsync<T>(sql, parameters).ConfigureAwait(false);
                return data.AsList();
            }
        }

        internal async Task<T> LoadFirstData<T, U>(string sql, U parameters)
        {
            using (var connection = CreateConnection(_sqlConnection))
            {
                var data = await connection.QueryAsync<T>(sql, parameters).ConfigureAwait(false);
                return data.FirstOrDefault();
            }
        }

        internal async Task<IEnumerable<T>> LoadDataEnum<T, U>(string sql, U parameters)
        {
            using (var connection = CreateConnection(_sqlConnection))
            {
                return (await connection.QueryAsync<T>(sql, parameters).ConfigureAwait(false));
            }
        }

        internal async Task SaveData<T>(string sql, T parameters)
        {
            using (var connection = CreateConnection(_sqlConnection))
            {
                await connection.ExecuteAsync(sql, parameters).ConfigureAwait(true);

                connection.Dispose();
                connection.Close();
            }
        }
    }
}
