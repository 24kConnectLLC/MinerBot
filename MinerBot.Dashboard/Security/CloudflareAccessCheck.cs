using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Settings;

namespace MinerBot.Dashboard.Security
{
    // With Cloudflare Access on, an admin page needs the signed token Cloudflare adds to each request it let through. Only a
    // check here still holds if a request ever reaches nginx without passing Access, such as through another Cloudflare
    // zone pointed at this server. It runs after routing, so it sees the page's policy, and before sign-in, so a refusal
    // costs no session lookup. With the settings empty it only takes the header off.
    internal sealed class CloudflareAccessCheck(RequestDelegate next, IOptions<CloudflareAccessOptions> access, IOptions<DashboardOptions> dashboard,
        CloudflareAccessKeys keys, TimeProvider time, ILogger<CloudflareAccessCheck> logger)
    {
        public const string HeaderName = "Cf-Access-Jwt-Assertion";

        // The path Cloudflare's rule covers
        internal const string AdminPath = "/admin";

        internal const string Unavailable = "Cloudflare Access couldn't be checked just now, so this page isn't shown. Try again in a minute.";

        internal const int RetryAfterSeconds = 60;

        readonly CloudflareAccessOptions settings = access.Value;

        // On the address people open, so it works from wherever the request came
        readonly string refusal = $"This part of the dashboard is behind Cloudflare Access, and this request didn't come through it. Open {dashboard.Value.PublicOrigin}{AdminPath} and sign in with Cloudflare first.";

        public async Task InvokeAsync(HttpContext context)
        {
            // Taken off every request first, guarded or not, so no later code, page or log can show it
            var sent = context.Request.Headers[HeaderName];
            context.Request.Headers.Remove(HeaderName);

            if (!settings.Enabled || !Guards(context))
            {
                await next(context);
                return;
            }

            var (problem, keysUnavailable) = await ProblemAsync(sent, context.RequestAborted);

            if (problem == null && !keysUnavailable)
            {
                await next(context);
                return;
            }

            // Written here, so the status pages don't run again. SecurityHeaders adds no-store.
            var response = context.Response;
            response.ContentType = "text/plain; charset=utf-8";

            if (keysUnavailable)
            {
                logger.LogInformation("A request for an admin page wasn't shown, because the Cloudflare Access check has no usable keys (request {RequestId})", context.TraceIdentifier);
                response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                await response.WriteAsync(Unavailable, context.RequestAborted);
                return;
            }

            logger.LogInformation("A request for an admin page was refused by the Cloudflare Access check: {Reason} (request {RequestId})", problem, context.TraceIdentifier);
            response.StatusCode = StatusCodes.Status403Forbidden;
            await response.WriteAsync(refusal, context.RequestAborted);
        }

        // The rules in order: exactly one header value, the token's header, the key it names, then everything else. The
        // reason when one fails, or that no keys are usable.
        async Task<(string? Problem, bool KeysUnavailable)> ProblemAsync(StringValues sent, CancellationToken cancellationToken)
        {
            if (StringValues.IsNullOrEmpty(sent))
                return (CloudflareAccessToken.Missing, false);

            if (sent.Count > 1)
                return (CloudflareAccessToken.MoreThanOne, false);

            var token = sent[0]!;

            if (!CloudflareAccessToken.TryReadHeader(token, out var kid, out var problem))
                return (problem, false);

            var (lookup, key) = await keys.FindAsync(kid, cancellationToken);

            return lookup switch
            {
                CloudflareAccessKeys.KeyLookup.Found => (CloudflareAccessToken.Problem(token, key!, settings.Issuer, settings.Audience, time.GetUtcNow()), false),
                CloudflareAccessKeys.KeyLookup.Unknown => (CloudflareAccessToken.UnknownKey, false),
                _ => (null, true)
            };
        }

        // A path under /admin in any letter case, or a page that asks for the Admin or Owner policy wherever it is.
        // Together they cover /ADMIN and /%61dmin, however Cloudflare's path rule reads them. Pages for another policy
        // outside /admin, where Cloudflare adds no token, aren't guarded.
        internal static bool Guards(HttpContext context) =>
            context.Request.Path.StartsWithSegments(AdminPath, StringComparison.OrdinalIgnoreCase)
            || context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(data => data.Policy is Policies.Admin or Policies.Owner) == true;

        // Said once as the dashboard starts, so its log shows whether admin pages need Access too
        internal static void LogState(IServiceProvider services)
        {
            var settings = services.GetRequiredService<IOptions<CloudflareAccessOptions>>().Value;
            var logger = services.GetRequiredService<ILogger<CloudflareAccessCheck>>();

            if (settings.Enabled)
                logger.LogInformation("The Cloudflare Access check is on for {TeamDomain}", settings.TeamDomain);
            else
                logger.LogInformation("The Cloudflare Access check is off");
        }
    }
}
