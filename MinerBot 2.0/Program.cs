using DataAccessLibrary;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using MinerBot_2._0;
using MinerBot_2._0.Handlers;
using MinerBot_2._0.Jobs;
using MinerBot_2._0.Requests;
using MinerBot_2._0.Services;
using MinerBot_2._0.Status;
using SteamWebAPIAccess;

// Docker's health check, which only reads the heartbeat file: it reads no settings, builds no host and connects to nothing
if (args is ["healthcheck"])
{
    var (exitCode, line) = Heartbeat.Check(Heartbeat.DefaultPath, DateTimeOffset.UtcNow);
    Console.WriteLine(line);
    Environment.ExitCode = exitCode;
    return;
}

// A container that Docker restarts keeps its /tmp, so the run before's heartbeat goes before anything else happens
Heartbeat.DeleteOld(Heartbeat.DefaultPath);

var builder = Host.CreateApplicationBuilder(args);
// The bot's status and history, which StatusWriter writes to MinerBot's database. Registered before the worker, so they
// stop after it, and the last write holds what its stop recorded.
builder.Services.AddSingleton<BotRecorder>();
builder.Services.AddSingleton<DiscordStatusHandler>();
builder.Services.AddSingleton<IDiscordStatus>(services => services.GetRequiredService<DiscordStatusHandler>());
builder.Services.AddHostedService<StatusWriter>();
builder.Services.AddHostedService<StatusSampler>();
// The servers and channels the bot is in, which GuildWriter copies to MinerBot's database as GuildWatcher notes them
builder.Services.AddSingleton<GuildWatcher>();
builder.Services.AddHostedService<GuildWriter>();
builder.Services.AddHostedService<Worker>();
// The dashboard's signed requests, which the bot takes from MinerBot's database. Registered after the worker, so it starts
// once JobHandler has, and stops before Steam's pump ends.
builder.Services.AddSingleton<PostsPause>();
builder.Services.AddSingleton<PresenceKeeper>();
builder.Services.AddSingleton<RequestActions>();
builder.Services.AddHostedService<RequestWorker>();
builder.Services.AddSingleton(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds });
builder.Services.AddSingleton<DiscordSocketClient>();
//builder.Services.AddSingleton<InteractionService>();
builder.Services.AddSingleton<InteractionService>(p => new InteractionService(p.GetRequiredService<DiscordSocketClient>()));
builder.Services.AddSingleton<InteractionHandler>();
builder.Services.AddSingleton<GuildEventHandler>();
builder.Services.AddWebClient();
builder.Services.AddSingleton<AnnounceService>();
builder.Services.AddSingleton<OwnerAlertService>();
builder.Services.AddSingleton<ArmorService>();
builder.Services.AddSingleton<SteamAPIService>();
builder.Services.AddSingleton<DiscordService>();
builder.Services.AddSingleton<NotificationState>();
// For the map cooldown of the notification job, tests give it another clock
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<JobHandler>();
//builder.Logging.ClearProviders();
//builder.Logging.AddConsole();

// SQL Data Access
builder.Services.AddDataAccessServices(builder.Configuration)
                .AddArmorAccess()
                .AddBlacklistAccess()
                .AddGuildAccess()
                .AddBotStatusAccess()
                .AddRetentionAccess()
                .AddBotGuildAccess()
                .AddBotRequestAccess();

// Steam API Access
builder.Services.AddSteamAPIServices();

// In Memory Caching
builder.Services.AddMemoryCache();

//builder.Services.AddSingleton<AutomatedNotificationService>();

var host = builder.Build();

var client = host.Services.GetRequiredService<DiscordSocketClient>();
var recorder = host.Services.GetRequiredService<BotRecorder>();

// On a signal or a failure, before any service stops
host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => recorder.Record(BotEvents.BotStopping));

// Need to use ILogger instead, but good for now.
client.Log += Client_Log;

// Client Activity: the default, which goes with the first IDENTIFY, until the request worker reads the status text the
// dashboard set, once the bot runs. PresenceKeeper sends it again at every connection.
await host.Services.GetRequiredService<PresenceKeeper>().InitializeAsync();
// MM Game Embed, RPC needed
//client.Activity.

// Before connecting, and before the other Ready handlers: Discord.Net skips the ones after a Ready handler that throws,
// and without this one's Ready the bot never counts as up, so its health check would fail
host.Services.GetRequiredService<DiscordStatusHandler>().Initialize();

// Also before connecting and the other handlers, so the servers that change from the start are noted, and a handler that
// throws, such as GuildEventHandler's LeftGuild when the website's database fails, can't skip the notes
host.Services.GetRequiredService<GuildWatcher>().Initialize();

// Before connecting, because a Ready event during this would register an empty list and delete every command
await host.Services.GetRequiredService<InteractionHandler>().InitializeAsync();

// Also before connecting, so a server that adds or removes the bot right after a start isn't missed
host.Services.GetRequiredService<GuildEventHandler>().Initialize();

await client.LoginAsync(TokenType.Bot, Environment.GetEnvironmentVariable("BOTTOKEN"));
await client.StartAsync();

await host.Services.GetRequiredService<AnnounceService>().InitializeWebhookAsync();

var steamApi = host.Services.GetService<ISteamAPIAccess>();
steamApi.SetCredentials(Environment.GetEnvironmentVariable("STEAMWEB_PUB_KEY"), 
                        Environment.GetEnvironmentVariable("STEAMUSER_REMEMBERME"),
                        Environment.GetEnvironmentVariable("STEAMUSER_NAME"),
                        Environment.GetEnvironmentVariable("STEAMUSER_PASSWORD"));
steamApi.AlertOwner = host.Services.GetRequiredService<OwnerAlertService>().SendAsync;
steamApi.RecordEvent = recorder.Record;
steamApi.AuthenticateClient();

var getSteamAPI = host.Services.GetService<IGetData>();
getSteamAPI.Initilization();

host.Run();

//var inits = host.Services.GetRequiredService<InitilizeHandler>();
//await inits.StartAsync();

//await Task.Delay(Timeout.Infinite);

Task Client_Log(LogMessage msg)
{
    Console.WriteLine($"[General/{msg.Severity}] {msg}");

    return Task.CompletedTask;
}