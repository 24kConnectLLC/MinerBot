using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using DataAccessLibrary.Armor;
using DataAccessLibrary.Discord;
using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.Net.Rest;
using Discord.Net.WebSockets;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MinerBot_2._0.Handlers;
using MinerBot_2._0.Jobs;
using MinerBot_2._0.Services;
using SteamWebAPIAccess;

namespace MinerBot.Tests
{
    // FakeDiscord swaps the console and the cooldowns are static, so these tests run one at a time.
    [CollectionDefinition("Discord", DisableParallelization = true)]
    public class DiscordCollection { }

    // One thing the bot did, in order. Body is the JSON it sent to Discord, and File the upload that came with it.
    internal sealed record Step(string Name, string Body = "", string? File = null)
    {
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

        // What Discord counts against its limit for all the embeds of a message
        public int EmbedsLength => Message.TryGetProperty("embeds", out var embeds)
            ? embeds.EnumerateArray().Sum(embed => TextLength(embed, "title") + TextLength(embed, "description")
                + TextLength(embed.GetProperty("footer"), "text")
                + embed.GetProperty("fields").EnumerateArray().Sum(row => TextLength(row, "name") + TextLength(row, "value")))
            : 0;

        static int TextLength(JsonElement json, string name) => json.TryGetProperty(name, out var text) ? text.GetString()!.Length : 0;

        public IEnumerable<string> ButtonIds => Message.TryGetProperty("components", out var rows)
            ? rows.EnumerateArray().SelectMany(row => row.GetProperty("components").EnumerateArray()).Select(button => button.GetProperty("custom_id").GetString()!)
            : [];
    }

    // Runs the bot's real handlers on the real Discord.Net pipeline, with only the network and the data layer faked.
    // Tests send the gateway JSON that Discord would send, then read what the bot did in response.
    internal sealed class FakeDiscord : IAsyncDisposable
    {
        public const ulong BotId = 1000, GuildId = 2000, ChannelId = 3000, DeletedChannelId = 3001, DirectChannelId = 3002, MessageId = 4000, TeamId = 7000;

        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

        static long lastUserId = 5000;

        readonly DiscordSocketClient client;
        readonly CommandService commands = new();
        readonly InteractionService interactions;
        readonly ServiceProvider services;
        readonly FakeGateway gateway;
        readonly List<Step> steps = new();
        readonly TextWriter realConsole = Console.Out;
        readonly ConsoleCapture console = new();
        readonly FakeClock cacheClock = new();
        TaskCompletionSource executed = NewSignal();
        int gatewaySequence;

        // The first user owns the guild, so it passes the admin checks. The other one has no permissions.
        public ulong UserId { get; }
        public ulong OtherUserId { get; }

        public FakeGetData Steam { get; }
        public FakeArmorData Armor { get; }
        public FakeGuildData Guilds { get; }
        public NotificationState Notifications => services.GetRequiredService<NotificationState>();

        // Name of the Discord request that gets refused, to test what happens when a reply can't be sent
        public string? FailStep { get; set; }

        // How many requests of FailStep get through before the refusals start
        public int FailStepAfter;

        public int ExecutedEvents;

        // Holds back READY, the way a slow gateway does, until ReleaseReadyAsync
        readonly TaskCompletionSource? readyGate;
        readonly TaskCompletionSource ready = NewSignal();

        // Set to make the bot's application belong to a team with this owner
        public ulong? TeamOwnerId { get; set; }

        public string ConsoleText => console.ToString();

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

        FakeDiscord(bool holdReady)
        {
            readyGate = holdReady ? NewSignal() : null;

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
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages,
                LogGatewayIntentWarnings = false,
                LogLevel = LogSeverity.Debug,
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
                .AddSingleton(commands)
                .AddSingleton<InteractionHandler>()
                .AddSingleton<CommandHandlingService>()
                .AddSingleton<HttpClient>()
                .AddSingleton<PictureService>()
                .AddSingleton<AnnounceService>()
                .AddSingleton<OwnerAlertService>()
                .AddSingleton<ArmorService>()
                .AddSingleton<SteamAPIService>()
                .AddSingleton<DiscordService>()
                .AddSingleton<NotificationState>()
                .AddSingleton<IGetData>(Steam)
                .AddSingleton<IArmorData>(Armor)
                .AddSingleton<IGuildData>(Guilds)
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
                .AddLogging()
                .AddMemoryCache(options => options.Clock = cacheClock)
                .BuildServiceProvider();
        }

        public static async Task<FakeDiscord> StartAsync(bool holdReady = false)
        {
            var discord = new FakeDiscord(holdReady);
            await discord.ConnectAsync();
            return discord;
        }

