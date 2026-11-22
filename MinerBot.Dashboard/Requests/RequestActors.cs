using MinerBot.Dashboard.Auth;

namespace MinerBot.Dashboard.Requests
{
    // Who a page's request is from, always worked out here: the signed-in person's Discord ID and name, from the session's
    // ticket, and the role the lists give them on this request, never one stored anywhere
    internal static class RequestActors
    {
        // Null for someone who isn't signed in, whom no page that sends a request lets in
        public static RequestActor? Of(HttpContext context) =>
            SignedInUser.From(context.User) is { } user ? new RequestActor(user.Id, user.Name, RoleClaims.RoleOf(context.User).Word()) : null;
    }
}
