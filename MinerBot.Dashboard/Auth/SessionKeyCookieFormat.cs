using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace MinerBot.Dashboard.Auth
{
    // What the session cookie holds: exactly the session's key, rather than the cookie handler's own encrypted ticket
    // around it. The key is 256 random bits and the table holds only its hash, so there's nothing in it to protect.
    internal sealed class SessionKeyCookieFormat : ISecureDataFormat<AuthenticationTicket>
    {
        // The cookie handler's own name for a session store's key
        internal const string SessionIdClaim = "Microsoft.AspNetCore.Authentication.Cookies-SessionId";

        public string Protect(AuthenticationTicket data) => Protect(data, null);

        // Throws rather than write anything else, so a ticket can never end up in the cookie
        public string Protect(AuthenticationTicket data, string? purpose) =>
            data.Principal.FindFirst(SessionIdClaim)?.Value is { } key && SessionKeys.IsWellFormed(key)
                ? key
                : throw new InvalidOperationException("Only a session's key goes into the cookie, and this ticket has none");

        public AuthenticationTicket? Unprotect(string? protectedText) => Unprotect(protectedText, null);

        public AuthenticationTicket? Unprotect(string? protectedText, string? purpose)
        {
            if (!SessionKeys.IsWellFormed(protectedText))
                return null;

            var identity = new ClaimsIdentity([new Claim(SessionIdClaim, protectedText!)], DashboardAuth.CookieScheme);
            return new AuthenticationTicket(new ClaimsPrincipal(identity), DashboardAuth.CookieScheme);
        }
    }
}
