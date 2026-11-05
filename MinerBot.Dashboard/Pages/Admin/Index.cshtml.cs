using System.Globalization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Auth;

namespace MinerBot.Dashboard.Pages.Admin
{
    // A placeholder overview, until the real one. It reads only the session's claims, so it costs nothing beyond the
    // session lookup every signed-in page makes.
    public sealed class OverviewModel : PageModel
    {
        public string Name { get; private set; } = "";
        public string UserId { get; private set; } = "";
        public string Role { get; private set; } = "";

        public void OnGet()
        {
            var user = SignedInUser.From(User);

            Name = user?.Name ?? "";
            UserId = user?.Id.ToString(CultureInfo.InvariantCulture) ?? "";
            Role = RoleClaims.RoleOf(User).Name();
        }
    }
}
