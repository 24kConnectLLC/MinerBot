using Microsoft.AspNetCore.Authentication;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Auth
{
    // A sign-in from the last 15 minutes, which restarting the bot, pausing posts, the Steam controls, a status text,
    // leaving a server and ending sessions need, so a stolen session cookie that's older can't do them. The time is the
    // session row's created_at, which RetrieveAsync puts on every request as the ticket's IssuedUtc: the browser holds only
    // the session's key, so it can't come from there, and a new sign-in always makes a new row. Checked when the form is
    // sent, with the dashboard's clock; a page only uses it to decide what to draw.
    internal static class FreshSignIn
    {
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

        // The session row's created_at, as RetrieveAsync put it on this request, or null
        public static DateTimeOffset? SignedInAt(HttpContext context) =>
            context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Properties?.IssuedUtc;

        // Fails closed: no time, a time ahead of the clock, or one 15 minutes old or more. The ticket keeps the time in whole
        // seconds, the part of a second cut off, so the window can end up to a second early, never late.
        public static bool IsFresh(HttpContext context, DateTimeOffset now) =>
            SignedInAt(context) is { } at && at <= now && now - at < Window;

        // When the sign-in stops being fresh enough, for a page's "these work until" line
        public static DateTimeOffset? Until(HttpContext context) => SignedInAt(context) + Window;

        // The requests that need one: the plan's restart, leave, pause and Steam controls, and a status text, which every
        // server shows under MinerBot's name. Back to the default status, resuming posts, the jobs, the cache clear and the
        // Admins' kinds don't: they're the safe direction, or do nothing the schedule doesn't. One list in the dashboard, not
        // MinerBot.Shared's, which would make it a bot change.
        public static bool Needs(RequestKind kind, string? argument) => kind.Word switch
        {
            RequestKinds.BotRestart or RequestKinds.GuildLeave or RequestKinds.PostsPause or RequestKinds.SteamReconnect or RequestKinds.SteamRenew => true,
            RequestKinds.StatusSet => argument != null,
            _ => false
        };

        // A link, never a form: browsers apply form-action 'self' to the redirect after a form, and discord.com isn't 'self'.
        // The way back is a path on this site, its anchor included, which /signin checks again.
        public static string SignInAgainLink(string returnPath) => "/signin?fresh=1&returnUrl=" + Uri.EscapeDataString(returnPath);
    }
}
