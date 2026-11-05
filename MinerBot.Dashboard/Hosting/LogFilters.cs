namespace MinerBot.Dashboard.Hosting
{
    internal static class LogFilters
    {
        // The dashboard's own log lines never hold cookies, Authorization headers, codes, states or tokens. These keep the
        // framework's from holding them either.
        public static ILoggingBuilder AddDashboardFilters(this ILoggingBuilder logging)
        {
            // Hosting.Diagnostics logs every request's full address at Information, and Discord's callback address holds the
            // sign-in's code and state. The OAuth handler logs its redirects, with the state, at Debug.
            logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

            // IHttpClientFactory's handlers log each request's address and headers. The Discord client has no loggers either.
            logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
            return logging;
        }
    }
}
