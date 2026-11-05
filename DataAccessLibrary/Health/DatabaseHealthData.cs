using System.Data.Common;

namespace DataAccessLibrary.Health
{
    // Whether a database answers a trivial question within a short limit, for the dashboard's readiness check. It never
    // throws, so the check never sees the driver's text, which names the login and the host.
    public sealed class DatabaseHealthData(ISQLConnectionFactory connectionFactory) : IDatabaseHealthData
    {
        public async Task<bool> Answers(DBConnectionName database, TimeSpan limit)
        {
            try
            {
                // A missing connection string is a no at once, so the probe never falls back to the driver's defaults
                using (var connection = connectionFactory.Create(database))
                {
                    if (string.IsNullOrWhiteSpace(connection.ConnectionString))
                        return false;
                }

                // A read, so a refused connection gets one more try, but a server that doesn't answer costs one limit
                var probe = new Probe(connectionFactory, database) { CallLimit = limit };
                return await probe.LoadFirstData<int, object?>("SELECT 1", null).ConfigureAwait(false) == 1;
            }
            catch (Exception e) when (e is DbException or TimeoutException or InvalidOperationException or ArgumentException)
            {
                // InvalidOperationException and ArgumentException are a connection string that's missing or unreadable
                return false;
            }
        }

        // DbBaseConnection's calls, with the limit given in place of the usual 15 seconds
        sealed class Probe(ISQLConnectionFactory connectionFactory, DBConnectionName database) : DbBaseConnection(connectionFactory, database);
    }
}
