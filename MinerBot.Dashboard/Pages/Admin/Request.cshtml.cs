using System.Globalization;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Requests.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Security;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Pages.Admin
{
    // One request to the bot: what was asked, by whom, and what the bot did, in its result box, which refresh.js gets again
    // from the Result handler while it can change, without the line that says to reload the page that sent it to see the
    // change, which this page doesn't show. The Owner and the Admins may open any request: all of it but the bot's
    // answer is on the audit page for both already, and its effects are among the Bot page's events. Never an address, the
    // request's key or its signature, which the reads never give, and never a stored status text the rules now refuse, as
    // the bot never shows one. Read-only.
    public sealed class BotRequestPageModel(IBotGuildReads guilds, ILogger<BotRequestPageModel> logger) : PageModel
    {
        public RequestPageRow Row { get; private set; } = null!;
        public RequestView View { get; private set; } = null!;

        // The channel's name in MinerBot's copy, null when the copy doesn't have it, or has it as a channel the bot can't see
        public string? ChannelName { get; private set; }

        // The request's text as this page shows it: a pause's reason, a lookup's name, or a status text the rules pass today.
        // For a status text they refuse, only the rule it breaks.
        public string? Text { get; private set; }
        public string? TextRefused { get; private set; }

        // The page the request was asked from: Bot controls, the server's page, the blacklist, or for a sign-in's message the
        // Sessions page for the Owner and the audit log for an Admin
        public string BackLink { get; private set; } = "";
        public string BackText { get; private set; } = "";

        // The number of a request MinerBot's database doesn't have, for the page's own words
        public ulong? Gone { get; private set; }

        public async Task<IActionResult> OnGetAsync(long id)
        {
            if (await LoadAsync(id) is not { } box)
            {
                if (id <= 0)
                    return NotFound();

                // The nightly clean-up deletes a request after 90 days, but its audit row, which links here, stays 2 years, so
                // the 404 says so, rather than that there's nothing at this address
                Gone = (ulong)id;
                Response.StatusCode = StatusCodes.Status404NotFound;
                return Page();
            }

            var row = Row = box.Row;
            View = RequestViews.OnItsOwnPage(box.View);
            var kind = RequestKinds.Find(row.kind);

            if (row.argument is { } argument)
            {
                if (kind?.Argument == RequestArgument.StatusText && RequestRules.StatusTextProblem(argument) is { } problem)
                    TextRefused = problem;
                else
                    Text = argument;
            }

            if (row.channel_id is { } channel)
            {
                var found = await DatabaseCalls.Guard(() => guilds.FindChannels([channel]), logger);
                ChannelName = found.FirstOrDefault(copy => copy.channel_id == channel && copy.can_view)?.name;
            }

            (BackLink, BackText) = kind?.Word switch
            {
                RequestKinds.GuildTestMessage or RequestKinds.GuildRefresh or RequestKinds.GuildLeave when row.guild_id is { } guild =>
                    ("/admin/servers/" + guild.ToString(CultureInfo.InvariantCulture), "Back to the server's page"),
                RequestKinds.SteamLookup => ("/admin/blacklist", "Back to the blacklist"),
                RequestKinds.OwnerSigninAlert when RoleClaims.RoleOf(User) == DashboardRole.Owner => ("/admin/sessions", "Back to Sessions and security"),
                RequestKinds.OwnerSigninAlert or RequestKinds.GuildTestMessage or RequestKinds.GuildRefresh or RequestKinds.GuildLeave or null =>
                    ("/admin/audit", "Back to the audit log"),
                _ => ("/admin/bot/controls", "Back to Bot controls")
            };

            return Page();
        }

        // The result box alone, for refresh.js. Its marker, and when to look next or that what it shows is final, are set on
        // the response itself and only once the read has succeeded, so a 404 has neither, and an error page, which the
        // exception handler writes on a cleared response, never does. own=1 is the box on this page, which leaves out what
        // only the page that sent the request needs; any other value is the box there.
        public async Task<IActionResult> OnGetResultAsync(long id, string? own)
        {
            if (await LoadAsync(id) is not { } box)
                return NotFound();

            Response.Headers[BackgroundRefresh.FragmentHeader] = RequestViews.Fragment;
            Response.Headers[BackgroundRefresh.NextHeader] = box.View.Final ? BackgroundRefresh.NextEnd : box.View.Next.ToString(CultureInfo.InvariantCulture);
            return Partial("_RequestResult", own == "1" ? RequestViews.OnItsOwnPage(box.View) : box.View);
        }

        // A request no handler takes, such as a POST, would otherwise get the page as if it were a GET
        public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
        {
            if (context.HandlerMethod == null)
                context.Result = BadRequest();
        }

        // An ID that isn't one is no request
        Task<RequestBox?> LoadAsync(long id) =>
            id <= 0 ? Task.FromResult<RequestBox?>(null) : HttpContext.RequestServices.GetRequiredService<RequestBoxes>().LoadAsync((ulong)id);
    }
}
