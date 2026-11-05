using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Auth;

namespace MinerBot.Dashboard.Pages
{
    // A POST, which OriginCheck has already seen come from the dashboard's own pages. The antiforgery token is checked
    // here rather than by Razor Pages, because it's tied to the person it was made for: once their session has ended,
    // after 30 minutes unused for one, the form they still have would be refused and the dead cookie would stay.
    [IgnoreAntiforgeryToken]
    public sealed class SignOutModel : PageModel
    {
        public bool SignedIn { get; private set; }

        public void OnGet() => SignedIn = RoleClaims.RoleOf(User) != DashboardRole.None;

        public async Task<IActionResult> OnPostAsync()
        {
            // A session that's still there ends only with the token from its own form. The cookie handler deletes a row
            // only when it found that session for this request, so for anyone else there's nothing a forged sign-out could
            // end, and all it does is delete their cookie.
            if (User.Identity?.IsAuthenticated == true)
            {
                if (!await HttpContext.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(HttpContext))
                    return BadRequest();

                // The session and its audit row go together
                if (SessionKeys.Current(HttpContext) is { } key && RoleClaims.RoleOf(User) != DashboardRole.None)
                    await HttpContext.RequestServices.GetRequiredService<DbTicketStore>().EndAsync(key, User, HttpContext);
            }

            // Deletes the cookie. The ticket store finds nothing left to delete.
            await HttpContext.SignOutAsync(DashboardAuth.CookieScheme);
            return LocalRedirect("/");
        }
    }
}
