using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Extensions;

namespace MinerBot.Dashboard.Auth
{
    // A form sent without a session, once its session has ended: the cookie handler's challenge for any method but GET and
    // HEAD. Its default answer sends it to /signin, which goes on to Discord, but browsers apply form-action 'self' to every
    // redirect after a form, and discord.com isn't 'self', so that hop is blocked and the press seems to do nothing. This
    // ends it on this site's own page instead, by GET, whose link onward isn't held to that rule. Authorization comes before
    // the page and its antiforgery check, so nothing the form held is read: it's dropped here, never kept, and never put in
    // an address, which nginx's log, the address bar and the history would keep.
    internal static class SessionEnded
    {
        // /signin's parameter for the page that says so
        public const string Parameter = "ended";

        // The page the form was on, for the way back after signing in: its path and query without the form's handler, so it
        // names a page, never a form. Only a path on this site.
        public static string ReturnPath(HttpRequest request)
        {
            var query = new QueryBuilder(request.Query.Where(field => !string.Equals(field.Key, "handler", StringComparison.OrdinalIgnoreCase)));
            return ReturnUrls.Pick((request.PathBase + request.Path).ToUriComponent() + query.ToQueryString());
        }

        // 303 See Other, which makes any method a GET, to the sign-in page on this site with ended=1 and the way back
        public static Task RedirectAsync(RedirectContext<CookieAuthenticationOptions> context)
        {
            var request = context.Request;
            context.Response.StatusCode = StatusCodes.Status303SeeOther;
            context.Response.Headers.Location = request.PathBase + context.Options.LoginPath + QueryString.Create(
            [
                new KeyValuePair<string, string?>(Parameter, "1"),
                new KeyValuePair<string, string?>(context.Options.ReturnUrlParameter, ReturnPath(request))
            ]);

            return Task.CompletedTask;
        }
    }
}
