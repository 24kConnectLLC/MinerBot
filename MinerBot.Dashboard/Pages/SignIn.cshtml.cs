using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Auth;

namespace MinerBot.Dashboard.Pages
{
    // A GET everywhere, linked rather than posted: browsers apply the form-action policy to the redirect after a form,
    // and discord.com isn't 'self'. fresh=1 is a sign-in again, which the controls that need a sign-in from the last 15
    // minutes link to: it goes to Discord even for someone signed in, and the session ends only once a new one is made.
    // ended=1 is where a form sent after its session ended lands (SessionEnded): for someone not signed in, the page that
    // says nothing was done, with a link to sign in again, and no challenge, so no correlation cookie and no call to
    // Discord until they follow it. Someone signed in never sees it.
    public sealed class SignInModel : PageModel
    {
        // The ended page's way to sign in again, back to the page the form was on; null on every other GET
        public string? EndedLink { get; private set; }

        public IActionResult OnGet(string? returnUrl, string? fresh, string? ended)
        {
            // Only a path on this site, so the sign-in can't be used to send someone elsewhere
            var target = ReturnUrls.Pick(returnUrl);
            var signedIn = RoleClaims.RoleOf(User) != DashboardRole.None;

            // Any other value of fresh is ignored
            if (signedIn && fresh != "1")
                return LocalRedirect(target);

            // Any other value of ended is ignored too
            if (!signedIn && ended == "1")
            {
                EndedLink = "/signin?returnUrl=" + Uri.EscapeDataString(target);
                return Page();
            }

            return Challenge(new AuthenticationProperties { RedirectUri = target }, DashboardAuth.DiscordScheme);
        }
    }
}
