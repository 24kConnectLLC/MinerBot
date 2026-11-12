using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Auth;

namespace MinerBot.Dashboard.Pages
{
    // The public home page: what MinerBot is, and a way in for the people who run it. The deploy's readiness check asks for
    // it signed out, through the socket with the proxy key, and needs 200 (home_page_status in deploy/common.sh), from every
    // dashboard a deploy, a rollback or setup starts. So it must keep answering a visitor who isn't signed in with the page
    // itself; deploy/README.md's settings rule says how to change that request.
    public sealed class IndexModel : PageModel
    {
        public bool SignedIn => RoleClaims.RoleOf(User) != DashboardRole.None;
    }
}
