using DataAccessLibrary.Discord;
using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Servers;
using MinerBot.Dashboard.Status;

namespace MinerBot.Dashboard.Pages.Admin.Servers
{
    // Every server MinerBot is in or has left, from its own copy of them, with the website's four settings of each read
    // against that copy and its switches, the bot's last post of each kind there, and the settings of servers it isn't in
    // apart. Read-only, so it works while the bot is down, and the copy's age says how current it is.
    public sealed class ServerListModel(IBotGuildReads guilds, IGuildSettingsReads settings, IBotStatusReads status, IGuildSwitchData switches,
        IDeliveryReads deliveries, TimeProvider time, ILogger<ServerListModel> logger) : PageModel
    {
        // The members' trend covers this many days, or the days since the first count
        public const int TrendDays = 30;

        public ServerFilter Filter { get; private set; } = ServerRows.Default;
        public IReadOnlyList<ServerRow> All { get; private set; } = [];
        public IReadOnlyList<ServerRow> Rows { get; private set; } = [];
        public IReadOnlyList<Guild> Elsewhere { get; private set; } = [];
        public CopyFreshness Freshness { get; private set; } = null!;
        public DateTime Now { get; private set; }

        // The servers list read as far as its limit, so it may not show every server
        public bool AtLimit { get; private set; }

        public static IReadOnlyList<ServerFilter> Filters => ServerRows.Filters;

        public int Count(ServerFilter filter) => All.Count(filter.Shows);

        public async Task<IActionResult> OnGetAsync([FromQuery] string? show)
        {
            if (ServerRows.FromWord(show) is not { } filter)
                return NotFound();

            Filter = filter;

            var settingsRows = await DatabaseCalls.Guard(() => settings.ListSettings(), logger);
            var servers = await DatabaseCalls.Guard(() => guilds.ListGuilds(), logger);
            var channels = await DatabaseCalls.Guard(() => guilds.FindChannels(ServerRows.SettingChannels(servers, settingsRows)), logger);

            Now = time.GetUtcNow().UtcDateTime;
            var trendFrom = Now.Date.AddDays(-TrendDays);
            var trends = await DatabaseCalls.Guard(() => guilds.MemberTrends(trendFrom), logger);

            // Every server's switches in one read, and the record of the listed servers' posts in another
            var switchRows = await DatabaseCalls.Guard(() => switches.ListSwitches(), logger);
            var statuses = await DatabaseCalls.Guard(() => deliveries.ListStatuses(servers.Select(server => server.guild_id).ToList()), logger);

            All = ServerRows.Rows(servers, settingsRows, channels, trends, trendFrom, switchRows, statuses);
            Rows = All.Where(filter.Shows).ToList();
            Elsewhere = ServerRows.SettingsElsewhere(servers, settingsRows);
            AtLimit = servers.Count >= BotGuildReads.GuildLimit;
            Freshness = await ServerFreshness.LoadAsync(status, Now, logger);

            return Page();
        }

        // Nothing here takes a form, and a request no handler takes would otherwise get the page as if it were a GET
        public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
        {
            if (context.HandlerMethod == null)
                context.Result = BadRequest();
        }
    }

    // How current the copy is, from the status row and the events it needs, for the servers list and a server's page
    internal static class ServerFreshness
    {
        public static async Task<CopyFreshness> LoadAsync(IBotStatusReads reads, DateTime now, ILogger logger)
        {
            var row = await DatabaseCalls.Guard(() => reads.GetStatus(), logger);
            var state = BotStates.Of(row, now);
            BotEventRow? outage = null, drop = null;
            DateTime? runEnded = null;

            if (CopyFreshnesses.Connected(row, state))
            {
                outage = await DatabaseCalls.Guard(() => reads.LatestEvent(EventWords.DatabaseUnreachable), logger);
            }
            else if (row != null)
            {
                var connected = await DatabaseCalls.Guard(() => reads.LatestEvent(EventWords.DiscordConnected), logger);
                drop = await DatabaseCalls.Guard(() => reads.FirstEventAfter(EventWords.DiscordDisconnected, connected), logger);

                // The run that made that connection ended by the first clean stop or start since, if either came. If it crashed,
                // the next run says so in an unclean end after its own start. Not the first unclean end since the connection: a
                // run that follows a crash records one at its first status write, 5 seconds after its start and so usually
                // after its own first connection, naming the crashed run's last heartbeat.
                var stop = await DatabaseCalls.Guard(() => reads.FirstEventAfter(EventWords.Stopping, connected), logger);
                var start = await DatabaseCalls.Guard(() => reads.FirstEventAfter(EventWords.Started, connected), logger);
                var unclean = start == null ? null : await DatabaseCalls.Guard(() => reads.FirstEventAfter(EventWords.UncleanEnd, start), logger);
                runEnded = CopyFreshnesses.RunEnded(stop, start, unclean);
            }

            return CopyFreshnesses.Of(row, state, now, outage, drop, runEnded);
        }
    }
}
