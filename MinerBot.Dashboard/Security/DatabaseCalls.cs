using System.Data.Common;
using System.Globalization;
using MySql.Data.MySqlClient;

namespace MinerBot.Dashboard.Security
{
    // The database couldn't be reached or didn't answer in time. Its message is fixed and it carries no inner exception,
    // because the driver's text names the login and the host.
    internal sealed class DashboardUnavailableException() : Exception("The dashboard couldn't reach its database");

    internal static class DatabaseCalls
    {
        // Every database call the dashboard makes goes through here. A database that's down or hung becomes
        // DashboardUnavailableException, which the error page shows as "try again in a few minutes" and nothing else, so
        // signed-in pages fail closed.
        public static async Task<T> Guard<T>(Func<Task<T>> call, ILogger logger)
        {
            try
            {
                return await call();
            }
            catch (Exception e) when (e is DbException or TimeoutException)
            {
                // MariaDB's error number, never the message: MySql.Data leaves the SQL state empty, so the number is what
                // tells a right the login lacks, 1142 or 1143, from a database that's down
                logger.LogWarning("A database call failed with {Type}, SQL state {State}, MariaDB error {Number}", e.GetType().Name, (e as DbException)?.SqlState ?? "none",
                    e is MySqlException mariaDb ? mariaDb.Number.ToString(CultureInfo.InvariantCulture) : "none");
                throw new DashboardUnavailableException();
            }
        }

        public static Task Guard(Func<Task> call, ILogger logger) => Guard(async () =>
        {
            await call();
            return true;
        }, logger);
    }
}
