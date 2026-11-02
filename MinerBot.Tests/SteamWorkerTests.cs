using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MinerBot_2._0;
using MinerBot_2._0.Attributes;
using MinerBot_2._0.Handlers;
using MinerBot_2._0.Jobs;
using SteamKit2;
using SteamWebAPIAccess;

namespace MinerBot.Tests
{
    public class SteamWorkerTests
    {
        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

        sealed class FakeSteam : ISteamAPIAccess
        {
            public TaskCompletionSource PumpStarted { get; } = new();
            public int Renewals;

            public uint appID { get; set; }
            public CallbackManager manager { get; set; } = null!;
            public string PublisherWebAPIKey { get; set; } = "";
            public SteamClient steamClient { get; set; } = null!;
            public SteamMatchmaking steamMatchmaking => null!;
            public SteamUser steamUser { get; set; } = null!;
            public Func<string, Task>? AlertOwner { get; set; }

            public void AuthenticateClient() { }
            public void SetCredentials(string key, string shouldRemember, string username = "", string password = "") { }

            public Task RunCallbacksAsync(CancellationToken stoppingToken)
            {
                PumpStarted.TrySetResult();
                return Task.Delay(Timeout.Infinite, stoppingToken);
            }

            public Task RenewTokenAsync()
            {
                Renewals++;
                return Task.CompletedTask;
            }
        }

        // NullLogger has every level turned off, which used to stop the pump without a word
        [Fact]
        public async Task WorkerRunsTheSteamPumpWhateverTheLogLevel()
        {
            var steam = new FakeSteam();
            var builder = Host.CreateApplicationBuilder();

            builder.Services.AddSingleton<ISteamAPIAccess>(steam);
            builder.Services.AddSingleton<JobHandler>();

            using var host = builder.Build();
            var worker = new Worker(NullLogger<Worker>.Instance, host);

            await worker.StartAsync(CancellationToken.None);
            await steam.PumpStarted.Task.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            await worker.StopAsync(CancellationToken.None);
        }

        [Fact]
        public async Task DailyJobRenewsTheToken()
        {
            var steam = new FakeSteam();

            await new SteamTokenJob(steam).RenewSteamToken();

            Assert.Equal(1, steam.Renewals);
        }

        [Fact]
        public void TokenJobIsScheduled()
        {
            var job = typeof(SteamTokenJob).GetMethod(nameof(SteamTokenJob.RenewSteamToken))!.GetCustomAttribute<JobAttribute>();

            Assert.Equal("0 12 * * *", job!.Cron);
        }
    }
}
