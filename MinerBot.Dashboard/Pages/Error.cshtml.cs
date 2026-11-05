using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MinerBot.Dashboard.Pages
{
    // The page for 404, 403, 400 and 405, shown again for any method, so it skips the antiforgery check. It says what
    // happened from the status alone, with the request ID to quote, and never anything of an exception.
    [IgnoreAntiforgeryToken]
    public sealed class ErrorModel : PageModel
    {
        public string Heading => Status switch
        {
            StatusCodes.Status404NotFound => "Page not found",
            StatusCodes.Status403Forbidden or StatusCodes.Status405MethodNotAllowed => "Not allowed",
            StatusCodes.Status400BadRequest => "Form expired",
            _ => "Something went wrong"
        };

        public string Message => Status switch
        {
            StatusCodes.Status404NotFound => "There's no page at that address.",
            StatusCodes.Status403Forbidden => "You can't open that page.",
            StatusCodes.Status400BadRequest => "That form had expired or was incomplete. Go back, reload the page and try again.",
            StatusCodes.Status405MethodNotAllowed => "That page can't take that kind of request.",
            _ => "Something went wrong."
        };

        public string RequestId => HttpContext.TraceIdentifier;

        // Worked out as the page renders, since a page shown again for a POST runs no handler
        int Status => HttpContext.Features.Get<IStatusCodeReExecuteFeature>()?.OriginalStatusCode ?? HttpContext.Response.StatusCode;
    }
}
