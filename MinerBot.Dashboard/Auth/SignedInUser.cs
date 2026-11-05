using System.Globalization;
using System.Security.Claims;

namespace MinerBot.Dashboard.Auth
{
    // Who signed in, as the sign-in found them: the numeric Discord ID, which is their identity, the name to show, and
    // whether two-factor was on. These three are all a session's ticket ever holds.
    internal sealed record SignedInUser(ulong Id, string Name, bool MfaEnabled)
    {
        // From the identity the sign-in made, or null for anyone else
        public static SignedInUser? From(ClaimsPrincipal? principal)
        {
            var identity = principal?.Identities.FirstOrDefault(identity => identity.IsAuthenticated);
            var id = identity?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (identity == null || !ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var userId) || userId == 0)
                return null;

            return new SignedInUser(userId, identity.FindFirst(ClaimTypes.Name)?.Value ?? "", identity.FindFirst(DashboardAuth.MfaClaim)?.Value == "true");
        }

        // Never a role, which is worked out on every request, and never a token
        public ClaimsPrincipal ToPrincipal() => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Id.ToString(CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, Name),
            new Claim(DashboardAuth.MfaClaim, MfaEnabled ? "true" : "false")
        ], DashboardAuth.DiscordScheme));
    }
}
