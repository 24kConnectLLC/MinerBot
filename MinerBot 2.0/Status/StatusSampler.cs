using SteamWebAPIAccess;

namespace MinerBot_2._0.Status
{
    // Every 5 seconds: rewrites the heartbeat file while the bot is up on Discord, reads Discord's and Steam's state for the
    // status row, and queues a sample at the first tick of each UTC minute, with Discord up in it while the gateway is
    // connected. It never touches the database, so a database that hangs can't hold up the heartbeat, and nothing leaves
    // its loop, because an exception there would stop the bot.
    public sealed class StatusSampler : BackgroundService
    {
        // Both shorter or elsewhere in tests, which move the clock themselves
        internal TimeSpan Interval = TimeSpan.FromSeconds(5);
        internal string HeartbeatPath = Heartbeat.DefaultPath;

        readonly BotRecorder _recorder;
        readonly IDiscordStatus _discord;
        readonly ISteamAPIAccess _steam;
        readonly TimeProvider _clock;
        readonly FailureLog _failures;

        DateTime? lastMinute;

        public StatusSampler(BotRecorder recorder, IDiscordStatus discord, ISteamAPIAccess steam, TimeProvider clock, ILogger<StatusSampler> logger)
        {
            _recorder = recorder;
            _discord = discord;
            _steam = steam;
            _clock = clock;
            _failures = new FailureLog(logger);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(Interval, _clock);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    Tick();
                }
                catch (Exception e)
                {
                    _failures.Log(e, "The status sampler failed");
                }

                try
                {
                    await timer.WaitForNextTickAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        internal void Tick()
        {
            var now = _clock.GetUtcNow();
            var discord = _discord.Read();

            // First, so nothing after it can hold up the heartbeat
            if (discord.Up)
            {
                try
                {
                    Heartbeat.Write(HeartbeatPath, now, _recorder.RunId);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _failures.Log(e, "The heartbeat file couldn't be written, so Docker's health check fails");
                }
            }

            var steam = _steam.Status;
            _recorder.UpdateStatus(discord, steam);

            var time = now.UtcDateTime;
            var minute = new DateTime(time.Ticks - time.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

            if (minute != lastMinute)
            {
                lastMinute = minute;
                _recorder.Sample(minute, discord.Connected, steam.State == SteamConnection.LoggedOn);
            }
        }
    }
}
