using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace MinerBot.Dashboard.Security
{
    // The exception handler's page, written here directly rather than by running the pipeline again, so a database outage
    // can't break the error page too. It says what to do and shows a request ID, and never anything of the exception.
    internal static class ErrorResponses
    {
        internal const string Unavailable = "The dashboard can't reach its database just now, so nothing was changed. Try again in a few minutes.";
        internal const string Failed = "Something went wrong, and nothing was changed.";
        internal const string Unreadable = "That request was too big, or couldn't be read, so nothing was changed.";

        public static async Task WriteAsync(HttpContext context)
        {
            var failure = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ErrorResponses).FullName!);

            var (status, title, message) = failure switch
            {
                DashboardUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Temporarily unavailable", Unavailable),
                // Such as a body over Kestrel's limit, which is the visitor's doing, not a fault
                BadHttpRequestException bad => (bad.StatusCode, "That request couldn't be read", Unreadable),
                _ => (StatusCodes.Status500InternalServerError, "Something went wrong", Failed)
            };

            if (failure is BadHttpRequestException)
                logger.LogInformation("Request {RequestId} couldn't be read, so it was answered with {Status}", context.TraceIdentifier, status);
            else
                logger.LogError("Request {RequestId} failed with {Failure}", context.TraceIdentifier, Describe(failure));

            context.Response.StatusCode = status;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(Page(context, title, message));
        }

        // The type and stack trace of each exception in the chain, but never a message, because the database driver's
        // messages name its login and host
        internal static string Describe(Exception? failure)
        {
            if (failure == null)
                return "no exception";

            var text = new StringBuilder();

            for (var exception = failure; exception != null; exception = exception.InnerException)
            {
                if (text.Length > 0)
                    text.Append("\n---> ");

                text.Append(exception.GetType().FullName).Append('\n').Append(exception.StackTrace);
            }

            return text.ToString();
        }

        // The same outline as the layout's, so it looks like the rest of the dashboard and works without the database
        static string Page(HttpContext context, string title, string message)
        {
            var html = HtmlEncoder.Default;

            return $"""
                <!DOCTYPE html>
                <html lang="en">
                <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <meta name="color-scheme" content="light dark">
                <title>{html.Encode(title)} - MinerBot dashboard</title>
                <link rel="stylesheet" href="{html.Encode(Stylesheet(context))}">
                </head>
                <body>
                <a class="skip-link" href="#main">Skip to the content</a>
                <main id="main" class="column">
                <h1>{html.Encode(title)}</h1>
                <p>{html.Encode(message)}</p>
                <p class="request-id">Request ID: <code>{html.Encode(context.TraceIdentifier)}</code></p>
                <p><a href="/">Go to the home page</a></p>
                </main>
                </body>
                </html>
                """;
        }

        // Versioned like the layout's link, so a browser never keeps an old stylesheet
        static string Stylesheet(HttpContext context)
        {
            const string path = "/css/site.css";

            try
            {
                return context.RequestServices.GetService<IFileVersionProvider>()?.AddFileVersionToPath(context.Request.PathBase, path) ?? path;
            }
            catch (IOException)
            {
                return path;
            }
        }
    }
}
