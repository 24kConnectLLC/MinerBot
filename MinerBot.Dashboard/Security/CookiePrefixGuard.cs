using Microsoft.Extensions.Primitives;

namespace MinerBot.Dashboard.Security
{
    // A browser keeps a __Host- cookie only from this very host, so murderminershub.com can't set or replace the session
    // or the sign-in's correlation cookie. Safari, though, checks the prefix with its case, and takes a __HOST-mb that
    // murderminershub.com sets for the whole domain, while ASP.NET Core reads cookie names ignoring case and would take
    // that one as the session, or as the correlation cookie of a sign-in someone else started. Such cookies are dropped
    // here, before anything reads the cookies.
    internal sealed class CookiePrefixGuard(RequestDelegate next)
    {
        public Task InvokeAsync(HttpContext context)
        {
            var headers = context.Request.Headers;

            if (Kept(headers.Cookie) is { } kept)
                headers.Cookie = kept;

            return next(context);
        }

        // The Cookie headers without any cookie whose name has one of the prefixes in another case, or null when there's
        // none to drop. The cookies that stay keep their text exactly.
        internal static StringValues? Kept(StringValues cookieHeaders)
        {
            if (!cookieHeaders.Any(header => header != null && (header.Contains("__host-", StringComparison.OrdinalIgnoreCase)
                    || header.Contains("__secure-", StringComparison.OrdinalIgnoreCase))))
                return null;

            var kept = new List<string>();
            var dropped = false;

            foreach (var header in cookieHeaders)
            {
                if (header == null)
                    continue;

                var cookies = header.Split(';').Where(cookie =>
                {
                    var name = cookie.Split('=', 2)[0].Trim();
                    var impostor = HasPrefixInAnotherCase(name, "__Host-") || HasPrefixInAnotherCase(name, "__Secure-");
                    dropped |= impostor;
                    return !impostor;
                }).ToList();

                if (cookies.Count > 0)
                    kept.Add(string.Join(';', cookies).Trim());
            }

            return dropped ? new StringValues(kept.ToArray()) : (StringValues?)null;
        }

        internal static bool HasPrefixInAnotherCase(string name, string prefix) =>
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !name.StartsWith(prefix, StringComparison.Ordinal);
    }
}
