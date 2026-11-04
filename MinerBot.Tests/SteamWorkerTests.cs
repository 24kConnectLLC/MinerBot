using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
            public Exception? PumpFailure;

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
                return PumpFailure != null ? Task.FromException(PumpFailure) : Task.Delay(Timeout.Infinite, stoppingToken);
            }

            public Task RenewTokenAsync()
            {
                Renewals++;
                return Task.CompletedTask;
            }
        }

        // Fails the way a schedule whose time zone can't be found would
        sealed class FailingJobHandler : JobHandler
        {
            public FailingJobHandler(ILogger<JobHandler> logger, IServiceProvider service) : base(logger, service)
            {
            }

            public override Task StartAsync(CancellationToken cancellationToken) => throw new TimeZoneNotFoundException("Eastern Standard Time wasn't found");
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
        public async Task SteamRunsWhenTheJobsCanNotStart()
        {
            var steam = new FakeSteam();
            var logger = new RecordingLogger<Worker>();
            var builder = Host.CreateApplicationBuilder();

            builder.Services.AddSingleton<ISteamAPIAccess>(steam);
            builder.Services.AddSingleton<JobHandler, FailingJobHandler>();

            using var host = builder.Build();
            var worker = new Worker(logger, host);

            await worker.StartAsync(CancellationToken.None);
            await steam.PumpStarted.Task.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            await worker.StopAsync(CancellationToken.None);

            Assert.True(logger.Logged(LogLevel.Critical, "The scheduled jobs couldn't start"));
        }

        // Docker's on-failure restart policy goes by the exit code, and the host leaves it at 0.
        // The worker is started by hand, so this doesn't show that Program.cs lets its failure stop the bot.
        [Fact]
        public async Task WorkerThatFailsSetsAnErrorExitCode()
        {
            var steam = new FakeSteam { PumpFailure = new InvalidOperationException("The pump failed") };
            var builder = Host.CreateApplicationBuilder();

            builder.Services.AddSingleton<ISteamAPIAccess>(steam);
            builder.Services.AddSingleton<JobHandler>();

            using var host = builder.Build();
            var worker = new Worker(NullLogger<Worker>.Instance, host);
            var exitCode = Environment.ExitCode;

            try
            {
                await worker.StartAsync(CancellationToken.None);

                await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ExecuteTask!.WaitAsync(WaitLimit, TestContext.Current.CancellationToken));
                Assert.Equal(1, Environment.ExitCode);
            }
            finally
            {
                Environment.ExitCode = exitCode;

                // Stops the job handler, which the failure leaves running
                await worker.StopAsync(CancellationToken.None);
            }
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
