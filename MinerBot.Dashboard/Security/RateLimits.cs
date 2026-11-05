using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using MinerBot.Dashboard.Auth;

namespace MinerBot.Dashboard.Security
{
    // Limits per visitor address, which is nginx's X-Forwarded-For once the forwarded headers have been applied, and one
    // shared limit on Discord's callback. Every sign-in costs Discord calls from the address the bot also uses, and
    // Discord blocks an address that sends too many bad ones, so sign-ins have the tightest limits.
    internal static class RateLimits
    {
        public const int SignInPerMinute = 10;

        // Every other request, signed in or not. The limit comes before the session is looked up, so a session cookie
        // can't buy a bigger allowance: anyone can send a made-up one, and each costs a database read.
        public const int VisitorPerMinute = 60;

        // Discord's callback, for everyone together, on top of each visitor's own limit. Each callback can cost a call to
        // Discord's token endpoint, and a state from one sign-in can be sent again and again from any number of addresses,
        // so without this the calls would have no ceiling. Only a handful of people sign in, so the worst an attack can do
        // is hold sign-in shut a minute at a time, which is better than Discord banning the address the bot shares.
        public const int CallbacksPerMinute = 20;

        internal const string Rejection = "Too many requests just now. Wait a minute, then try again.";

        static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        public static void Configure(RateLimiterOptions options)
        {
            // The visitor's own limit first, so one address that's over it never uses up the shared one
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(Partition),
                PartitionedRateLimiter.Create<HttpContext, string>(AllCallbacks));

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = OnRejectedAsync;
        }

        static RateLimitPartition<string> AllCallbacks(HttpContext context) =>
            context.Request.Path.StartsWithSegments(DashboardAuth.CallbackPath, StringComparison.OrdinalIgnoreCase)
                ? Fixed("callbacks", CallbacksPerMinute)
                : RateLimitPartition.GetNoLimiter("other");

        static RateLimitPartition<string> Partition(HttpContext context)
        {
            var request = context.Request;

            // The container's own check, which never comes through nginx
            if (ProxyKeyMiddleware.IsLocalHealthCheck(request))
                return RateLimitPartition.GetNoLimiter("health");

            var visitor = Visitor(context.Connection.RemoteIpAddress);

            return IsSignIn(request.Path) ? Fixed("signin " + visitor, SignInPerMinute) : Fixed("visitor " + visitor, VisitorPerMinute);
        }

        // By segments, because routing also takes /signin/ for the sign-in page. Discord's callback is answered only at its
        // exact path, and anything below either one is a 404, which may as well count here too.
        internal static bool IsSignIn(PathString path) =>
            path.StartsWithSegments("/signin", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments(DashboardAuth.CallbackPath, StringComparison.OrdinalIgnoreCase);

        static RateLimitPartition<string> Fixed(string key, int permits) =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = Window,
                QueueLimit = 0
            });

        // An IPv6 visitor is counted by their /64, because one connection usually has the whole /64 to pick addresses from
        internal static string Visitor(IPAddress? address)
        {
            if (address == null)
                return "unknown";

            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            if (address.AddressFamily != AddressFamily.InterNetworkV6)
                return address.ToString();

            var bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return new IPAddress(bytes) + "/64";
        }

        // Answered here with a body, so no status page runs back through the limiter
        static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
        {
            var response = context.HttpContext.Response;
            var wait = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : Window;

            response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            response.ContentType = "text/plain; charset=utf-8";
            await response.WriteAsync(Rejection, cancellationToken);
        }
    }
}
