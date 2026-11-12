using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using MinerBot.Shared.Armor;

namespace MinerBot.Dashboard.Security
{
    // The browser's half of the security: what may run, load, frame or keep each response. Set as the response starts, so
    // they survive the exception handler clearing the response, and nothing later can drop them.
    internal sealed class SecurityHeaders(RequestDelegate next)
    {
        // Scripts and styles only from the dashboard's own files. Images also from Discord's CDN, for avatars and server
        // icons, and from the website's uploads folder, where armor screenshots are allowed, which MinerBot.Shared keeps in
        // one place. A source ending in / covers only the paths under it, so the browser won't load anything else of the
        // website's, which would carry the reviewer's WordPress cookies.
        internal static readonly string ContentSecurityPolicy =
            "default-src 'none'; script-src 'self'; style-src 'self'; " +
            $"img-src 'self' https://cdn.discordapp.com {string.Join(' ', ArmorRules.ScreenshotHosts.Select(host => "https://" + host + ArmorRules.ScreenshotPath))}; " +
            "connect-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'; object-src 'none'";

        internal const string NoTransform = "no-transform";

        // Every response gets exactly these
        internal static readonly IReadOnlyDictionary<string, string> Fixed = new Dictionary<string, string>
        {
            [HeaderNames.ContentSecurityPolicy] = ContentSecurityPolicy,
            // HSTS from here rather than UseHsts, which skips localhost, and nginx and Cloudflare already serve only https
            [HeaderNames.StrictTransportSecurity] = "max-age=31536000",
            [HeaderNames.XContentTypeOptions] = "nosniff",
            // Same-origin rather than no-referrer, because under no-referrer browsers send Origin: null with every form
            // post, and OriginCheck needs the real one. Other sites, Discord and the image hosts still get nothing.
            ["Referrer-Policy"] = "same-origin",
            [HeaderNames.XFrameOptions] = "DENY",
            ["Cross-Origin-Opener-Policy"] = "same-origin",
            ["Cross-Origin-Resource-Policy"] = "same-origin",
            ["Permissions-Policy"] = "camera=(), geolocation=(), microphone=(), payment=(), usb=()",
            ["X-Robots-Tag"] = "noindex, nofollow"
        };

        public Task InvokeAsync(HttpContext context)
        {
            context.Response.OnStarting(Apply, context.Response);
            return next(context);
        }

        static Task Apply(object state)
        {
            var headers = ((HttpResponse)state).Headers;

            foreach (var (name, value) in Fixed)
                headers[name] = value;

            // Nothing dynamic is stored anywhere. A Cache-Control that's already set is kept: the exception handler's and the
            // cookie handler's say no-store too, and the stylesheet's may be cached because its links carry a version.
            // Every response also gets no-transform, which Cloudflare's documentation says makes it leave a response
            // uncompressed. The pages hold secrets next to text visitors control, which is why Program.cs leaves
            // compression out, and nginx has gzip off for the same reason.
            var cacheControl = headers.CacheControl;

            if (StringValues.IsNullOrEmpty(cacheControl))
                headers.CacheControl = "no-store, " + NoTransform;
            else if (!HasDirective(cacheControl, NoTransform))
                headers.CacheControl = string.Join(", ", cacheControl.Where(value => !string.IsNullOrWhiteSpace(value))) + ", " + NoTransform;

            // Kestrel's own Server header is off at the source, and these are only in case anything else sets them
            headers.Remove(HeaderNames.Server);
            headers.Remove(HeaderNames.XPoweredBy);

            return Task.CompletedTask;
        }

        // Whether a Cache-Control, in one header or several, already has the directive
        static bool HasDirective(StringValues cacheControl, string directive) =>
            cacheControl.Any(value => value != null && value.Split(',').Any(part => part.Trim().Equals(directive, StringComparison.OrdinalIgnoreCase)));
    }
}
