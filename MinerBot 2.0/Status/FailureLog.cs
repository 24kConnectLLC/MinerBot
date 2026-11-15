using MySql.Data.MySqlClient;
using System.Diagnostics;

namespace MinerBot_2._0.Status
{
    // Logs a failure that repeats at most once a minute, so a loop that fails every few seconds doesn't flood the log. It
    // never throws, because the loops call it from their last catch, and keeps its own time for the same reason.
    internal sealed class FailureLog(ILogger logger)
    {
        static readonly TimeSpan Quiet = TimeSpan.FromMinutes(1);

        readonly Dictionary<string, long> logged = new(StringComparer.Ordinal);

        public void Log(Exception e, string what)
        {
            try
            {
                var problem = Describe(e);
                var now = Stopwatch.GetTimestamp();

                lock (logged)
                {
                    if (logged.TryGetValue(what + problem, out var last) && Stopwatch.GetElapsedTime(last, now) < Quiet)
                        return;

                    logged[what + problem] = now;
                }

                logger.LogError("{What} ({Problem}), and carries on", what, problem);
            }
            catch (Exception)
            {
                // Logging is all this does
            }
        }

        // The exception's type, with MariaDB's error number when there's one, but never the message: MySql.Data's can name
        // the login and the server
        public static string Describe(Exception e)
        {
            for (var inner = e; inner != null; inner = inner.InnerException)
            {
                if (inner is MySqlException mySql)
                    return $"{e.GetType().Name}, MySQL error {mySql.Number}";
            }

            return e.GetType().Name;
        }
    }
}
