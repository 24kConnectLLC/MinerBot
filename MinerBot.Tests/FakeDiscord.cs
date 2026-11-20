using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataAccessLibrary;
using DataAccessLibrary.Armor;
using DataAccessLibrary.Discord;
using Discord;
using Discord.Interactions;
using Discord.Net;
using Discord.Net.Rest;
using Discord.Net.WebSockets;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MinerBot_2._0.Handlers;
using MinerBot_2._0.Jobs;
using MinerBot_2._0.Requests;
using MinerBot_2._0.Services;
using MinerBot_2._0.Status;
using SteamWebAPIAccess;

namespace MinerBot.Tests
{
    // FakeDiscord swaps the console and the cooldowns are static, so these tests run one at a time.
    [CollectionDefinition("Discord", DisableParallelization = true)]
    public class DiscordCollection { }

    // A presence the bot sent: with an IDENTIFY (op 2) or in a presence update (op 3), as its activity's name, type and state,
    // which a custom status keeps its text in. No activity is all null.
    internal sealed record SentPresence(int Op, string? Name, int? Type, string? State);

    // One thing the bot did, in order. Body is the JSON it sent to Discord, File the upload that came with it, and Endpoint where it went.
    internal sealed record Step(string Name, string Body = "", string? File = null, string Endpoint = "")
    {
        // The channel of a message the bot sent
        public ulong SentTo => ulong.Parse(Regex.Match(Endpoint, @"^channels/(\d+)/messages$").Groups[1].Value);

        // The first response to an interaction nests the message under data, a followup doesn't
        public JsonElement Message
        {
            get
            {
                var json = JsonDocument.Parse(Body).RootElement;
                return json.TryGetProperty("data", out var data) ? data : json;
            }
        }

        public string? Content => Message.TryGetProperty("content", out var content) ? content.GetString() : null;

        public bool IsEphemeral => Message.TryGetProperty("flags", out var flags) && ((MessageFlags)flags.GetInt32()).HasFlag(MessageFlags.Ephemeral);

        public bool AllowsNoMentions => Message.TryGetProperty("allowed_mentions", out var mentions)
            && IsEmpty(mentions, "parse") && IsEmpty(mentions, "roles") && IsEmpty(mentions, "users");

        static bool IsEmpty(JsonElement json, string name) =>
            !json.TryGetProperty(name, out var list) || list.ValueKind == JsonValueKind.Null || list.GetArrayLength() == 0;

        public IEnumerable<string> EmbedTitles => Message.TryGetProperty("embeds", out var embeds)
            ? embeds.EnumerateArray().Select(embed => embed.GetProperty("title").GetString()!)
            : [];

        public IEnumerable<string> EmbedDescriptions => Message.TryGetProperty("embeds", out var embeds)
            ? embeds.EnumerateArray().Select(embed => embed.GetProperty("description").GetString()!)
            : [];

        // The small line above each embed's title, null for an embed without one
        public IEnumerable<string?> EmbedAuthors => Message.TryGetProperty("embeds", out var embeds)
            ? embeds.EnumerateArray().Select(embed => Author(embed)?.GetProperty("name").GetString())
            : [];

        // What Discord counts against its limit for all the embeds of a message
        public int EmbedsLength => Message.TryGetProperty("embeds", out var embeds)
            ? embeds.EnumerateArray().Sum(embed => TextLength(embed, "title") + TextLength(embed, "description")
                + (Author(embed) is JsonElement author ? TextLength(author, "name") : 0)
                + TextLength(embed.GetProperty("footer"), "text")
                + embed.GetProperty("fields").EnumerateArray().Sum(row => TextLength(row, "name") + TextLength(row, "value")))
            : 0;

        static JsonElement? Author(JsonElement embed) =>
            embed.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object ? author : null;

        static int TextLength(JsonElement json, string name) => json.TryGetProperty(name, out var text) ? text.GetString()!.Length : 0;

        IEnumerable<JsonElement> Buttons => Message.TryGetProperty("components", out var rows)
            ? rows.EnumerateArray().SelectMany(row => row.GetProperty("components").EnumerateArray())
            : [];

        public IEnumerable<string> ButtonIds => Buttons.Select(button => button.GetProperty("custom_id").GetString()!);

        public IEnumerable<string> DisabledButtonIds => Buttons
            .Where(button => button.TryGetProperty("disabled", out var disabled) && disabled.GetBoolean())
            .Select(button => button.GetProperty("custom_id").GetString()!);

        public IEnumerable<(string Name, string Value)> Choices => Message.GetProperty("choices").EnumerateArray()
            .Select(choice => (choice.GetProperty("name").GetString()!, choice.GetProperty("value").GetString()!));
    }

    // Runs the bot's real handlers on the real Discord.Net pipeline, with only the network and the data layer faked.
    // Tests send the gateway JSON that Discord would send, then read what the bot did in response.
    internal sealed class FakeDiscord : IAsyncDisposable
    {
        public const ulong BotId = 1000, GuildId = 2000, JoinedGuildId = 2100, BotRoleId = 2101, ChannelId = 3000, DeletedChannelId = 3001, DirectChannelId = 3002, JoinedChannelId = 3100, MessageId = 4000, TeamId = 7000;

        // Roles of the first guild, which a member only has when a test sets MemberRoles. The last one is denied Pin Messages in ChannelId.
        public const ulong PinnerRoleId = 2001, ChannelManagerRoleId = 2002, DeniedPinnerRoleId = 2003;

        // The bot's role in the first guild gives it View Channel, Send Messages and Embed Links, and these channels there take one away.
        // HiddenChannelId is hidden from @everyone, the way Discord sends a channel the bot can't view.
        public const ulong PosterRoleId = 2004, NoSendChannelId = 3003, HiddenChannelId = 3004, NoEmbedChannelId = 3005, AnnouncementChannelId = 3006;