        public async Task ReleaseReadyAsync()
        {
            readyGate!.SetResult();
            await WaitAsync(ready.Task);
        }

        async Task ConnectAsync()
        {
            Console.SetOut(console);

            await services.GetRequiredService<CommandHandlingService>().InitializeAsync();
            await services.GetRequiredService<InteractionHandler>().InitializeAsync();

            // InitializeAsync looks in the entry assembly, which is the test runner here
            await commands.AddModulesAsync(typeof(CommandHandlingService).Assembly, services);
            await interactions.AddModulesAsync(typeof(CommandHandlingService).Assembly, services);

            // Added after the bot's own handlers, so these fire once those are done
            commands.CommandExecuted += (_, _, _) => MarkExecuted();
            interactions.InteractionExecuted += (_, _, _) => MarkExecuted();

            client.Ready += () =>
            {
                ready.TrySetResult();
                return Task.CompletedTask;
            };

            await client.LoginAsync(TokenType.Bot, "fake token", validateToken: false);
            await client.StartAsync();

            if (readyGate == null)
                await WaitAsync(ready.Task);
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

        void AddStep(string name) => AddStep(new Step(name));

        void AddStep(Step step)
        {
            lock (steps)
                steps.Add(step);
        }

        static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Sending

        // A message in the guild's channel, waits until the command is done
        public async Task SendMessageAsync(string content, ulong? userId = null)
        {
            var authorId = userId ?? UserId;

            await SendAsync("MESSAGE_CREATE", $$"""
                {"id":"{{SnowflakeUtils.ToSnowflake(DateTimeOffset.UtcNow)}}","channel_id":"{{ChannelId}}","guild_id":"{{GuildId}}",
                 "author":{{UserJson(authorId)}},"member":{{MemberJson(authorId)}},"content":{{JsonSerializer.Serialize(content)}},
                 "timestamp":"2026-01-01T00:00:00+00:00","tts":false,"mention_everyone":false,"mentions":[],"mention_roles":[],
                 "attachments":[],"embeds":[],"pinned":false,"type":0}
                """);
        }

        // /group subcommand with options like {"name":"map-url","type":3,"value":"x"}
        public Task SendSlashCommandAsync(string group, string subcommand, string options = "", string resolved = "{}", ulong? userId = null, TimeSpan age = default)
        {
            var data = $$"""{"id":"1","name":"{{group}}","type":1,"options":[{"name":"{{subcommand}}","type":1,"options":[{{options}}]}],"resolved":{{resolved}}}""";
            return SendInteractionAsync(type: 2, data, userId ?? UserId, age);
        }

        // A click on a button of the bot's message
        public Task SendButtonAsync(string customId, ulong? userId = null)
        {
            var data = $$"""{"custom_id":{{JsonSerializer.Serialize(customId)}},"component_type":2}""";
            return SendInteractionAsync(type: 3, data, userId ?? UserId, default, $$""","message":{{MessageJson()}}""");
        }

        public Task SendMessageCommandAsync(string name, int messageType = 0)
        {
            var data = $$"""{"id":"1","name":"{{name}}","type":3,"target_id":"{{MessageId}}","resolved":{"messages":{"{{MessageId}}":{{MessageJson(messageType)}} } } }""";
            return SendInteractionAsync(type: 2, data, UserId, default);
        }

        // Autocomplete for a slash command that doesn't exist, which is the only way the handler sees one
        public Task SendUnknownAutocompleteAsync()
        {
            var data = """{"id":"1","name":"unknown","type":1,"options":[{"name":"value","type":3,"value":"a","focused":true}]}""";
            return SendInteractionAsync(type: 4, data, UserId, default);
        }

        Task SendInteractionAsync(int type, string data, ulong userId, TimeSpan age, string extraFields = "")
        {
            // Discord.Net reads the age of an interaction from its ID
            var id = SnowflakeUtils.ToSnowflake(DateTimeOffset.UtcNow - age);

            return SendAsync("INTERACTION_CREATE", $$"""
                {"id":"{{id}}","application_id":"{{BotId}}","type":{{type}},"token":"interaction-token","version":1,
                 "guild_id":"{{GuildId}}","channel_id":"{{ChannelId}}","channel":{"id":"{{ChannelId}}","type":0},
                 "member":{{MemberJson(userId)}},"data":{{data}},"entitlements":[],"app_permissions":"0"{{extraFields}}}
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
            cacheClock.UtcNow += TimeSpan.FromMinutes(5);

            await ActivatorUtilities.CreateInstance<GuildNotificationJob>(services).NotifyAllGuilds();
        }

        public Task LeaveGuildAsync() => gateway.DeliverAsync("GUILD_DELETE", $$"""{"id":"{{GuildId}}"}""");

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

        static string DirectChannelJson(ulong userId) => $$"""
            {"id":"{{DirectChannelId}}","type":1,"last_message_id":null,"recipients":[{{UserJson(userId)}}]}
            """;

        static string MemberJson(ulong id) =>
            $$"""{"user":{{UserJson(id)}},"roles":[],"joined_at":"2020-01-01T00:00:00+00:00","deaf":false,"mute":false,"permissions":"0"}""";

        static string MessageJson(int type = 0) => $$"""
            {"id":"{{MessageId}}","channel_id":"{{ChannelId}}","author":{{UserJson(BotId, bot: true)}},"content":"",
             "timestamp":"2026-01-01T00:00:00+00:00","tts":false,"mention_everyone":false,"mentions":[],"mention_roles":[],
             "attachments":[],"embeds":[],"pinned":false,"type":{{type}}}
            """;

        string ReadyJson() => $$"""
            {"v":10,"user":{{UserJson(BotId, bot: true)}},"session_id":"session","resume_gateway_url":"wss://gateway.invalid",
             "private_channels":[],"application":{"id":"{{BotId}}","flags":0},
             "guilds":[{"id":"{{GuildId}}","name":"Guild","owner_id":"{{UserId}}","verification_level":0,"default_message_notifications":0,
                        "explicit_content_filter":0,"mfa_level":0,"premium_tier":0,"nsfw_level":0,"features":[],"emojis":[],"stickers":[],
                        "roles":[{"id":"{{GuildId}}","name":"@everyone","permissions":"0","position":0,"color":0,"hoist":false,"managed":false,"mentionable":false}],
                        "channels":[{"id":"{{ChannelId}}","type":0,"name":"general","position":0,"permission_overwrites":[]}],
                        "unavailable":false,"member_count":1,"large":false,"members":[],"presences":[],"threads":[],
                        "voice_states":[],"guild_scheduled_events":[],"joined_at":"2020-01-01T00:00:00+00:00"}]}
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
            public event Func<byte[], int, int, Task>? BinaryMessage { add { } remove { } }
            public event Func<string, Task>? TextMessage;
            public event Func<Exception, Task>? Closed { add { } remove { } }

            public void SetHeader(string key, string value) { }
            public void SetCancelToken(CancellationToken cancelToken) { }
            public Task ConnectAsync(string host) => Task.CompletedTask;
            public Task DisconnectAsync(int closeCode = 1000) => Task.CompletedTask;
            public void Dispose() { }

            public Task SendAsync(byte[] data, int index, int count, bool isText)
            {
                using var json = JsonDocument.Parse(data.AsMemory(index, count));

                const int identifyOpCode = 2;
                if (json.RootElement.GetProperty("op").GetInt32() == identifyOpCode)
                    _ = Task.Run(() => LoginAsync());

                return Task.CompletedTask;
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
                    discord.AddStep(new Step(name, body, file));

                if (name != null && name == discord.FailStep && Interlocked.Decrement(ref discord.FailStepAfter) < 0)
                    return Response(HttpStatusCode.Forbidden, """{"code":50013,"message":"Missing Permissions"}""");

                if (endpoint == "users/@me")
                    return Response(HttpStatusCode.OK, UserJson(BotId, bot: true));

                if (endpoint == "oauth2/applications/@me")
                    return Response(HttpStatusCode.OK, discord.ApplicationJson());

                if (endpoint == "users/@me/channels")
                    return Response(HttpStatusCode.OK, DirectChannelJson(ulong.Parse(JsonDocument.Parse(body).RootElement.GetProperty("recipient_id").GetString()!)));

                if (endpoint.StartsWith("users/"))
                    return Response(HttpStatusCode.OK, UserJson(ulong.Parse(endpoint["users/".Length..])));

                if (endpoint == $"channels/{DeletedChannelId}")
                    return Response(HttpStatusCode.NotFound, """{"code":10003,"message":"Unknown Channel"}""");

                if (endpoint.Contains("/commands") || endpoint.EndsWith("/pins"))
                    return Response(HttpStatusCode.OK, "[]");

                return Response(HttpStatusCode.OK, MessageJson());
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

                if (endpoint.EndsWith("/messages"))
                    return "send message";

                return null;
            }

            static Task<RestResponse> Response(HttpStatusCode status, string json) =>
                Task.FromResult(new RestResponse(status, new Dictionary<string, string>(), new MemoryStream(Encoding.UTF8.GetBytes(json))));
        }
    }
}
