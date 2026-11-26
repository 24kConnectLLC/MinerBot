using Discord.Net;
using MinerBot.Shared.Delivery;
using MySql.Data.MySqlClient;
using System.Diagnostics;
using System.Globalization;

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

        // The exception's type, with MariaDB's error number or Discord's numbers when there are some, but never the message:
        // MySql.Data's can name the login and the server, and Discord's can carry Discord's own words or a token. Discord's
        // answer is its HTTP status, and its own code when that isn't 0, which Discord's 5xx and 401 answers give.
        public static string Describe(Exception e)
        {
            for (var inner = e; inner != null; inner = inner.InnerException)
            {
                if (inner is MySqlException mySql)
                    return $"{e.GetType().Name}, MySQL error {mySql.Number}";

                if (inner is HttpException http)
                {
                    var code = Code(http) is { } discord ? string.Create(CultureInfo.InvariantCulture, $", Discord {discord}") : "";
                    return string.Create(CultureInfo.InvariantCulture, $"{e.GetType().Name}, HTTP {(int)http.HttpCode}{code}");
                }
            }

            return e.GetType().Name;
        }

        // Discord's own code for an answer, such as 50013 for a missing permission, or the HTTP status when it gave none, or 0
        public static int DiscordCode(HttpException e) => Code(e) ?? (int)e.HttpCode;

        // Discord's code 0 is its "general error", which says nothing more than the status
        static int? Code(HttpException e) => DeliveryReasons.CodeOf((int?)e.DiscordCode);
    }
}
