using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Auth;

namespace MinerBot.Dashboard.Pages
{
    // A GET everywhere, linked rather than posted: browsers apply the form-action policy to the redirect after a form,
    // and discord.com isn't 'self'. fresh=1 is a sign-in again, which the controls that need a sign-in from the last 15
    // minutes link to: it goes to Discord even for someone signed in, and the session ends only once a new one is made.
    public sealed class SignInModel : PageModel
    {
        public IActionResult OnGet(string? returnUrl, string? fresh)
        {
            // Only a path on this site, so the sign-in can't be used to send someone elsewhere
            var target = ReturnUrls.Pick(returnUrl);

            // Any other value of fresh is ignored
            if (RoleClaims.RoleOf(User) != DashboardRole.None && fresh != "1")
                return LocalRedirect(target);

            return Challenge(new AuthenticationProperties { RedirectUri = target }, DashboardAuth.DiscordScheme);
        }
    }
}
