using DataAccessLibrary;
using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.WebSocket;
using MinerBot_2._0;
using MinerBot_2._0.Handlers;
using MinerBot_2._0.Services;
using SteamWebAPIAccess;
using System.Reflection;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();
builder.Services.AddSingleton(new DiscordSocketConfig { GatewayIntents = GatewayIntents.All, AlwaysDownloadUsers = true, MessageCacheSize = 100 });
builder.Services.AddSingleton<DiscordSocketClient>();
builder.Services.AddSingleton(new InteractionServiceConfig { LocalizationManager = new ResxLocalizationManager("InteractionFramework.Resources.CommandLocales", Assembly.GetEntryAssembly()) }); // ,new CultureInfo("en-US")
//builder.Services.AddSingleton<InteractionService>();
builder.Services.AddSingleton<InteractionService>(p => new InteractionService(p.GetRequiredService<DiscordSocketClient>()));
builder.Services.AddSingleton<InteractionHandler>();
builder.Services.AddSingleton<CommandService>();
builder.Services.AddSingleton<CommandHandlingService>();
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<PictureService>();
builder.Services.AddSingleton<AnnounceService>();
builder.Services.AddSingleton<ArmorService>();
builder.Services.AddSingleton<SteamAPIService>();
builder.Services.AddSingleton<DiscordService>();
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

// Redis Caching
//builder.Services.AddStackExchangeRedisCache(options =>
//{
//    options.Configuration = builder.Configuration.GetConnectionString(builder.Environment.IsDevelopment() ? "RedisRemote" : "Redis"); // builder.Environment.IsDevelopment() ? "RedisRemote" : "Redis"
//    options.InstanceName = "Minerbot20_";
//});

// In Memory Caching
builder.Services.AddMemoryCache();

//builder.Services.AddSingleton<AutomatedNotificationService>();
// Add Hangfire Services
//builder.Services.AddHangfire(configuration => configuration
//                    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
//                    .UseSimpleAssemblyNameTypeSerializer()
//                    .UseRecommendedSerializerSettings()
//                    .UseSqlServerStorage(builder.Configuration.GetConnectionString("HangfireConnection"), new Hangfire.SqlServer.SqlServerStorageOptions
//                    {
//                        CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
//                        SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
//                        QueuePollInterval = TimeSpan.Zero,
//                        UseRecommendedIsolationLevel = true,
//                        DisableGlobalLocks = true
//                    }))
//    .AddHangfireServer();

var host = builder.Build();

var client = host.Services.GetRequiredService<DiscordSocketClient>();

// Need to use ILogger instead, but good for now.
client.Log += Client_Log;

// Client Activity
await client.SetGameAsync("Murder Miners on my PC", type:ActivityType.Playing);
// MM Game Embed, RPC needed
//client.Activity.

await client.LoginAsync(TokenType.Bot, Environment.GetEnvironmentVariable("BOTTOKEN"));
await client.StartAsync();

await host.Services.GetRequiredService<CommandHandlingService>().InitializeAsync();
await host.Services.GetRequiredService<InteractionHandler>().InitializeAsync();
await host.Services.GetRequiredService<AnnounceService>().InitializeWebhookAsync();

var steamApi = host.Services.GetService<ISteamAPIAccess>();
steamApi.SetCredentials(Environment.GetEnvironmentVariable("STEAMWEB_PUB_KEY"), 
                        Environment.GetEnvironmentVariable("STEAMUSER_REMEMBERME"),
                        Environment.GetEnvironmentVariable("STEAMUSER_NAME"),
                        Environment.GetEnvironmentVariable("STEAMUSER_PASSWORD"));
steamApi.AuthenticateClient();

var getSteamAPI = host.Services.GetService<IGetData>();
getSteamAPI.Initilization();

host.Run();

//var inits = host.Services.GetRequiredService<InitilizeHandler>();
//await inits.StartAsync();

//await Task.Delay(Timeout.Infinite);

Task Client_Log(LogMessage msg)
{
    if (msg.Exception is CommandException cmdException)
    {
        Console.WriteLine($"[Command/{msg.Severity}] {cmdException.Command.Aliases.First()}"
            + $" failed to execute in {cmdException.Context.Channel}.");
        Console.WriteLine(cmdException);
    }
    else
        Console.WriteLine($"[General/{msg.Severity}] {msg}");

    return Task.CompletedTask;
}