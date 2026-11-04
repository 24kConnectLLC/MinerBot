using MinerBot_2._0.Handlers;
using SteamWebAPIAccess;

namespace MinerBot_2._0
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IHost _host;

        private ISteamAPIAccess steamAPI { get; set; }
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
                    // A failed start only costs the scheduled jobs, so Steam and Discord carry on
                    try
                    {
                        await jobHandler.StartAsync(stoppingToken);
                    }
                    catch (Exception e)
                    {
                        _logger.LogCritical(e, "The scheduled jobs couldn't start, so nothing runs on a schedule until a restart");
                    }

                    await steamAPI.RunCallbacksAsync(stoppingToken);
                }
                else
                    _logger.LogInformation("SteamAPI is null!");
            }
            catch (OperationCanceledException e)
            {
                _logger.LogCritical($"The Operation was canceled!\n{e.ToString()}");
            }
            // The host stops after this, and a restart policy of on-failure only acts on an exit code that says so
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                Environment.ExitCode = 1;
                throw;
            }
            finally
            {
                _logger.LogCritical("Service Stopped");
            }
        }
    }
}
