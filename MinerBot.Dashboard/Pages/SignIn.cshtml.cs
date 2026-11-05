using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Auth;

namespace MinerBot.Dashboard.Pages
{
    // A GET everywhere, linked rather than posted: browsers apply the form-action policy to the redirect after a form,
    // and discord.com isn't 'self'
    public sealed class SignInModel : PageModel
    {
        public IActionResult OnGet(string? returnUrl)
        {
            // Only a path on this site, so the sign-in can't be used to send someone elsewhere
            var target = ReturnUrls.Pick(returnUrl);

            if (RoleClaims.RoleOf(User) != DashboardRole.None)
                return LocalRedirect(target);

            return Challenge(new AuthenticationProperties { RedirectUri = target }, DashboardAuth.DiscordScheme);
        }
    }
}
