using System.Globalization;
using DataAccessLibrary;
using DataAccessLibrary.Armor;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Discord;
using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Health;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Health;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Servers;
using MinerBot.Dashboard.Status;
using MinerBot.Shared.Armor;
using MinerBot.Shared.Requests;
using StatusBanners = MinerBot.Dashboard.Status.Banners;

namespace MinerBot.Dashboard.Pages.Admin
{
    // The bot at a glance: what needs someone, the status box, the last notification run, pending armor and the latest
    // changes, all read-only. The website's database failing doesn't take the page down: it's asked first, as the
    // readiness check asks it, its parts say it isn't answering, and a banner shows. A read that fails after it answered,
    // such as one of a table whose right the login lost, leaves the other read's part and has a banner of its own, while
    // the status box keeps the check's answer, as its refresh does. MinerBot's own database failing gives the usual page,
    // since every session needs it anyway, apart from the record of the bot's posts, which is read on its own too, so a read
    // of it that fails costs only its banner. The Status handler is the status box alone, for refresh.js.
    public sealed class OverviewModel(IBotStatusReads status, IBotGuildReads guilds, IGuildSettingsReads settings,
        IArmorSubmissionData submissions, IAuditLogData audit, IDatabaseHealthData databases, IGuildSwitchData switches, IDeliveryReads deliveries,
        ILogger<OverviewModel> logger) : PageModel
    {
        public const int ChangesShown = 10;

        public string Name { get; private set; } = "";
        public string UserId { get; private set; } = "";
        public string Role { get; private set; } = "";

        public StatusBox Box { get; private set; } = null!;
        public IReadOnlyList<Banner> Banners { get; private set; } = [];

        // The notification job's row, null before the bot has recorded its jobs
        public JobStatusRow? Notifications { get; private set; }

        // Null while the website's database doesn't answer, or when reading it failed after it answered
        public PendingArmor? Armor { get; private set; }

        // The website's reads that failed after its database answered the check
        public WebsiteReads FailedReads { get; private set; }

        public AuditTable Changes { get; private set; } = new([], false);

        // Whether the latest changes show each visitor's address: only for the Owner
        public bool ShowsAddresses { get; private set; }

        public async Task OnGetAsync()
        {
            var user = SignedInUser.From(User);

            Name = user?.Name ?? "";
            UserId = user?.Id.ToString(CultureInfo.InvariantCulture) ?? "";
            Role = RoleClaims.RoleOf(User).Name();
            ShowsAddresses = RoleClaims.RoleOf(User) == DashboardRole.Owner;

            // The website's parts, only once its database answers, so a silent one costs the check's 3 seconds and no more.
            // Each read on its own, so one that fails after that leaves the other's part. Guard has already logged it.
            var websiteAnswers = await databases.Answers(DBConnectionName.Armor, DatabaseHealthCheck.Limit);
            IReadOnlyList<Guild>? settingsRows = null;

            if (websiteAnswers)
            {
                try
                {
                    var pending = await DatabaseCalls.Guard(() => submissions.ListByStatus(ArmorStatus.Pending, 0, 1), logger);
                    Armor = new PendingArmor(pending.Total, pending.Rows.FirstOrDefault()?.CreatedAt);
                }
                catch (DashboardUnavailableException)
                {
                    FailedReads |= WebsiteReads.Armor;
                }

                try
                {
                    settingsRows = await DatabaseCalls.Guard(() => settings.ListSettings(), logger);
                }
                catch (DashboardUnavailableException)
                {
                    FailedReads |= WebsiteReads.Settings;
                }
            }

            // The check's answer, which the box its refresh brings gives too
            Box = await Boxes().LoadAsync(websiteAnswers);
            var jobs = await DatabaseCalls.Guard(() => status.ListJobs(), logger);
            Notifications = jobs.FirstOrDefault(job => job.job == JobWords.Notifications);

            int? serversCantPost = null;
            int? serversFailing = null;
            IReadOnlyList<GuildSwitches>? switchRows = null;
            IReadOnlyList<DataAccessLibrary.Guilds.Models.BotGuildRow>? copy = null;

            // The servers whose posts keep failing, from the bot's record and its copy of its servers alone: one it's in, with a
            // kind that isn't switched off. On its own, so a read of the record that fails leaves the rest of the page.
            try
            {
                switchRows = await DatabaseCalls.Guard(() => switches.ListSwitches(), logger);
                var failing = await DatabaseCalls.Guard(() => deliveries.ListFailing(DeliveryWords.FailingTries), logger);
                copy = await DatabaseCalls.Guard(() => guilds.ListGuilds(), logger);
                serversFailing = FailingServers(failing, switchRows, copy);
            }
            catch (DashboardUnavailableException)
            {
                switchRows = null;
            }

            if (settingsRows != null)
            {
                var servers = copy ?? await DatabaseCalls.Guard(() => guilds.ListGuilds(), logger);
                var channels = await DatabaseCalls.Guard(() => guilds.FindChannels(ServerRows.SettingChannels(servers, settingsRows)), logger);

                // A kind switched off isn't one MinerBot posts, whatever its channel's permissions
                serversCantPost = ServerRows.Rows(servers, settingsRows, channels, [], Box.Now, switchRows)
                    .Count(row => row.IsIn && row.Settings.Any(setting => SettingStatuses.IsProblem(setting.State)));
            }

            // A sign-in's message to the owner isn't a change, and every sign-in has one beside its own row, so they're left
            // out here; the audit page lists them
            var changes = await DatabaseCalls.Guard(() => audit.ListAudit(new AuditQuery
            {
                PageSize = ChangesShown,
                IncludeIp = ShowsAddresses,
                ExcludeActions = [RequestKinds.OwnerSigninAlert]
            }), logger);

            // Named by the section's heading, for when the rows hold no link, such as sign-ins refused for their scope
            Changes = AuditRows.Table(changes.Rows, ShowsAddresses, labelledBy: "changes");

            // The requests' banners after the bot's own, from MinerBot's database alone
            var facts = await HttpContext.RequestServices.GetRequiredService<BannerReads>().LoadAsync(Box.Status);
            Banners = [.. StatusBanners.Of(Box, jobs, Armor, serversCantPost, FailedReads, serversFailing), .. RequestBanners.Of(facts, RoleClaims.RoleOf(User), Box.Now)];
        }

        // Each server once, however many of its kinds keep failing
        internal static int FailingServers(IReadOnlyList<DeliveryStatusRow> failing, IReadOnlyList<GuildSwitches> switchRows,
            IReadOnlyList<DataAccessLibrary.Guilds.Models.BotGuildRow> copy)
        {
            var inServers = copy.Where(guild => guild.left_at == null).Select(guild => guild.guild_id).ToHashSet();
            var switchesById = switchRows.ToDictionary(row => row.guild_id);

            return failing.Where(row => DeliveryWords.KeepsFailing(row) && inServers.Contains(row.guild_id) && PostSource.IsPosted(row.source)
                    && (!switchesById.TryGetValue(row.guild_id, out var switched) || switched.IsOn(row.source)))
                .Select(row => row.guild_id)
                .Distinct()
                .Count();
        }

        // The status box alone, marked as the status fragment on the response itself, so an error page, which the
        // exception handler writes on a cleared response, never carries the marker
        public async Task<IActionResult> OnGetStatusAsync()
        {
            var box = await Boxes().LoadAsync();

            Response.Headers[BackgroundRefresh.FragmentHeader] = StatusBoxes.Fragment;
            return Partial("_StatusBox", box);
        }

        StatusBoxes Boxes() => HttpContext.RequestServices.GetRequiredService<StatusBoxes>();

        // Nothing here takes a form, and a request no handler takes would otherwise get the page as if it were a GET
        public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
        {
            if (context.HandlerMethod == null)
                context.Result = BadRequest();
        }
    }
}
