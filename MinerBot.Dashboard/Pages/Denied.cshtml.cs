using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Auth;

namespace MinerBot.Dashboard.Pages
{
    // Why a sign-in ended without a session, in plain words, and what to do. The reason is mapped to one of a fixed set
    // and never shown as it came, so an address someone crafted can't put words on the page.
    public sealed class DeniedModel : PageModel
    {
        public string Heading { get; private set; } = "";
        public string Message { get; private set; } = "";
        public bool OfferSignIn { get; private set; }

        public void OnGet(string? reason)
        {
            var refusal = SignInRefusals.FromWord(reason);

            Heading = refusal == SignInRefusal.None ? "You can't open that page" : "You weren't signed in";

            Message = refusal switch
            {
                SignInRefusal.Unlisted => "Your Discord account isn't on the dashboard's list, so you weren't signed in. Only the people who run MinerBot can use the dashboard. If you should be one of them, ask the owner to add your Discord user ID.",
                SignInRefusal.NoTwoFactor => "Your Discord account doesn't have two-factor authentication turned on, and the dashboard needs it. Turn it on in Discord under User Settings, My Account, then sign in again.",
                SignInRefusal.WrongScope => "Discord gave the dashboard different permissions from the ones it asked for, so the sign-in was stopped. Try again, and if it keeps happening, tell the owner.",
                SignInRefusal.Cancelled => "You cancelled the sign-in at Discord, so nothing changed.",
                SignInRefusal.Failed => "The sign-in with Discord didn't finish. Discord may be busy, or the sign-in took too long. Wait a minute and try again.",
                _ => "You can't open that page."
            };

            // Signing in again can't help someone who isn't on the list
            OfferSignIn = refusal is not (SignInRefusal.None or SignInRefusal.Unlisted);
        }
    }
}