        // A role of the joined guild without Administrator, with only View Channel and Send Messages
        public const ulong JoinedPosterRoleId = 2102;

        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

        static long lastUserId = 5000;

        readonly DiscordSocketClient client;
        readonly InteractionService interactions;
        readonly ServiceProvider services;
        readonly FakeGateway gateway;
        readonly List<Step> steps = new();
        readonly TextWriter realConsole = Console.Out;
        readonly ConsoleCapture console = new();

        // For the memory cache and the notification job
        readonly FakeClock clock = new();
        TaskCompletionSource executed = NewSignal();
        int gatewaySequence;

        // The first user owns the guild, so it passes the admin checks. The other one has no permissions.
        public ulong UserId { get; }
        public ulong OtherUserId { get; }

        // Roles of the member who sends an interaction in the guild
        public ulong[] MemberRoles { get; set; } = [];

        public FakeGetData Steam { get; }
        public FakeArmorData Armor { get; }
        public FakeGuildData Guilds { get; }
        public NotificationState Notifications => services.GetRequiredService<NotificationState>();
        public ArmorService ArmorService => services.GetRequiredService<ArmorService>();

        // What the bot recorded for its status and history, and what its status sampler would read of Discord
        public BotRecorder Recorder => services.GetRequiredService<BotRecorder>();
        public DiscordStatusHandler DiscordStatus => services.GetRequiredService<DiscordStatusHandler>();

        // The servers that changed, as the bot notes them for its server writer, and Discord.Net's cache, which the writer reads
        public GuildWatcher Watcher => services.GetRequiredService<GuildWatcher>();
        public DiscordSocketClient Client => client;

        // The bot's clock, which tests can set
        public FakeClock Clock => clock;

        // Discord's approximate member count of each server, which its API gives the daily count, 42 for any other
        public Dictionary<ulong, int> MemberCounts { get; } = new();

        // Servers whose count Discord never sends, as for a request stuck behind a rate limit, whatever its cancel token says
        public HashSet<ulong> UnansweredGuilds { get; } = new();

        // Holds Discord's answer to each message the bot sends until it completes, as for a slow request
        public Task? SendHold { get; set; }

        // Holds Discord's answer to the bot leaving a server until it completes, as for a slow request, and the gateway then
        // isn't told: the bot is still in the server
        public Task? LeaveHold { get; set; }

        // Servers Discord lists with the session as unavailable, in an outage, and doesn't send
        ulong[] unavailableAtStart;

        // Name of the Discord request that gets refused, to test what happens when a reply can't be sent
        public string? FailStep { get; set; }

        // How many requests of FailStep get through before the refusals start
        public int FailStepAfter;

        public int ExecutedEvents;

        // Holds back READY, the way a slow gateway does, until ReleaseReadyAsync
        readonly TaskCompletionSource? readyGate;
        TaskCompletionSource ready = NewSignal();

        // Set to make the bot's application belong to a team with this owner
        public ulong? TeamOwnerId { get; set; }

        // The bot's settings, which start empty, the way environment variables reach it in Program.cs
        public IConfiguration Settings => services.GetRequiredService<IConfiguration>();

        public string ConsoleText => console.ToString();

        // Group and name of every slash command the bot loaded, such as "armor list", or "help" for a top-level one
        public IEnumerable<string> LoadedSlashCommands => interactions.SlashCommands.Select(command => (command.Module.SlashGroupName + " " + command.Name).Trim());

        // The JSON the bot sent to register its commands with Discord
        public string? RegisteredCommands { get; private set; }

        public IReadOnlyList<Step> Steps
        {
            get
            {
                lock (steps)
                    return steps.ToList();
            }
        }

        public IReadOnlyList<string> StepNames => Steps.Select(step => step.Name).ToList();

        public Step Single(string name) => Assert.Single(Steps, step => step.Name == name);

        // The bot's services, as Program.cs registers them, for the tests that build the request worker and its jobs on them
        public IServiceProvider Services => services;

        // The presence the bot sent with each IDENTIFY and in each presence update, in order, while the socket was open: a
        // frame sent while it's closed is dropped, as Discord.Net's socket drops it
        public IReadOnlyList<SentPresence> Presences
        {
            get
            {
                lock (presences)
                    return presences.ToList();
            }
        }

        readonly List<SentPresence> presences = new();

        // How many times Discord.Net raised Connected, which it does after a READY and after a RESUMED
        public int Connections => Volatile.Read(ref connections);
        int connections;

        FakeDiscord(bool holdReady, ulong[] unavailableGuilds)
        {
            readyGate = holdReady ? NewSignal() : null;
            unavailableAtStart = unavailableGuilds;

            // Every run is a new pair of users, because cooldowns are static
            UserId = (ulong)Interlocked.Add(ref lastUserId, 2);
            OtherUserId = UserId + 1;

            Steam = new FakeGetData(AddStep);
            Armor = new FakeArmorData(AddStep);
            Guilds = new FakeGuildData(AddStep);
            gateway = new FakeGateway(this);

            client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayHost = "wss://gateway.invalid",
                GatewayIntents = GatewayIntents.Guilds,
                LogGatewayIntentWarnings = false,
                LogLevel = LogSeverity.Debug,
                // How long Discord.Net waits for a server listed as unavailable before Ready, which the bot leaves at 10 seconds
                MaxWaitBetweenGuildAvailablesBeforeReady = 1000,
                RestClientProvider = _ => new FakeRest(this),
                WebSocketProvider = () => gateway
            });
            interactions = new InteractionService(client);

            client.Log += log =>
            {
                console.WriteLine(log);
                return Task.CompletedTask;
            };

