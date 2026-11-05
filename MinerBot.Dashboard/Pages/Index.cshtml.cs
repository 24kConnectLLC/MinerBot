using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Auth;

namespace MinerBot.Dashboard.Pages
{
    // The public home page: what MinerBot is, and a way in for the people who run it
    public sealed class IndexModel : PageModel
    {
        public bool SignedIn => RoleClaims.RoleOf(User) != DashboardRole.None;
    }
}
