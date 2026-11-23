using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Security;

namespace MinerBot.Dashboard.Pages
{
    // The page for 404, 403, 400 and 405, shown again for any method, so it skips the antiforgery check. It says what
    // happened from the status alone, with the request ID to quote, and never anything of an exception. A form that failed
    // or went over the rate limit is sent here by GET, as ?failed= with the word for why, so reloading it sends nothing: it
    // says what happened to the form, with the status it would have had, and for a form that failed, the ID its error was
    // logged with. The rate limiter logs nothing and sends no ID, so a form over the limit shows this request's own.
    [IgnoreAntiforgeryToken]
    public sealed partial class ErrorModel : PageModel
    {
        // ?failed=, as ErrorResponses and RateLimits send it
        public const string Database = "database";
        public const string Failure = "error";
        public const string Limit = "limit";

        // Why a form wasn't sent, or null on a status page
        public string? Failed { get; private set; }

        string? failedRequest;

        public void OnGet([FromQuery] string? failed, [FromQuery] string? request)
        {
            // A status page shown again for a GET runs this too, and says what its status alone says
            if (HttpContext.Features.Get<IStatusCodeReExecuteFeature>() != null || failed is not (Database or Failure or Limit))
                return;

            Failed = failed;
            Response.StatusCode = failed switch
            {
                Database => StatusCodes.Status503ServiceUnavailable,
                Limit => StatusCodes.Status429TooManyRequests,
                _ => StatusCodes.Status500InternalServerError
            };

            if (failed == Limit)
                Response.Headers.RetryAfter = ((int)RateLimits.Window.TotalSeconds).ToString(CultureInfo.InvariantCulture);

            // The ID the failed request was logged with, when it looks like one, and otherwise this request's own
            if (request != null && RequestIdForm().IsMatch(request))
                failedRequest = request;
        }

        public string Heading => Failed switch
        {
            Database => "Temporarily unavailable",
            Failure => "Something went wrong",
            Limit => "Too many requests",
            _ => Status switch
            {
                StatusCodes.Status404NotFound => "Page not found",
                StatusCodes.Status403Forbidden or StatusCodes.Status405MethodNotAllowed => "Not allowed",
                StatusCodes.Status400BadRequest => "Form expired",
                _ => "Something went wrong"
            }
        };

        public string Message => Failed switch
        {
            Database => ErrorResponses.UnavailableAfterForm,
            Failure => ErrorResponses.FailedAfterForm,
            Limit => RateLimits.RejectedForm,
            _ => Status switch
            {
                StatusCodes.Status404NotFound => "There's no page at that address.",
                StatusCodes.Status403Forbidden => "You can't open that page.",
                StatusCodes.Status400BadRequest => "That form had expired or was incomplete. Go back, reload the page and try again.",
                StatusCodes.Status405MethodNotAllowed => "That page can't take that kind of request.",
                _ => "Something went wrong."
            }
        };

        public string RequestId => failedRequest ?? HttpContext.TraceIdentifier;

        // Worked out as the page renders, since a page shown again for a POST runs no handler
        int Status => HttpContext.Features.Get<IStatusCodeReExecuteFeature>()?.OriginalStatusCode ?? HttpContext.Response.StatusCode;

        // Kestrel's request IDs, such as 0HNA1B2C3D4E5:00000001: the connection's 13 characters, 0 to 9 and A to V,
        // then the request's number on it in 8 hex digits. The test host's are the 13 alone. Nothing else is shown, so a
        // link someone crafted can't put a host name, a phrase or a line break on the page.
        [GeneratedRegex(@"^[0-9A-V]{13}(?::[0-9A-F]{8})?\z", RegexOptions.CultureInvariant)]
        private static partial Regex RequestIdForm();
    }
}