            // The same registrations as Program.cs, with fakes for the data layer
            services = new ServiceCollection()
                .AddSingleton(client)
                .AddSingleton(interactions)
                .AddSingleton<InteractionHandler>()
                .AddSingleton<GuildEventHandler>()
                .AddWebClient()
                .AddSingleton<AnnounceService>()
                .AddSingleton<OwnerAlertService>()
                .AddSingleton<ArmorService>()
                .AddSingleton<SteamAPIService>()
                .AddSingleton<DiscordService>()
                .AddSingleton<NotificationState>()
                .AddSingleton<BotRecorder>()
                .AddSingleton<DiscordStatusHandler>()
                .AddSingleton<IDiscordStatus>(provider => provider.GetRequiredService<DiscordStatusHandler>())
                .AddSingleton<GuildWatcher>()
                .AddSingleton<PostsPause>()
                .AddSingleton<PresenceKeeper>()
                .AddSingleton<CacheReset>()
                .AddSingleton<TimeProvider>(clock)
                .AddSingleton<IGetData>(Steam)
                .AddSingleton<IArmorData>(Armor)
                .AddSingleton<IGuildData>(Guilds)
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build())
                .AddLogging()
                .AddMemoryCache(options => options.Clock = clock)
                .BuildServiceProvider();
        }

        public static async Task<FakeDiscord> StartAsync(bool holdReady = false, ulong[]? unavailableGuilds = null)
        {
            var discord = new FakeDiscord(holdReady, unavailableGuilds ?? []);
            await discord.ConnectAsync();
            return discord;
        }

        public async Task ReleaseReadyAsync()
        {
            readyGate!.SetResult();
            await WaitAsync(ready.Task);
            await SyncedAsync();
            await ConnectedAsync();
        }

        // A new session, as after the bot was offline: the gateway stopped and started again, with Discord listing these guilds
        // as unavailable, in an outage, the first guild too when it's one of them
        public async Task StartNewSessionAsync(params ulong[] unavailableGuilds)
        {
            await StopGatewayAsync();

            unavailableAtStart = unavailableGuilds;
            ready = NewSignal();

            await client.StartAsync();
            await WaitAsync(ready.Task);
            await SyncedAsync();
            await ConnectedAsync();
        }

        // Discord.Net's stop only starts the disconnect, which raises GuildUnavailable for every guild before it ends
        public async Task StopGatewayAsync()
        {
            await client.StopAsync();

            var waited = System.Diagnostics.Stopwatch.StartNew();

            while (client.ConnectionState != ConnectionState.Disconnected)
            {
                if (waited.Elapsed > WaitLimit)
                    throw new TimeoutException($"The gateway never disconnected. Console output: {ConsoleText}");

                await Task.Delay(10);
            }
        }

        // The gateway's connection dropping, as in a short network outage: Discord.Net connects again and resumes the session,
        // which Discord answers with RESUMED rather than READY, and which carries nothing of the bot's presence
        public async Task DropAndResumeAsync()
        {
            var before = Connections;
            gateway.Resumes = true;

            try
            {
                await gateway.DropAsync();

                var waited = System.Diagnostics.Stopwatch.StartNew();

                while (Connections == before || client.ConnectionState != ConnectionState.Connected)
                {
                    if (waited.Elapsed > WaitLimit)
                        throw new TimeoutException($"The gateway never resumed. Console output: {ConsoleText}");

                    await Task.Delay(10);
                }
            }
            finally
            {
                gateway.Resumes = false;
            }
        }

        // Discord.Net marks a guild synced on a task of its own once it has taken the guild, and until then raises no event for
        // its channels, roles or members, so tests that send those right away wait for every guild the session sent whole. One
        // sent with nothing but its ID, in an outage, never syncs, though Discord.Net counts it as connected; it has no name.
        Task SyncedAsync() => WaitAsync(Task.WhenAll(client.Guilds.Where(guild => guild.IsConnected && guild.Name != null).Select(guild => guild.SyncPromise)));

        // Discord.Net raises Ready and marks the gateway connected on tasks of their own, in either order, so tests whose
        // first step needs the gateway connected, as the server writer's ticks do, wait for that too
        async Task ConnectedAsync()
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();

            while (client.ConnectionState != ConnectionState.Connected)
            {
                if (waited.Elapsed > WaitLimit)
                    throw new TimeoutException($"The gateway never connected. Console output: {ConsoleText}");

                await Task.Delay(10);
            }
        }

        // A GUILD_CREATE with all of a guild, which Discord.Net marks synced the same way
        async Task DeliverGuildAsync(ulong guildId, string json)
        {
            await gateway.DeliverAsync("GUILD_CREATE", json);
            await WaitAsync(client.GetGuild(guildId)?.SyncPromise ?? Task.CompletedTask);
        }

        async Task ConnectAsync()
        {
            Console.SetOut(console);

            // In Program.cs's order: the presence first, then the status handler's Ready, then the server watcher's handlers
            await services.GetRequiredService<PresenceKeeper>().InitializeAsync();
            services.GetRequiredService<DiscordStatusHandler>().Initialize();
            services.GetRequiredService<GuildWatcher>().Initialize();
            await services.GetRequiredService<InteractionHandler>().InitializeAsync();
            services.GetRequiredService<GuildEventHandler>().Initialize();

            // InitializeAsync looks in the entry assembly, which is the test runner here
            await interactions.AddModulesAsync(typeof(InteractionHandler).Assembly, services);

            // Added after the bot's own handlers, so these fire once those are done
            interactions.InteractionExecuted += (_, _, _) => MarkExecuted();

            // Autocomplete handlers don't raise InteractionExecuted
            interactions.AutocompleteHandlerExecuted += (_, _, _) => MarkExecuted();

            client.Ready += () =>
            {
                ready.TrySetResult();
                return Task.CompletedTask;
            };

            client.Connected += () =>
            {
                Interlocked.Increment(ref connections);
                return Task.CompletedTask;
            };

            await client.LoginAsync(TokenType.Bot, "fake token", validateToken: false);
            await client.StartAsync();

            if (readyGate == null)
            {
                await WaitAsync(ready.Task);
                await SyncedAsync();
                await ConnectedAsync();
            }
        }

        // A timeout here means the bot never finished, and the console usually says why
        async Task WaitAsync(Task task)
        {
            try
            {
                await task.WaitAsync(WaitLimit);
            }
            catch (TimeoutException e)
            {
                throw new TimeoutException($"Timed out. Console output: {ConsoleText}", e);
            }
        }

        Task MarkExecuted()
        {
            Interlocked.Increment(ref ExecutedEvents);
            executed.TrySetResult();
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await client.StopAsync();
            await client.DisposeAsync();
            await services.DisposeAsync();
            Console.SetOut(realConsole);
        }

        // A presence's first activity, which is the only one Discord.Net sends
        void AddPresence(int op, JsonElement? presence)
        {
            JsonElement activity = default;
            var has = presence is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("activities", out var activities)
                && activities.ValueKind == JsonValueKind.Array && activities.GetArrayLength() > 0 && (activity = activities[0]).ValueKind == JsonValueKind.Object;

            string? Text(string name) => has && activity.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;

            var sent = new SentPresence(op, Text("name"), has && activity.TryGetProperty("type", out var type) ? type.GetInt32() : null, Text("state"));

            lock (presences)
                presences.Add(sent);
        }

        void AddStep(string name) => AddStep(new Step(name));

        void AddStep(Step step)
        {
            lock (steps)
                steps.Add(step);
        }

        static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Sending

        // /group subcommand with options like {"name":"map-url","type":3,"value":"x"}
        public Task SendSlashCommandAsync(string group, string subcommand, string options = "", string resolved = "{}", ulong? userId = null, TimeSpan age = default, bool direct = false)
        {
            var data = $$"""{"id":"1","name":"{{group}}","type":1,"options":[{"name":"{{subcommand}}","type":1,"options":[{{options}}]}],"resolved":{{resolved}}}""";
            return SendInteractionAsync(type: 2, data, userId ?? UserId, age, direct: direct);
        }

        // /admin set_channels with these options. The channels have to be in the first guild's cache, which is where Discord.Net
        // takes them from, as for an unknown one it asks Discord over REST, and that fails the interaction here.
        public Task SendSetChannelsAsync(params (string Option, ulong ChannelId)[] channels)
        {
            var options = channels.Select(channel => $$"""{"name":"{{channel.Option}}","type":7,"value":"{{channel.ChannelId}}"}""");
            var resolved = channels.Select(channel => channel.ChannelId).Distinct().Select(id => $$"""
                "{{id}}":{"id":"{{id}}","type":{{(id == AnnouncementChannelId ? 5 : 0)}},"name":"channel","permissions":"0"}
                """);

            return SendSlashCommandAsync("admin", "set_channels", string.Join(",", options),
                channels.Length == 0 ? "{}" : $$"""{"channels":{ {{string.Join(",", resolved)}} } }""");
        }

        // /name, with no group
        public Task SendTopLevelCommandAsync(string name, bool direct = false)
        {
            var data = $$"""{"id":"1","name":"{{name}}","type":1}""";
            return SendInteractionAsync(type: 2, data, UserId, default, direct: direct);
        }

        // What Discord sends while the user types in an option of /group subcommand
        public Task SendAutocompleteAsync(string group, string subcommand, string option, string typed, bool direct = false)
        {
            var data = $$"""{"id":"1","name":"{{group}}","type":1,"options":[{"name":"{{subcommand}}","type":1,"options":[{"name":"{{option}}","type":3,"value":{{JsonSerializer.Serialize(typed)}},"focused":true}]}]}""";
            return SendInteractionAsync(type: 4, data, UserId, default, direct: direct);
        }

        // A click on a button of the bot's message
        public Task SendButtonAsync(string customId, ulong? userId = null, bool direct = false)
        {
            var data = $$"""{"custom_id":{{JsonSerializer.Serialize(customId)}},"component_type":2}""";
            return SendInteractionAsync(type: 3, data, userId ?? UserId, default, $$""","message":{{MessageJson(channelId: direct ? DirectChannelId : ChannelId)}}""", direct);
        }

        public Task SendMessageCommandAsync(string name, int messageType = 0, ulong? userId = null, bool direct = false)
        {
            var message = MessageJson(messageType, channelId: direct ? DirectChannelId : ChannelId);
            var data = $$"""{"id":"1","name":"{{name}}","type":3,"target_id":"{{MessageId}}","resolved":{"messages":{"{{MessageId}}":{{message}} } } }""";
            return SendInteractionAsync(type: 2, data, userId ?? UserId, default, direct: direct);
        }

        // A member of a resolved list has no user field, Discord sends the user next to it
        public Task SendUserCommandAsync(string name, ulong targetId, ulong? userId = null)
        {
            var member = """{"roles":[],"joined_at":"2020-01-01T00:00:00+00:00","deaf":false,"mute":false,"permissions":"0"}""";
            var data = $$"""{"id":"1","name":"{{name}}","type":2,"target_id":"{{targetId}}","resolved":{"users":{"{{targetId}}":{{UserJson(targetId)}} },"members":{"{{targetId}}":{{member}} } } }""";
            return SendInteractionAsync(type: 2, data, userId ?? UserId, default);
        }

        // Autocomplete for a slash command that doesn't exist, which is the only way the handler sees one
        public Task SendUnknownAutocompleteAsync()
        {
            var data = """{"id":"1","name":"unknown","type":1,"options":[{"name":"value","type":3,"value":"a","focused":true}]}""";
            return SendInteractionAsync(type: 4, data, UserId, default);
        }

        Task SendInteractionAsync(int type, string data, ulong userId, TimeSpan age, string extraFields = "", bool direct = false)
        {
            // Discord.Net reads the age of an interaction from its ID
            var id = SnowflakeUtils.ToSnowflake(DateTimeOffset.UtcNow - age);

            // In a direct message there's no guild, and a user instead of a member
            var place = direct
                ? $$"""
                  "channel_id":"{{DirectChannelId}}","channel":{{DirectChannelJson(userId)}},"user":{{UserJson(userId)}}
                  """
                : $$"""
                  "guild_id":"{{GuildId}}","channel_id":"{{ChannelId}}","channel":{"id":"{{ChannelId}}","type":0},"member":{{MemberJson(userId)}}
                  """;

            return SendAsync("INTERACTION_CREATE", $$"""
                {"id":"{{id}}","application_id":"{{BotId}}","type":{{type}},"token":"interaction-token","version":1,
                 {{place}},"data":{{data}},"entitlements":[],"app_permissions":"0"{{extraFields}}}
                """);
        }

        async Task SendAsync(string eventName, string json)
        {
            executed = NewSignal();

            await gateway.DeliverAsync(eventName, json);
            await WaitAsync(executed.Task);
        }

        // One run the way JobHandler does it, with a new job object, 5 minutes after the last run
        public async Task RunNotificationJobAsync()
        {
            clock.UtcNow += TimeSpan.FromMinutes(5);

            var run = ActivatorUtilities.CreateInstance<GuildNotificationJob>(services).NotifyAllGuilds();

            // Some tests expect the job's own TimeoutException, so a job that never finishes fails with something else
            try
            {
                await run.WaitAsync(WaitLimit);
            }
            catch (TimeoutException) when (!run.IsCompleted)
            {
                Assert.Fail($"The notification job didn't finish. Console output: {ConsoleText}");
            }
        }

        // For the map cooldown, which is in hours, without running the job every 5 minutes of them
        public void MoveClock(TimeSpan time) => clock.UtcNow += time;

        public Task LeaveGuildAsync() => gateway.DeliverAsync("GUILD_DELETE", $$"""{"id":"{{GuildId}}"}""");

        // Any dispatch, as Discord sends it. It returns once Discord.Net has processed it, the bot's handlers included.
        public Task DeliverAsync(string eventName, string json) => gateway.DeliverAsync(eventName, json);

        // GUILD_UPDATE with a new name, with the guild's roles, as Discord's guild object carries them
        public Task UpdateGuildAsync(string name) => gateway.DeliverAsync("GUILD_UPDATE", GuildJson(name));

        // A guild channel as CHANNEL_CREATE, CHANNEL_UPDATE and CHANNEL_DELETE carry it, with its guild and category
        public static string ChannelEventJson(ulong guildId, ulong id, int position, string overwrites = "", int type = 0, string? name = null, ulong? parentId = null) => $$"""
            {"id":"{{id}}","type":{{type}},"guild_id":"{{guildId}}","name":"{{name ?? $"channel-{id}"}}","position":{{position}},"parent_id":{{IdJson(parentId)}},"permission_overwrites":[{{overwrites}}]}
            """;

        // A role as GUILD_ROLE_CREATE and GUILD_ROLE_UPDATE carry it
        public static string RoleEventJson(ulong guildId, ulong roleId, string name, GuildPermission permissions) =>
            $$"""{"guild_id":"{{guildId}}","role":{{RoleJson(roleId, name, permissions)}}}""";

        // The bot's own member with these roles, which Discord sends without the members intent
        public static string BotMemberUpdateJson(ulong guildId, params ulong[] roles) =>
            $$"""{"guild_id":"{{guildId}}","roles":[{{string.Join(",", roles.Select(role => $"\"{role}\""))}}],"user":{{UserJson(BotId, bot: true)}},"joined_at":"2026-01-01T00:00:00+00:00","deaf":false,"mute":false,"flags":0}""";

        // Another member, which Discord would only send with the members intent
        public static string MemberUpdateJson(ulong guildId, ulong userId) =>
            $$"""{"guild_id":"{{guildId}}","roles":[],"user":{{UserJson(userId)}},"joined_at":"2026-01-01T00:00:00+00:00","deaf":false,"mute":false,"flags":0}""";

        // The bot's member the way Discord lists it with a guild. In the joined guild BotRoleId gives it Administrator,
        // and JoinedPosterRoleId only View Channel and Send Messages.
        public static string BotMemberJson(params ulong[] roles) => $$"""
            {"user":{{UserJson(BotId, bot: true)}},"roles":[{{string.Join(",", roles.Select(role => $"\"{role}\""))}}],"joined_at":"2026-01-01T00:00:00+00:00","deaf":false,"mute":false}
            """;

        // Being added to a server, which has only its general channel unless a test gives it a JSON list of channels.
        // An "unavailable":false here would make it an existing guild coming back, and JoinedGuild wouldn't fire.
        public Task JoinGuildAsync(string members, string? channels = null, ulong? systemChannelId = JoinedChannelId, ulong? publicUpdatesChannelId = null) =>
            DeliverGuildAsync(JoinedGuildId, $$"""
            {"id":"{{JoinedGuildId}}","name":"New guild","owner_id":"{{OtherUserId}}","verification_level":0,"default_message_notifications":0,
             "explicit_content_filter":0,"mfa_level":0,"premium_tier":0,"nsfw_level":0,"features":[],"emojis":[],"stickers":[],
             "system_channel_id":{{IdJson(systemChannelId)}},"public_updates_channel_id":{{IdJson(publicUpdatesChannelId)}},
             "roles":[{"id":"{{JoinedGuildId}}","name":"@everyone","permissions":"0","position":0,"color":0,"hoist":false,"managed":false,"mentionable":false},
                      {"id":"{{BotRoleId}}","name":"MinerBot","permissions":"8","position":1,"color":0,"hoist":false,"managed":true,"mentionable":false,"tags":{"bot_id":"{{BotId}}"} },
                      {{RoleJson(JoinedPosterRoleId, "MinerBot poster", GuildPermission.ViewChannel | GuildPermission.SendMessages)}}],
             "channels":{{channels ?? $"[{ChannelJson(JoinedChannelId, 0, name: "general")}]"}},
             "member_count":2,"large":false,"members":{{members}},"presences":[],"threads":[],"voice_states":[],"guild_scheduled_events":[],
             "joined_at":"2026-01-01T00:00:00+00:00"}
            """);

        // A guild channel, 0 is a text channel. Discord.Net reads the name of every channel.
        public static string ChannelJson(ulong id, int position, string overwrites = "", int type = 0, string? name = null) => $$"""
            {"id":"{{id}}","type":{{type}},"name":"{{name ?? $"channel-{id}"}}","position":{{position}},"permission_overwrites":[{{overwrites}}]}
            """;

        // An overwrite that takes a permission away from a role in a channel. The id of @everyone is the guild's own.
        public static string DenyJson(ulong roleId, ChannelPermission permission) =>
            $$"""{"id":"{{roleId}}","type":0,"allow":"0","deny":"{{(ulong)permission}}"}""";

        static string IdJson(ulong? id) => id == null ? "null" : $"\"{id}\"";

        // A GUILD_CREATE during an outage, which has nothing but the guild's id. Discord.Net takes it as a join, even for a guild it has.
        public Task SendUnavailableGuildAsync(ulong guildId) => gateway.DeliverAsync("GUILD_CREATE", UnavailableGuildJson(guildId));

        // An outage in the first guild, and then its end, when Discord sends all of the guild again the way it does after a start,
        // with the name it may have been given meanwhile
        public Task MakeGuildUnavailableAsync() => gateway.DeliverAsync("GUILD_DELETE", $$"""{"id":"{{GuildId}}","unavailable":true}""");
        public Task MakeGuildAvailableAsync(string name = "Guild") => DeliverGuildAsync(GuildId, GuildJson(name));

        public Task SendOwnerAlertAsync(string message) => services.GetRequiredService<OwnerAlertService>().SendAsync(message);

        // Gateway JSON

        static string UserJson(ulong id, bool bot = false) =>
            $$"""{"id":"{{id}}","username":"user{{id}}","discriminator":"0","global_name":null,"avatar":null,"bot":{{(bot ? "true" : "false")}}}""";

        // The first user owns the application, unless it belongs to a team
        string ApplicationJson() => $$"""
            {"id":"{{BotId}}","name":"MinerBot","icon":null,"description":"","bot_public":true,"bot_require_code_grant":false,"verify_key":"key","flags":0,
             "owner":{{UserJson(TeamOwnerId == null ? UserId : TeamId)}},
             "team":{{(TeamOwnerId == null ? "null" : TeamJson(TeamOwnerId.Value))}}}
            """;

        static string TeamJson(ulong ownerId) => $$"""
            {"id":"{{TeamId}}","name":"Team","icon":null,"owner_user_id":"{{ownerId}}",
             "members":[{"membership_state":2,"permissions":["*"],"team_id":"{{TeamId}}","user":{{UserJson(ownerId)}}}]}
            """;

        // A guild as Discord's API gives it with its counts. Discord.Net reads its preferred locale.
        static string RestGuildJson(ulong id, int members) => $$"""
            {"id":"{{id}}","name":"Guild","icon":null,"owner_id":"1","verification_level":0,"default_message_notifications":0,"explicit_content_filter":0,
             "mfa_level":0,"premium_tier":0,"nsfw_level":0,"features":[],"emojis":[],"stickers":[],
             "roles":[{"id":"{{id}}","name":"@everyone","permissions":"0","position":0,"color":0,"hoist":false,"managed":false,"mentionable":false}],
             "preferred_locale":"en-US","approximate_member_count":{{members}},"approximate_presence_count":1}
            """;

        static string DirectChannelJson(ulong userId) => $$"""
            {"id":"{{DirectChannelId}}","type":1,"last_message_id":null,"recipients":[{{UserJson(userId)}}]}
            """;

        string MemberJson(ulong id) =>
            $$"""{"user":{{UserJson(id)}},"roles":[{{string.Join(",", MemberRoles.Select(role => $"\"{role}\""))}}],"joined_at":"2020-01-01T00:00:00+00:00","deaf":false,"mute":false,"permissions":"0"}""";

        static string RoleJson(ulong id, string name, GuildPermission permissions) =>
            $$"""{"id":"{{id}}","name":"{{name}}","permissions":"{{(ulong)permissions}}","position":1,"color":0,"hoist":false,"managed":false,"mentionable":false}""";

        static string MessageJson(int type = 0, ulong channelId = ChannelId) => $$"""
            {"id":"{{MessageId}}","channel_id":"{{channelId}}","author":{{UserJson(BotId, bot: true)}},"content":"",
             "timestamp":"2026-01-01T00:00:00+00:00","tts":false,"mention_everyone":false,"mentions":[],"mention_roles":[],
             "attachments":[],"embeds":[],"pinned":false,"type":{{type}}}
            """;

        // Discord sends only each guild's id here and the rest in a GUILD_CREATE right after, which leaves Discord.Net with the same guild.
        // A guild in an outage stays that way, the first guild too when the session lists it so.
        string ReadyJson() => $$"""
            {"v":10,"user":{{UserJson(BotId, bot: true)}},"session_id":"session","resume_gateway_url":"wss://gateway.invalid",
             "private_channels":[],"application":{"id":"{{BotId}}","flags":0},
             "guilds":[{{string.Join(",", unavailableAtStart.Contains(GuildId)
                 ? unavailableAtStart.Select(UnavailableGuildJson)
                 : unavailableAtStart.Select(UnavailableGuildJson).Prepend(GuildJson()))}}]}
            """;

        // A guild in an outage, of which Discord sends nothing but its id
        static string UnavailableGuildJson(ulong id) => $$"""{"id":"{{id}}","unavailable":true}""";

        // The first guild. Discord lists the bot's own member with every guild, even with only the Guilds intent. The owner makes it 2 members.
        string GuildJson(string name = "Guild") => $$"""
            {"id":"{{GuildId}}","name":"{{name}}","owner_id":"{{UserId}}","verification_level":0,"default_message_notifications":0,
             "explicit_content_filter":0,"mfa_level":0,"premium_tier":0,"nsfw_level":0,"features":[],"emojis":[],"stickers":[],
             "roles":[{"id":"{{GuildId}}","name":"@everyone","permissions":"0","position":0,"color":0,"hoist":false,"managed":false,"mentionable":false},
                      {{RoleJson(PinnerRoleId, "Pinner", GuildPermission.ViewChannel | GuildPermission.PinMessages)}},
                      {{RoleJson(ChannelManagerRoleId, "Channel manager", GuildPermission.ViewChannel | GuildPermission.ManageChannels)}},
                      {{RoleJson(DeniedPinnerRoleId, "Pinner denied in general", GuildPermission.ViewChannel | GuildPermission.PinMessages)}},
                      {{RoleJson(PosterRoleId, "MinerBot", GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks)}}],
             "channels":[{"id":"{{ChannelId}}","type":0,"name":"general","position":0,
                          "permission_overwrites":[{"id":"{{DeniedPinnerRoleId}}","type":0,"allow":"0","deny":"{{(ulong)ChannelPermission.PinMessages}}"}]},
                         {{ChannelJson(NoSendChannelId, 1, DenyJson(PosterRoleId, ChannelPermission.SendMessages))}},
                         {{ChannelJson(HiddenChannelId, 2, DenyJson(GuildId, ChannelPermission.ViewChannel), name: "___hidden___")}},
                         {{ChannelJson(NoEmbedChannelId, 3, DenyJson(PosterRoleId, ChannelPermission.EmbedLinks))}},
                         {{ChannelJson(AnnouncementChannelId, 4, type: 5)}}],
             "unavailable":false,"member_count":2,"large":false,"members":[{{BotMemberJson(PosterRoleId)}}],"presences":[],"threads":[],
             "voice_states":[],"guild_scheduled_events":[],"joined_at":"2020-01-01T00:00:00+00:00"}
            """;

        // The bot logs from many threads while tests read the text
        sealed class ConsoleCapture : TextWriter
        {
            readonly ConcurrentQueue<string> parts = new();

            public override Encoding Encoding => Encoding.UTF8;
            public override void Write(char value) => parts.Enqueue(value.ToString());
            public override void Write(string? value) => parts.Enqueue(value ?? "");
            public override string ToString() => string.Concat(parts);
        }

        // Stands in for the gateway connection, it answers the login and delivers the events of the test
        sealed class FakeGateway(FakeDiscord discord) : IWebSocketClient
        {
            volatile bool open;

            public event Func<byte[], int, int, Task>? BinaryMessage { add { } remove { } }
            public event Func<string, Task>? TextMessage;
            public event Func<Exception, Task>? Closed;

            // Whether a resume is answered with RESUMED, as after a short outage, rather than with a new session
            public volatile bool Resumes;

            public void SetHeader(string key, string value) { }
            public void SetCancelToken(CancellationToken cancelToken) { }

            public Task ConnectAsync(string host)
            {
                open = true;
                return Task.CompletedTask;
            }

            public Task DisconnectAsync(int closeCode = 1000)
            {
                open = false;
                return Task.CompletedTask;
            }

            public void Dispose() { }

            // The socket closing under Discord.Net, which then connects again by itself
            public Task DropAsync()
            {
                open = false;
                return Closed?.Invoke(new WebSocketClosedException(4000)) ?? Task.CompletedTask;
            }

            public Task SendAsync(byte[] data, int index, int count, bool isText)
            {
                using var json = JsonDocument.Parse(data.AsMemory(index, count));

                const int identifyOpCode = 2, presenceOpCode = 3, resumeOpCode = 6;
                var opCode = json.RootElement.GetProperty("op").GetInt32();

                // Discord.Net's socket drops a frame while it's closed, without an error
                if (open && opCode == identifyOpCode)
                    discord.AddPresence(opCode, json.RootElement.GetProperty("d").TryGetProperty("presence", out var presence) ? presence : null);

                if (open && opCode == presenceOpCode)
                    discord.AddPresence(opCode, json.RootElement.GetProperty("d"));

                if (opCode == identifyOpCode)
                    _ = Task.Run(() => LoginAsync());

                // Unless the test lets it resume, the session can't be, so Discord.Net identifies and gets a new one, as after the
                // bot was offline
                if (opCode == resumeOpCode)
                    _ = Task.Run(() => Resumes ? ResumeAsync() : RaiseAsync("""{"op":9,"d":false}"""));

                return Task.CompletedTask;
            }

            async Task ResumeAsync()
            {
                await RaiseAsync("""{"op":10,"d":{"heartbeat_interval":3600000}}""");
                await DeliverAsync("RESUMED", "{}");
            }

            async Task LoginAsync()
            {
                await RaiseAsync("""{"op":10,"d":{"heartbeat_interval":3600000}}""");

                if (discord.readyGate != null)
                    await discord.readyGate.Task;

                await DeliverAsync("READY", discord.ReadyJson());
            }

            public Task DeliverAsync(string eventName, string json) =>
                RaiseAsync($$"""{"op":0,"s":{{Interlocked.Increment(ref discord.gatewaySequence)}},"t":"{{eventName}}","d":{{json}}}""");

            Task RaiseAsync(string frame) => TextMessage?.Invoke(frame) ?? Task.CompletedTask;
        }

        // Stands in for Discord's HTTP API, it records what the bot sends and answers with an empty success
        sealed class FakeRest(FakeDiscord discord) : IRestClient
        {
            public void SetHeader(string key, string? value) { }
            public void SetCancelToken(CancellationToken cancelToken) { }
            public void Dispose() { }

            public Task<RestResponse> SendAsync(string method, string endpoint, CancellationToken cancelToken, bool headerOnly = false, string? reason = null,
                IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders = null) => Answer(method, endpoint, "");

            public Task<RestResponse> SendAsync(string method, string endpoint, string json, CancellationToken cancelToken, bool headerOnly = false, string? reason = null,
                IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders = null) => Answer(method, endpoint, json);

            public Task<RestResponse> SendAsync(string method, string endpoint, IReadOnlyDictionary<string, object> multipartParams, CancellationToken cancelToken, bool headerOnly = false, string? reason = null,
                IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders = null)
            {
                // Discord.Net's MultipartFile is internal
                var upload = multipartParams["files[0]"];
                using var file = new StreamReader((Stream)upload.GetType().GetProperty("Stream")!.GetValue(upload)!);

                return Answer(method, endpoint, multipartParams["payload_json"].ToString()!, file.ReadToEnd());
            }

            Task<RestResponse> Answer(string method, string endpoint, string body, string? file = null)
            {
                var name = StepName(method, endpoint, body);

                if (name != null)
                    discord.AddStep(new Step(name, body, file, endpoint));

                if (name != null && name == discord.FailStep && Interlocked.Decrement(ref discord.FailStepAfter) < 0)
                    return Response(HttpStatusCode.Forbidden, """{"code":50013,"message":"Missing Permissions"}""");

                if (method == "PUT" && endpoint.EndsWith("/commands"))
                    discord.RegisteredCommands = body;

                if (endpoint == "users/@me")
                    return Response(HttpStatusCode.OK, UserJson(BotId, bot: true));

                if (endpoint == "oauth2/applications/@me")
                    return Response(HttpStatusCode.OK, discord.ApplicationJson());

                if (endpoint == "users/@me/channels")
                    return Response(HttpStatusCode.OK, DirectChannelJson(ulong.Parse(JsonDocument.Parse(body).RootElement.GetProperty("recipient_id").GetString()!)));

                // The bot leaving a server: Discord answers with nothing, then tells the gateway, which removes the server
                if (method == "DELETE" && Regex.Match(endpoint, @"^users/@me/guilds/(\d+)$") is { Success: true } left)
                {
                    if (discord.LeaveHold is { } leaveHold)
                        return HeldLeaveAsync(leaveHold);

                    var leftId = left.Groups[1].Value;
                    _ = Task.Run(() => discord.gateway.DeliverAsync("GUILD_DELETE", $$"""{"id":"{{leftId}}"}"""));

                    return Response(HttpStatusCode.NoContent, "");
                }

                if (endpoint.StartsWith("users/"))
                    return Response(HttpStatusCode.OK, UserJson(ulong.Parse(endpoint["users/".Length..])));

                if (endpoint == $"channels/{DeletedChannelId}")
                    return Response(HttpStatusCode.NotFound, """{"code":10003,"message":"Unknown Channel"}""");

                if (Regex.Match(endpoint, @"^guilds/(\d+)\?with_counts=true$") is { Success: true } guild)
                {
                    var id = ulong.Parse(guild.Groups[1].Value);

                    if (discord.UnansweredGuilds.Contains(id))
                        return new TaskCompletionSource<RestResponse>().Task;

                    return Response(HttpStatusCode.OK, RestGuildJson(id, discord.MemberCounts.GetValueOrDefault(id, 42)));
                }

                if (endpoint.Contains("/commands") || endpoint.EndsWith("/pins"))
                    return Response(HttpStatusCode.OK, "[]");

                if (name == "send message" && discord.SendHold is { } hold)
                    return HeldAsync(hold);

                return Response(HttpStatusCode.OK, MessageJson());
            }

            static async Task<RestResponse> HeldAsync(Task hold)
            {
                await hold;
                return await Response(HttpStatusCode.OK, MessageJson());
            }

            static async Task<RestResponse> HeldLeaveAsync(Task hold)
            {
                await hold;
                return await Response(HttpStatusCode.NoContent, "");
            }

            // Requests during login aren't steps of a command
            static string? StepName(string method, string endpoint, string body)
            {
                if (endpoint.EndsWith("/callback"))
                    return JsonDocument.Parse(body).RootElement.GetProperty("type").GetInt32() switch
                    {
                        4 => "respond",
                        5 => "defer",
                        6 => "defer update",
                        7 => "update message",
                        8 => "autocomplete",
                        var type => $"callback {type}"
                    };

                if (endpoint.Contains("/messages/@original"))
                    return method == "PATCH" ? "edit original" : "delete original";

                if (endpoint.StartsWith("webhooks/"))
                    return "followup";

                if (endpoint.EndsWith("/pins"))
                    return "get pins";

                if (endpoint.Contains("/pins/"))
                    return "pin";

                if (endpoint == "users/@me/channels")
                    return "open direct message";

                if (method == "DELETE" && Regex.IsMatch(endpoint, @"^users/@me/guilds/\d+$"))
                    return "leave server";

                if (method == "GET" && Regex.IsMatch(endpoint, @"^channels/\d+$"))
                    return "get channel";

                if (method == "GET" && Regex.IsMatch(endpoint, @"^guilds/\d+\?with_counts=true$"))
                    return "get guild";

                if (endpoint.EndsWith("/messages"))
                    return "send message";

                return null;
            }

            static Task<RestResponse> Response(HttpStatusCode status, string json) =>
                Task.FromResult(new RestResponse(status, new Dictionary<string, string>(), new MemoryStream(Encoding.UTF8.GetBytes(json))));
        }
    }
}
