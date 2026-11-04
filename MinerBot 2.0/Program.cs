using DataAccessLibrary;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using MinerBot_2._0;
using MinerBot_2._0.Handlers;
using MinerBot_2._0.Jobs;
using MinerBot_2._0.Services;
using SteamWebAPIAccess;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();
builder.Services.AddSingleton(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds });
builder.Services.AddSingleton<DiscordSocketClient>();
//builder.Services.AddSingleton<InteractionService>();
builder.Services.AddSingleton<InteractionService>(p => new InteractionService(p.GetRequiredService<DiscordSocketClient>()));
builder.Services.AddSingleton<InteractionHandler>();
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<AnnounceService>();
builder.Services.AddSingleton<OwnerAlertService>();
builder.Services.AddSingleton<ArmorService>();
builder.Services.AddSingleton<SteamAPIService>();
builder.Services.AddSingleton<DiscordService>();
builder.Services.AddSingleton<NotificationState>();
builder.Services.AddSingleton<JobHandler>();
//builder.Logging.ClearProviders();
//builder.Logging.AddConsole();
builder.Services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// SQL Data Access
builder.Services.AddDataAccessServices(builder.Configuration)
                .AddArmorAccess()
                .AddBlacklistAccess()
                .AddGuildAccess();

// Steam API Access
builder.Services.AddSteamAPIServices();

// In Memory Caching
builder.Services.AddMemoryCache();

//builder.Services.AddSingleton<AutomatedNotificationService>();

var host = builder.Build();

var client = host.Services.GetRequiredService<DiscordSocketClient>();

// Need to use ILogger instead, but good for now.
client.Log += Client_Log;

// Client Activity
await client.SetGameAsync("Murder Miners on my PC", type:ActivityType.Playing);
// MM Game Embed, RPC needed
//client.Activity.

// Before connecting, because a Ready event during this would register an empty list and delete every command
await host.Services.GetRequiredService<InteractionHandler>().InitializeAsync();

await client.LoginAsync(TokenType.Bot, Environment.GetEnvironmentVariable("BOTTOKEN"));
await client.StartAsync();

await host.Services.GetRequiredService<AnnounceService>().InitializeWebhookAsync();

var steamApi = host.Services.GetService<ISteamAPIAccess>();
steamApi.SetCredentials(Environment.GetEnvironmentVariable("STEAMWEB_PUB_KEY"), 
                        Environment.GetEnvironmentVariable("STEAMUSER_REMEMBERME"),
                        Environment.GetEnvironmentVariable("STEAMUSER_NAME"),
                        Environment.GetEnvironmentVariable("STEAMUSER_PASSWORD"));
steamApi.AlertOwner = host.Services.GetRequiredService<OwnerAlertService>().SendAsync;
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