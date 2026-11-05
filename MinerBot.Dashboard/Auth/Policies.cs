using Microsoft.AspNetCore.Authorization;

namespace MinerBot.Dashboard.Auth
{
    // Everything under /Admin needs Admin, which Owners pass too. An Owner-only page adds
    // [Authorize(Policy = Policies.Owner)] to its PageModel, on top of the folder's policy, and both must pass.
    internal static class Policies
    {
        public const string Admin = "Admin";
        public const string Owner = "Owner";

        public static void Add(AuthorizationBuilder authorization) => authorization
            .AddPolicy(Admin, policy => policy.RequireAuthenticatedUser().RequireRole(DashboardRole.Owner.Word(), DashboardRole.Admin.Word()))
            .AddPolicy(Owner, policy => policy.RequireAuthenticatedUser().RequireRole(DashboardRole.Owner.Word()));
    }
}
