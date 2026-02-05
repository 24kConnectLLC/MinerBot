using Dapper;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataAccessLibrary
{
    public enum DBConnectionName
    {
        Armor,
        Blacklist,
        Guild
    }

    public sealed class SQLConnectionFactory : ISQLConnectionFactory
    {
        private readonly IDictionary<DBConnectionName, string> _connectionDict;
        public SQLConnectionFactory(IDictionary<DBConnectionName, string> connectionDict)
        {
            _connectionDict = connectionDict;
            SqlMapper.AddTypeHandler(new DapperSqlDateOnlyTypeHandler());
            SqlMapper.Settings.CommandTimeout = 60;
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

    internal class DapperSqlDateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly date)
            => parameter.Value = date.ToDateTime(new TimeOnly(0, 0));

        public override DateOnly Parse(object value)
            => DateOnly.FromDateTime((DateTime)value);
    }
}
