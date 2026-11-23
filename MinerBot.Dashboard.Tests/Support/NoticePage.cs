using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Hosting;

namespace MinerBot.Dashboard.Tests.Support
{
    // A page as FormNotices sees one, without the app: a request at the path given, of the session whose key is given, with
    // the browser's notice cookie when there is one. Its address is the path with its values as the query, as Url.Page
    // gives a page whose route has no parameters. What the page answered with is on its response.
    internal sealed class NoticePage : PageModel
    {
        public static NoticePage At(string path, string? sessionKey, string? notice = null)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = path;

            if (notice != null)
                context.Request.Headers.Cookie = DashboardAuth.NoticeCookie + "=" + notice;

            if (sessionKey != null)
                SessionKeys.Remember(context, sessionKey);

            return new NoticePage
            {
                PageContext = new PageContext { HttpContext = context, ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()) },
                Url = new PageAddress(context, path)
            };
        }

        // Whether the layout would say a notice has gone
        public bool Gone => ViewData[FormNotices.GoneKey] is true;

        // The Set-Cookie headers of the page's answer for the notice: the one that keeps it, and whether one deletes it
        public string? SetNotice => SetCookies.SingleOrDefault(cookie => cookie.StartsWith(DashboardAuth.NoticeCookie + "=", StringComparison.Ordinal)
            && !cookie.StartsWith(DashboardAuth.NoticeCookie + "=;", StringComparison.Ordinal));

        public bool DeletesNotice => SetCookies.Any(cookie => cookie.StartsWith(DashboardAuth.NoticeCookie + "=;", StringComparison.Ordinal));

        public bool TouchesNotice => SetCookies.Any(cookie => cookie.StartsWith(DashboardAuth.NoticeCookie + "=", StringComparison.Ordinal));

        // The cookie's value, as the browser would send it back
        public string? NoticeValue => SetNotice?.Split(';')[0][(DashboardAuth.NoticeCookie.Length + 1)..];

        IEnumerable<string> SetCookies => HttpContext.Response.Headers.SetCookie.Where(cookie => cookie != null).Select(cookie => cookie!);

        // The page's own address, with its values as the query, leaving out the page and a handler, as Url.Page does
        sealed class PageAddress(HttpContext context, string path) : IUrlHelper
        {
            public ActionContext ActionContext { get; } = new(context, new RouteData(), new ActionDescriptor());

            public string? RouteUrl(UrlRouteContext routeContext) =>
                QueryHelpers.AddQueryString(path, new RouteValueDictionary(routeContext.Values)
                    .Where(value => value.Key is not ("page" or "handler") && value.Value != null)
                    .Select(value => KeyValuePair.Create(value.Key, (string?)Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture))));

            public string? Action(UrlActionContext actionContext) => throw new NotSupportedException();
            public string? Content(string? contentPath) => throw new NotSupportedException();
            public bool IsLocalUrl(string? url) => throw new NotSupportedException();
            public string? Link(string? routeName, object? values) => throw new NotSupportedException();
        }
    }
}
