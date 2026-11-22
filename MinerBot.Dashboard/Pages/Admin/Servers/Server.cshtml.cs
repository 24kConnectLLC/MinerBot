using System.Globalization;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Discord;
using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Guilds.Models;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Status;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Servers;
using MinerBot.Dashboard.Settings;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Pages.Admin.Servers
{
    // One server: what MinerBot's copy has of it, its four settings read against its channels, its channels in Discord's
    // order with what the bot can do in each, its joins and leaves, its members over 90 days, and the changes made to it on
    // the dashboard. A server the copy doesn't have but the website has settings for gets a page saying MinerBot isn't in
    // it, and an ID that's neither is a 404. For a server the bot is in and Discord has, while requests are on, it asks
    // MinerBot for a test message to one of the server's chosen channels or a refresh of the copy, each sent through
    // RequestSender with the page's checks made again at the POST, and links the Owner to the page that asks it to leave.
    public sealed class ServerPageModel(IBotGuildReads guilds, IGuildSettingsReads settings, IBotStatusReads status, IAuditLogData audit,
        IBotRequestReads reads, TimeProvider time, ILogger<ServerPageModel> logger) : PageModel
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

        // MinerBot's actions here: whether they're offered, the channels the server chose for its posts as a test message
        // sees them, and the box for the request ?request= names, when it's this server's
        public ServerActionState Actions { get; private set; }
        public IReadOnlyList<TestChannel> TestChannels { get; private set; } = [];
        public RequestSection? Result { get; private set; }
        public bool IsOwner { get; private set; }
        public string LeaveLink => $"/admin/servers/{IdText}/leave";

        // A form sent and not sent on: its handler, how the sender answered, why in its words, the request those name, and
        // the channel it named, which stays chosen
        public string? Posted { get; private set; }
        public SendOutcome? Outcome { get; private set; }
        public string? Problem { get; private set; }
        public ulong? ProblemRequest { get; private set; }
        public ulong? PostedChannel { get; private set; }

        IReadOnlyDictionary<string, RequestView> latest = new Dictionary<string, RequestView>();
        LimitReads limits = new([], [], []);

        public async Task<IActionResult> OnGetAsync(long id, [FromQuery] string? request)
        {
            // Every Discord ID is a positive long, which the route takes
            if (id <= 0 || !await LoadAsync((ulong)id))
                return NotFound();

            // Only chooses what to show, so anything but a request's number, or one of another server or kind, shows nothing
            if (ulong.TryParse(request, NumberStyles.None, CultureInfo.InvariantCulture, out var named) && named > 0)
                Result = await HttpContext.RequestServices.GetRequiredService<RequestBoxes>().SectionAsync(named, ServerActions.Kinds, Id,
                    $"/admin/servers/{IdText}?request={named.ToString(CultureInfo.InvariantCulture)}#result");

            return Page();
        }

        // A test message to one of the channels the server chose, checked again against the copy and the settings as they
        // are now: a channel that isn't one of them is a form someone made up, and one that can't take it now is refused,
        // with the picker as it is
        public async Task<IActionResult> OnPostTestMessageAsync(long id, string? channel, string? formkey)
        {
            if (id <= 0 || !ulong.TryParse(channel, NumberStyles.None, CultureInfo.InvariantCulture, out var channelId) || channelId == 0)
                return BadRequest();

            if (!await LoadAsync((ulong)id))
                return NotFound();

            if (TestChannels.FirstOrDefault(chosen => chosen.Id == channelId) is not { } test)
                return BadRequest();

            PostedChannel = channelId;

            if (Actions is ServerActionState.NotIn or ServerActionState.Unavailable)
                return Refused(ServerActions.TestMessage, ServerActions.NotOffered(Actions));

            if (test.Why is { } why)
                return Refused(ServerActions.TestMessage, ServerActions.ChannelNotNow(why));

            return await SendAsync(ServerActions.TestMessage, new SendOrder(RequestKinds.GuildTestMessage, formkey, Id, channelId));
        }

        // The bot rewrites its copy of this server from what it holds now
        public async Task<IActionResult> OnPostRefreshAsync(long id, string? formkey)
        {
            if (id <= 0)
                return BadRequest();

            if (!await LoadAsync((ulong)id))
                return NotFound();

            if (Actions is ServerActionState.NotIn or ServerActionState.Unavailable)
                return Refused(ServerActions.Refresh, ServerActions.NotOffered(Actions));

            return await SendAsync(ServerActions.Refresh, new SendOrder(RequestKinds.GuildRefresh, formkey, Id));
        }

        // One action above its form: its label, what it does, the bot's limit, its last use for this server, whether one
        // more would be sent now, and why the last try from it was refused
        public ControlView Control(string handler)
        {
            var (kind, label, help) = handler == ServerActions.TestMessage
                ? (RequestKinds.GuildTestMessage, ServerActions.TestMessageLabel, ServerActions.TestMessageHelp)
                : (RequestKinds.GuildRefresh, ServerActions.RefreshLabel, ServerActions.RefreshHelp);
            var posted = handler == Posted;

            return new ControlView(handler, label, help, RequestWords.Limit(RequestKinds.Find(kind)!), new RequestLine(latest.GetValueOrDefault(kind)),
                Actions == ServerActionState.Offered ? ControlForm.Offered : ControlForm.NotOffered, KeyFor(handler))
            {
                Availability = Actions == ServerActionState.Offered ? limits.Line(RequestKinds.Find(kind)!, Id, Now) : null,
                Problem = posted ? Problem : null,
                ProblemRequest = posted ? ProblemRequest : null
            };
        }

        // The leave's last use here, under the Owner's link to its page
        public RequestLine LastLeave => new(latest.GetValueOrDefault(RequestKinds.GuildLeave));

        // A new key for every form on every render, refusals included, apart from the form sent when the database didn't
        // confirm its save, which keeps the key it was sent with, so sending it again finds the first request
        public string KeyFor(string handler) => handler == Posted ? FormKeys.For(Outcome) : RequestFields.NewKey();

        async Task<IActionResult> SendAsync(string handler, SendOrder order)
        {
            var outcome = await HttpContext.RequestServices.GetRequiredService<RequestSender>().SendAsync(order, HttpContext);

            switch (outcome)
            {
                // This page with the request's box, so reloading shows the box rather than sending the form again
                case SendOutcome.Sent sent:
                    return RedirectToPage(null, null, new { id = (long)Id, request = sent.Id }, "result");

                case SendOutcome.Refused { Why: SendRefusal.Forbidden }:
                    return Forbid();

                case SendOutcome.Refused { Why: SendRefusal.MadeUp }:
                    return BadRequest();
            }

            Posted = handler;
            Outcome = outcome;
            (Problem, ProblemRequest) = outcome switch
            {
                SendOutcome.SentBefore before => (RequestWords.SentBefore(before.Id), before.Id),
                SendOutcome.Refused refused => (refused.Words, refused.Waiting),
                _ => (RequestWords.Unsure, (ulong?)null)
            };

            RequestWords.Answer(Response, outcome, Now);
            return Page();
        }

        // The page again with why nothing was sent, before the sender, as things stand
        PageResult Refused(string handler, string words)
        {
            Posted = handler;
            Problem = words;
            Response.StatusCode = StatusCodes.Status409Conflict;
            return Page();
        }

        // Everything the page shows, for its GET and for a form's answer that draws it again. False for a server neither the
        // copy nor the website knows.
        async Task<bool> LoadAsync(ulong id)
        {
            Id = id;
            IsOwner = RoleClaims.RoleOf(User) == DashboardRole.Owner;
            ShowsAddresses = IsOwner;

            Server = await DatabaseCalls.Guard(() => guilds.GetGuild(Id), logger);
            Settings = await DatabaseCalls.Guard(() => settings.GetSettings(Id), logger);

            if (Server == null && Settings == null)
                return false;

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

            // The actions, and what they need, only where they're offered
            var requestsOn = RequestSigning.ParseKey(HttpContext.RequestServices.GetRequiredService<IOptions<DashboardOptions>>().Value.RequestSigningKey) != null;
            Actions = ServerActions.State(Server, requestsOn);
            TestChannels = ServerActions.TestChannels(Id, Settings, channels);

            if (Actions == ServerActionState.Offered)
            {
                // Each action's last use for this server. Its stage is all a line shows, so the bot's status isn't read for it.
                var rows = await DatabaseCalls.Guard(() => reads.ListLatest(ServerActions.Kinds, Id), logger);
                latest = rows.ToDictionary(row => row.kind, row => RequestViews.Of(row, null, Now, RequestViews.Waits), StringComparer.Ordinal);

                if (RequestActors.Of(HttpContext) is { } actor)
                    limits = await LimitReads.LoadAsync(reads, actor.Id, Now, logger);
            }

            return true;
        }

        // A request no handler takes would otherwise get the page as if it were a GET
        public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
        {
            if (context.HandlerMethod == null)
                context.Result = BadRequest();
        }
    }
}
