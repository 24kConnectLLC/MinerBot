using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Settings;

namespace MinerBot.Dashboard.Security
{
    // A change must come from the dashboard's own pages. Browsers count murderminershub.com, where WordPress runs, as the
    // same site, so its pages carry the dashboard's SameSite cookies, and the antiforgery token alone shouldn't have to
    // stop them. This runs before the token is checked, on every method that can change something.
    internal sealed class OriginCheck(RequestDelegate next, IOptions<DashboardOptions> settings, ILogger<OriginCheck> logger)
    {
        internal const string Refusal = "This request came from another site, so the dashboard didn't act on it.";

        readonly string origin = settings.Value.PublicOrigin;

        public Task InvokeAsync(HttpContext context)
        {
            var request = context.Request;

            if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method)
                || HttpMethods.IsTrace(request.Method) || FromOwnPages(request))
                return next(context);

            logger.LogInformation("A {Method} request from another site, or one that didn't say where it came from, was refused", request.Method);

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            return context.Response.WriteAsync(Refusal, context.RequestAborted);
        }

        // An Origin the browser names must be the dashboard's own. Without one, or with null, the browser's Sec-Fetch-Site
        // must say same-origin. same-site is what a page on murderminershub.com sends, so it's refused like cross-site.
        bool FromOwnPages(HttpRequest request)
        {
            var origins = request.Headers.Origin;

            if (origins.Count == 1 && !string.Equals(origins[0], "null", StringComparison.Ordinal))
                return string.Equals(origins[0], origin, StringComparison.OrdinalIgnoreCase);

            if (origins.Count > 1)
                return false;

            var site = request.Headers["Sec-Fetch-Site"];
            return site.Count == 1 && string.Equals(site[0], "same-origin", StringComparison.Ordinal);
        }
    }
}
