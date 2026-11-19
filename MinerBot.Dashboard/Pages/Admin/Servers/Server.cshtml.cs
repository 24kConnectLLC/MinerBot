using System.Globalization;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Discord;
using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Guilds.Models;
using DataAccessLibrary.Status;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Servers;

namespace MinerBot.Dashboard.Pages.Admin.Servers
{
    // One server: what MinerBot's copy has of it, its four settings read against its channels, its channels in Discord's
    // order with what the bot can do in each, its joins and leaves, its members over 90 days, and the changes made to it on
    // the dashboard. A server the copy doesn't have but the website has settings for gets a page saying MinerBot isn't in
    // it, and an ID that's neither is a 404.
    public sealed class ServerPageModel(IBotGuildReads guilds, IGuildSettingsReads settings, IBotStatusReads status, IAuditLogData audit,
        TimeProvider time, ILogger<ServerPageModel> logger) : PageModel
    {
        public const int EventsShown = 50;
        public const int ChangesShown = 25;
        public const int TrendDays = 90;

        public ulong Id { get; private set; }
        public string IdText => Id.ToString(CultureInfo.InvariantCulture);

        // Null for a server the copy doesn't have, of which the website has settings only
        public BotGuildRow? Server { get; private set; }
        public Guild? Settings { get; private set; }
        public IReadOnlyList<SettingStatus> Statuses { get; private set; } = [];
        public IReadOnlyList<ChannelGroup> Groups { get; private set; } = [];
        public IReadOnlyList<GuildEventRow> JoinsAndLeaves { get; private set; } = [];
        public IReadOnlyList<GuildDayRow> MemberDays { get; private set; } = [];
        public MemberTrend? Trend { get; private set; }
        public CopyFreshness? Freshness { get; private set; }
        public AuditTable Changes { get; private set; } = new([], false);
        public long ChangeTotal { get; private set; }
        public bool ShowsAddresses { get; private set; }
        public DateTime Now { get; private set; }

        public bool IsIn => Server is { left_at: null };
        public string? IconUrl => Server == null ? null : ServerRows.IconUrl(Server.guild_id, Server.icon_hash);
        public string ChangesLink => "/admin/audit?server=" + IdText;

        public async Task<IActionResult> OnGetAsync(long id)
        {
            // Every Discord ID is a positive long, which the route takes
            if (id <= 0)
                return NotFound();

            Id = (ulong)id;
            ShowsAddresses = RoleClaims.RoleOf(User) == DashboardRole.Owner;

            Server = await DatabaseCalls.Guard(() => guilds.GetGuild(Id), logger);
            Settings = await DatabaseCalls.Guard(() => settings.GetSettings(Id), logger);

            if (Server == null && Settings == null)
                return NotFound();

            Now = time.GetUtcNow().UtcDateTime;
            var channels = new Dictionary<ulong, BotChannelRow>();

            if (Server != null)
            {
                var own = await DatabaseCalls.Guard(() => guilds.ListChannels(Id), logger);

                foreach (var channel in own)
                    channels[channel.channel_id] = channel;

                // A setting's channel that isn't among the server's own may be another server's
                var others = Settings == null ? [] : SettingStatuses.Kinds.Select(kind => SettingStatuses.ChannelOf(Settings, kind))
                    .Where(channel => channel != Guild.ChannelOff && !channels.ContainsKey(channel)).Distinct().ToList();

                if (others.Count > 0)
                {
                    foreach (var channel in await DatabaseCalls.Guard(() => guilds.FindChannels(others), logger))
                        channels[channel.channel_id] = channel;
                }

                Groups = ChannelGroups.Of(own);
                JoinsAndLeaves = await DatabaseCalls.Guard(() => guilds.ListGuildEvents(Id, EventsShown), logger);

                var today = Now.Date;
                MemberDays = await DatabaseCalls.Guard(() => guilds.ListMemberDays(Id, today.AddDays(1 - TrendDays), today.AddDays(1)), logger);
                Trend = ServerRows.Trend(MemberDays, today.AddDays(1 - TrendDays));
                Freshness = CopyFreshnesses.ForServer(await ServerFreshness.LoadAsync(status, Now, logger), IsIn && !Server.available);
            }

            Statuses = SettingStatuses.Of(Id, IsIn, Settings, channels);

            var changes = await DatabaseCalls.Guard(() => audit.ListAudit(new AuditQuery { GuildId = Id, PageSize = ChangesShown, IncludeIp = ShowsAddresses }), logger);
            ChangeTotal = changes.Total;
            Changes = AuditRows.Table(changes.Rows, ShowsAddresses);

            return Page();
        }

        // Nothing here takes a form, and a request no handler takes would otherwise get the page as if it were a GET
        public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
        {
            if (context.HandlerMethod == null)
                context.Result = BadRequest();
        }
    }
}
