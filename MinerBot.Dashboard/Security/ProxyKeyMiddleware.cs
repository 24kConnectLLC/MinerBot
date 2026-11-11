using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MinerBot.Dashboard.Health;
using MinerBot.Dashboard.Settings;

namespace MinerBot.Dashboard.Security
{
    // On the server Kestrel listens on a socket that only processes in the group nobody can open, cPanel's Apache as well as
    // nginx's workers. On loopback, which a local run uses, the PHP of every site on a server could reach it too. Either way
    // only nginx knows this key, so anything else that calls the dashboard directly gets a bare 404, as if nothing were there.
    internal sealed class ProxyKeyMiddleware(RequestDelegate next, IOptions<DashboardOptions> settings, ILogger<ProxyKeyMiddleware> logger)
    {
        public const string HeaderName = "X-MinerBot-Proxy-Key";

        // Both sides are hashed first, so they have the same length and neither the content nor the length leaks
        readonly byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes(settings.Value.ProxyKey));

        public Task InvokeAsync(HttpContext context)
        {
            // Taken off first, so no later code, page or log can show it
            var sent = context.Request.Headers[HeaderName];
            context.Request.Headers.Remove(HeaderName);

            if (IsLocalHealthCheck(context.Request) || Matches(sent))
                return next(context);

            logger.LogDebug("A request without the proxy key was answered with 404");
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        bool Matches(StringValues sent) =>
            sent.Count == 1 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(sent[0] ?? "")), expected);

        // The container's own health check, which comes from inside it without nginx. The endpoints also require Host
        // localhost, so sending the key doesn't make them public.
        internal static bool IsLocalHealthCheck(HttpRequest request) =>
            string.Equals(request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            && (request.Path.Equals(HealthEndpoints.LivePath, StringComparison.OrdinalIgnoreCase) || request.Path.Equals(HealthEndpoints.ReadyPath, StringComparison.OrdinalIgnoreCase));
    }
}
