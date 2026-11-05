using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace MinerBot.Dashboard.Auth
{
    // The only source of roles. It runs on every authentication, from the lists as they are on that request, and its
    // output is never stored, so a role is never in a ticket and a change to the lists applies at once.
    internal sealed class RoleClaims(RoleLists roles) : IClaimsTransformation
    {
        // Marks the identity this adds, so running twice adds nothing more
        const string Label = "mb:roles";

        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            if (principal.Identities.Any(identity => identity.Label == Label))
                return Task.FromResult(principal);

            var role = ListedRole(SignedInUser.From(principal));

            if (role == DashboardRole.None)
                return Task.FromResult(principal);

            // A copy, so the input never changes. The role's identity has no authentication type, so antiforgery's token,
            // which is bound to the signed-in identity's claims, ignores it.
            var withRole = principal.Clone();
            withRole.AddIdentity(new ClaimsIdentity([new Claim(ClaimTypes.Role, role.Word())]) { Label = Label });
            return Task.FromResult(withRole);
        }

        // Only with two-factor on, whatever the lists say
        DashboardRole ListedRole(SignedInUser? user) => user is { MfaEnabled: true } ? roles.RoleOf(user.Id) : DashboardRole.None;

        // The role this request's user has, from the claims added above
        public static DashboardRole RoleOf(ClaimsPrincipal? user)
        {
            if (user?.IsInRole(DashboardRole.Owner.Word()) == true)
                return DashboardRole.Owner;

            return user?.IsInRole(DashboardRole.Admin.Word()) == true ? DashboardRole.Admin : DashboardRole.None;
        }
    }
}
