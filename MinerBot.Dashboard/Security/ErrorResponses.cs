using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using MinerBot.Dashboard.Hosting;

namespace MinerBot.Dashboard.Security
{
    // The exception handler's page, written here directly rather than by running the pipeline again, so a database outage
    // can't break the error page too. It says what to do and shows a request ID, and never anything of the exception. A
    // form's failure is never answered with this page, which a reload would send again, but with a redirect to /error,
    // whose GET says the same in the past tense with this request's ID.
    internal static class ErrorResponses
    {
        internal const string Unavailable = "The dashboard can't reach its database just now, so nothing was changed. Try again in a few minutes.";
        internal const string Failed = "Something went wrong, and nothing was changed.";
        internal const string Unreadable = "That request was too big, or couldn't be read, so nothing was changed.";

        // A save the database didn't confirm, whose notice's GET can't read its session while the database stays down: the
        // page can't tell whether it was saved
        internal const string UnsureTitle = "Not sure it was saved";
        internal const string UnsureUnavailable = "The dashboard lost touch with its database while it was saving this, and still can't reach it, so it can't tell "
            + "whether it was saved. Reload this page in a few minutes, once the database answers, and look at what it shows, or at the audit log, before you "
            + "send it again.";

        // The error page's words for a form that failed, by /error?failed=. They send the person back to the page the form
        // came from, never to the page opened anew: after a save the database didn't confirm, the form there keeps the key it
        // was sent with, which finds the first request should it have landed, where a page opened anew draws a new key, which
        // would send a second.
        internal const string UnavailableAfterForm = "The dashboard couldn't reach its database when that form was sent, so nothing was changed. In a few minutes, "
            + "go back to the page you sent it from and send it again there.";
        internal const string FailedAfterForm = "Something went wrong when that form was sent, and nothing was changed. Go back to the page you sent it from and send it "
            + "again there.";

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

            var request = context.Request;

            // A form that failed: the error page by GET, which is safe to reload, with this request's ID. A body that couldn't
            // be read keeps its page, since sending it again gets the same answer.
            if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && failure is not BadHttpRequestException)
            {
                context.Response.StatusCode = StatusCodes.Status303SeeOther;
                context.Response.Headers.Location = request.PathBase + "/error?failed=" + (failure is DashboardUnavailableException ? "database" : "error")
                    + "&request=" + Uri.EscapeDataString(context.TraceIdentifier);
                return;
            }

            // The GET of a save the database didn't confirm, whose notice can't be drawn while the database stays down: not
            // "nothing was changed", which may not be true
            if (failure is DashboardUnavailableException && IsUnsure(context))
                (title, message) = (UnsureTitle, UnsureUnavailable);

            context.Response.StatusCode = status;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(Page(context, title, message));
        }

        // Read only, and never failing: this page must always come
        static bool IsUnsure(HttpContext context)
        {
            try
            {
                return context.RequestServices.GetService<FormNotices>()?.IsUnsure(context) == true;
            }
            catch (Exception)
            {
                return false;
            }
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

        // Versioned like the layout's link, so a browser never keeps an old stylesheet. One that can't be read gets the plain
        // link, because the error page must never fail itself. On Linux a file the app's user can't open throws
        // UnauthorizedAccessException, which isn't an IOException: the first image on the server had such a stylesheet,
        // and every page, this one included, answered an empty 500 without a request ID.
        static string Stylesheet(HttpContext context)
        {
            const string path = "/css/site.css";

            try
            {
                return context.RequestServices.GetService<IFileVersionProvider>()?.AddFileVersionToPath(context.Request.PathBase, path) ?? path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return path;
            }
        }
    }
}
