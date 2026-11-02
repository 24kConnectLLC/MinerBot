using Microsoft.AspNetCore.Components;
using MinerBot_2._0.Handlers;
using MinerBot_2._0.Models;
using SteamWebAPIAccess;

namespace MinerBot_2._0
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IHost _host;

        [Inject]
        private ISteamAPIAccess steamAPI { get; set; }

        [Inject]
        private JobHandler jobHandler { get; set; }

        public Worker(ILogger<Worker> logger, IHost host)
        {
            _logger = logger;
            _host = host;

            steamAPI = _host.Services.GetService<ISteamAPIAccess>();
            jobHandler = host.Services.GetRequiredService<JobHandler>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Bot Starting...");

            try
            {
                if (steamAPI != null)
                {
                    await jobHandler.StartAsync(stoppingToken);
                    await steamAPI.RunCallbacksAsync(stoppingToken);
                }
                else
                    _logger.LogInformation("SteamAPI is null!");
            }
            catch (OperationCanceledException e)
            {
                _logger.LogCritical($"The Operation was canceled!\n{e.ToString()}");
            }
            finally
            {
                _logger.LogCritical("Service Stopped");
            }
        }
    }
}
