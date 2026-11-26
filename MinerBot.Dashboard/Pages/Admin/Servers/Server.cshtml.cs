using System.Globalization;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Discord;
using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Guilds.Models;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Status;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Hosting;
using MinerBot.Dashboard.Requests;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Servers;
using MinerBot.Dashboard.Settings;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Pages.Admin.Servers
{
    // One server: what MinerBot's copy has of it, its four settings read against its channels and its switches, with the
    // bot's last post of each kind, its channels in Discord's order with what the bot can do in each, its recent posts, its
    // joins and leaves, its members over 90 days, and the changes made to it on the dashboard. A server the copy doesn't
    // have but the website has settings for gets a page saying MinerBot isn't in it, and an ID that's neither is a 404. For
    // a server the bot is in and Discord has, while requests are on, it asks MinerBot for a test message to one of the
    // server's chosen channels or a refresh of the copy, each sent through RequestSender with the page's checks made again
    // at the POST, and links the Owner to the page that asks it to leave. The Owner saves the server's switches here, while
    // MinerBot is in it, through SwitchChanges; an Admin sees them read-only, and a gate before any handler records an
    // Admin's POST to them and sends it to /denied. Sent redirects here with ?request=, and a save with ?done=; anything
    // else is kept as a form's notice and redirects here with ?notice=, whose GET draws why, never this page drawn on the
    // POST, which a reload would send again.
    public sealed class ServerPageModel(IBotGuildReads guilds, IGuildSettingsReads settings, IBotStatusReads status, IAuditLogData audit,
        IBotRequestReads reads, IGuildSwitchData switches, IDeliveryReads deliveries, TimeProvider time, ILogger<ServerPageModel> logger) : PageModel
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

        // The server's switches, null when it has none, which means every kind is on; the bot's record of its posts of each
        // kind here, and its latest messages
        public GuildSwitches? Switches { get; private set; }
        public IReadOnlyList<DeliveryStatusRow> Deliveries { get; private set; } = [];
        public IReadOnlyList<DeliveryRow> RecentPosts { get; private set; } = [];

        // Who saved the switches last, by the name their row in the server's changes has, or their role
        public string? SwitchesChangedBy { get; private set; }

        // What ?done= says after a save, while the switches are still the ones it saved, and a switch form's notice, which
        // says whether it's a problem
        public string? SwitchesDone { get; private set; }
        public string? SwitchNotice { get; private set; }
        public bool SwitchNoticeIsProblem { get; private set; }

        public bool IsIn => Server is { left_at: null };

        // The Owner's form, only while MinerBot is in the server: a server it left has its switches deleted, and one it was
        // never in has none to read
        public bool CanChangeSwitches => IsOwner && IsIn;

        public DeliveryStatusRow? Delivery(SettingKind kind) =>
            SettingStatuses.SourceOf(kind) is { } source ? Deliveries.FirstOrDefault(row => row.source == source) : null;

        // The kind's latest message that went, for a kind whose status holds no last post: a leave deletes a server's statuses
        // while the record of its messages stays for 90 days, so the page never says no post is recorded while one is, for a
        // server MinerBot left or is back in, one older than the recent posts it lists included
        public DeliveryRow? LastMessage(SettingKind kind) =>
            SettingStatuses.SourceOf(kind) is { } source ? lastPosts.FirstOrDefault(post => post.source == source && post.state == DeliveryState.Sent) : null;

        // A recent post's channel, from the copy, when it has it
        public BotChannelRow? ChannelOf(ulong channelId) => channels.GetValueOrDefault(channelId) is { } channel && channel.guild_id == Id ? channel : null;

        public string? IconUrl => Server == null ? null : ServerRows.IconUrl(Server.guild_id, Server.icon_hash);
        public string ChangesLink => "/admin/audit?server=" + IdText;

        // MinerBot's actions here: whether they're offered, the channels the server chose for its posts as a test message
        // sees them, and the box for the request ?request= names, when it's this server's
        public ServerActionState Actions { get; private set; }
        public IReadOnlyList<TestChannel> TestChannels { get; private set; } = [];
        public RequestSection? Result { get; private set; }
        public bool IsOwner { get; private set; }
        public string LeaveLink => $"/admin/servers/{IdText}/leave";

        // A form sent and not sent on, from its notice: its handler, the key it keeps after a save the database didn't
        // confirm, why in its words, the request those name, and the channel it named, which stays chosen
        public string? Posted { get; private set; }
        public SendOutcome? Outcome { get; private set; }
        public string? Problem { get; private set; }
        public ulong? ProblemRequest { get; private set; }
        public ulong? PostedChannel { get; private set; }

        // The channel a test message named, kept in its notice under this name, as invariant digits
        const string ChannelBox = "channel";

        IReadOnlyDictionary<string, RequestView> latest = new Dictionary<string, RequestView>();
        IReadOnlyList<DeliveryRow> lastPosts = [];
        LimitReads limits = new([], [], []);
        Dictionary<ulong, BotChannelRow> channels = [];

        public async Task<IActionResult> OnGetAsync(long id, [FromQuery] string? request, [FromQuery] string? notice, [FromQuery] string? done, [FromQuery] string? saved)
        {
            // Every Discord ID is a positive long, which the route takes
            if (id <= 0 || !await LoadAsync((ulong)id))
                return NotFound();

            // A save's word, only while the switches are still the version it saved, so a later save, or a link someone made,
            // can't say this one happened
            if (done == SwitchWords.DoneWord && SwitchChanges.TryVersion(saved, out var version) && version > 0 && Switches?.version == version)
                SwitchesDone = SwitchWords.Saved;

            // A form's notice, which draws why nothing was sent, with the channel it named still chosen, and the form's key
            // after a save the database didn't confirm
            var found = HttpContext.RequestServices.GetRequiredService<FormNotices>().Find(this, notice, [ServerActions.TestMessage, ServerActions.Refresh, SwitchWords.Form]);

            // The switches' own, in fixed words: a problem, or nothing to save
            if (found is { Form: SwitchWords.Form })
            {
                SwitchNotice = found.Words;
                SwitchNoticeIsProblem = found.Status >= StatusCodes.Status400BadRequest;
            }
            else if (found != null)
            {
                Posted = found.Form;
                Outcome = found.Key is { } key ? new SendOutcome.Unsure(key) : null;
                Problem = found.Words;
                ProblemRequest = found.Request;
                PostedChannel = ulong.TryParse(found.Kept?.GetValueOrDefault(ChannelBox), NumberStyles.None, CultureInfo.InvariantCulture, out var channel) ? channel : null;

                // A save the database didn't confirm, whose form this GET doesn't draw, as once MinerBot has left the server:
                // the words don't offer it
                if (found.Unsure && !Drawn(found.Form))
                    Problem = RequestWords.UnsureNotOffered;
                // Nor do a test message's, sent before to another channel, once its form has gone: they name that request
                else if (found is { Request: { } before } && found.Words == RequestWords.SentBefore(before) && !Drawn(found.Form))
                    Problem = RequestWords.SentBeforeNotOffered(before);
            }

            // Only chooses what to show, so anything but a request's number, or one of another server or kind, shows nothing
            if (ulong.TryParse(request, NumberStyles.None, CultureInfo.InvariantCulture, out var named) && named > 0)
                Result = await HttpContext.RequestServices.GetRequiredService<RequestBoxes>().SectionAsync(named, ServerActions.Kinds, Id,
                    RequestBoxes.CheckAgain($"/admin/servers/{IdText}?request={named.ToString(CultureInfo.InvariantCulture)}", Request.Query));

            if (found != null)
                RequestWords.Answer(Response, found, Now);

            return Page();
        }

        // A test message to one of the channels the server chose, checked again against the copy and the settings as they
        // are now: a channel that isn't one of them now, as after /admin set_channels changed them, and one that can't take
        // it now are refused, with the picker as it is then
        public async Task<IActionResult> OnPostTestMessageAsync(long id, string? channel, string? formkey)
        {
            if (id <= 0 || !ulong.TryParse(channel, NumberStyles.None, CultureInfo.InvariantCulture, out var channelId) || channelId == 0)
                return BadRequest();

            if (!await LoadAsync((ulong)id))
                return NotFound();

            // Not a 400: the server can choose the channel again, after which the same form would be sent
            if (TestChannels.FirstOrDefault(chosen => chosen.Id == channelId) is not { } test)
                return Refused(ServerActions.TestMessage, ServerActions.NotChosenNow, null);

            if (Actions is ServerActionState.NotIn or ServerActionState.Unavailable)
                return Refused(ServerActions.TestMessage, ServerActions.NotOffered(Actions), channelId);

            if (test.Why is { } why)
                return Refused(ServerActions.TestMessage, ServerActions.ChannelNotNow(why), channelId);

            return await SendAsync(ServerActions.TestMessage, new SendOrder(RequestKinds.GuildTestMessage, formkey, Id, channelId), channelId);
        }

        // The Owner's save of the switches, which the gate lets no one else reach. Every field is checked before the database:
        // the version the page was drawn with, in digits, and each box ticked or not, as a browser sends it.
        public async Task<IActionResult> OnPostSwitchesAsync(long id, string? version, string? armor, string? maps, string? lobbies)
        {
            if (id <= 0 || !SwitchChanges.TryVersion(version, out var seen) || !SwitchChanges.TryTick(armor, out var armorOn)
                || !SwitchChanges.TryTick(maps, out var mapsOn) || !SwitchChanges.TryTick(lobbies, out var lobbiesOn))
                return BadRequest();

            if (!await LoadAsync((ulong)id))
                return NotFound();

            // Not a 400: MinerBot may be invited back, after which the same form would be saved
            if (!IsIn)
                return KeepSwitches(StatusCodes.Status409Conflict, SwitchWords.NotInNow);

            var result = await HttpContext.RequestServices.GetRequiredService<SwitchChanges>()
                .SaveAsync(new SwitchRequest(Id, seen, armorOn, mapsOn, lobbiesOn), HttpContext);

            return result.Outcome switch
            {
                // This page with the save's word, so reloading shows it rather than saving again
                SwitchOutcome.Saved => RedirectToPage(null, null, new { id = (long)Id, done = SwitchWords.DoneWord, saved = result.Version }, SwitchWords.Anchor),
                SwitchOutcome.Unchanged => KeepSwitches(StatusCodes.Status200OK, SwitchWords.Unchanged),
                SwitchOutcome.Already => KeepSwitches(StatusCodes.Status200OK, SwitchWords.Already),
                SwitchOutcome.Stale => KeepSwitches(StatusCodes.Status409Conflict, SwitchWords.Stale),
                SwitchOutcome.Limit => Keep(new FormNotice { Form = SwitchWords.Form, Status = StatusCodes.Status429TooManyRequests, Words = SwitchWords.Limit(result.After!.Value), After = result.After }),
                _ => Keep(new FormNotice { Form = SwitchWords.Form, Status = StatusCodes.Status503ServiceUnavailable, Words = SwitchWords.Unsure, Unsure = true })
            };
        }

        IActionResult KeepSwitches(int status, string words) => Keep(new FormNotice { Form = SwitchWords.Form, Status = status, Words = words });

        // The bot rewrites its copy of this server from what it holds now
        public async Task<IActionResult> OnPostRefreshAsync(long id, string? formkey)
        {
            if (id <= 0)
                return BadRequest();

            if (!await LoadAsync((ulong)id))
                return NotFound();

            if (Actions is ServerActionState.NotIn or ServerActionState.Unavailable)
                return Refused(ServerActions.Refresh, ServerActions.NotOffered(Actions), null);

            return await SendAsync(ServerActions.Refresh, new SendOrder(RequestKinds.GuildRefresh, formkey, Id), null);
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

        // A new key for every form on every render, refusals included, apart from the form a notice draws after a save the
        // database didn't confirm, which keeps the key it was sent with, so sending it again finds the first request
        public string KeyFor(string handler) => handler == Posted ? FormKeys.For(Outcome) : RequestFields.NewKey();

        // Whether the page draws the action's form: none while the actions aren't offered, and no test message while none of
        // the channels the server chose can take one
        bool Drawn(string handler) => Actions == ServerActionState.Offered && (handler != ServerActions.TestMessage || TestChannels.Any(channel => channel.Offered));

        async Task<IActionResult> SendAsync(string handler, SendOrder order, ulong? channel)
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

            // Anything else is a notice this page's GET draws, with the channel the form named
            return Keep(FormNotice.Of(handler, outcome) with { Kept = Kept(channel) });
        }

        // Why nothing was sent, before the sender, as things stand: a notice this page's GET draws at 409
        IActionResult Refused(string handler, string words, ulong? channel) =>
            Keep(new FormNotice { Form = handler, Status = StatusCodes.Status409Conflict, Words = words, Kept = Kept(channel) });

        IActionResult Keep(FormNotice notice) => HttpContext.RequestServices.GetRequiredService<FormNotices>().Keep(this, notice, new { id = (long)Id });

        static Dictionary<string, string>? Kept(ulong? channel) =>
            channel is { } named ? new(StringComparer.Ordinal) { [ChannelBox] = named.ToString(CultureInfo.InvariantCulture) } : null;

        // Everything the page shows, for its GET, and what a form's checks read at the POST. False for a server neither the
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
            channels = [];
            Switches = await DatabaseCalls.Guard(() => switches.GetSwitches(Id), logger);

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

                // The bot posts only where it is, so a server only the website knows has no record
                Deliveries = await DatabaseCalls.Guard(() => deliveries.ListStatuses([Id]), logger);
                RecentPosts = await DatabaseCalls.Guard(() => deliveries.ListRecentPosts(Id, DeliveryWords.RecentShown), logger);
                lastPosts = await DatabaseCalls.Guard(() => deliveries.ListLastPosts(Id), logger);
            }

            Statuses = SettingStatuses.Of(Id, IsIn, Settings, channels, Switches);

            var changes = await DatabaseCalls.Guard(() => audit.ListAudit(new AuditQuery { GuildId = Id, PageSize = ChangesShown, IncludeIp = ShowsAddresses }), logger);
            ChangeTotal = changes.Total;
            Changes = AuditRows.Table(changes.Rows, ShowsAddresses);

            if (Switches is { } saved)
                SwitchesChangedBy = changes.Rows.FirstOrDefault(row => row.action == AuditWords.SwitchesChange && row.outcome == AuditOutcome.Ok && row.actor_id == saved.changed_by
                    && row.at == saved.changed_at)?.actor_name ?? SwitchWords.RoleName(saved.changed_by_role);

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

        // The gate, before any handler. The antiforgery check and the origin check have run already, so a forged POST can't
        // write its audit row.
        public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(next);

            // 1. A request no handler takes would otherwise get the page as if it were a GET
            if (context.HandlerMethod is not { } handler)
            {
                context.Result = BadRequest();
                return;
            }

            // 2. The switches are the Owner's: anyone else's POST to them is one refused audit row, under the server it names,
            // and /denied. The handler never runs, so nothing is read or saved.
            if (handler.Name == SwitchWords.Handler && !string.Equals(handler.HttpMethod, HttpMethods.Get, StringComparison.OrdinalIgnoreCase)
                && RoleClaims.RoleOf(User) != DashboardRole.Owner)
            {
                ulong? server = context.HandlerArguments.TryGetValue("id", out var value) && value is long id && id > 0 ? (ulong)id : null;
                await DatabaseCalls.Guard(() => audit.AddAudit(SwitchChanges.Refusal(HttpContext, server, SwitchWords.OnlyOwnerDetail, time.GetUtcNow().UtcDateTime)), logger);
                context.Result = Forbid();
                return;
            }

            // 3. The handler
            await next();
        }
    }
}
