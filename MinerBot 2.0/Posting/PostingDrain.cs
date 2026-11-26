namespace MinerBot_2._0.Posting
{
    // The notification run's part in a stop. Registered after WebsiteFeedWriter, so it stops before every other service.
    // From the host's first sign of a stop no message is claimed; then it waits up to 20 s for the one in flight to settle,
    // which with StatusWriter's 5 s for its last write fits in Docker's 30 s, and ends it if it's still under way, when it
    // counts as one Discord may not have answered and the next start looks for it in the channel's history.
    public sealed class PostingDrain(PostingGate gate, IHostApplicationLifetime lifetime, ILogger<PostingDrain> logger) : IHostedService
    {
        // Shorter in tests
        internal TimeSpan Limit = TimeSpan.FromSeconds(20);

        public Task StartAsync(CancellationToken cancellationToken)
        {
            lifetime.ApplicationStopping.Register(gate.Close);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (!await gate.DrainAsync(Limit, cancellationToken))
                logger.LogWarning("A post was still under way {Limit} s into the stop, so the bot stopped waiting for it, and its next start looks for it in the channel",
                    Limit.TotalSeconds);
        }
    }
}
