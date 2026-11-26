namespace MinerBot_2._0.Posting
{
    // Whether the notification run may claim another message: not once the host has begun to stop. The message in flight is
    // counted from its claim to its settle, so PostingDrain can wait for it; once that wait is over, Abandon ends what's still
    // under way: a send, which then counts as one Discord may not have answered, or a read of a channel's messages. Each of
    // those also ends by itself at RequestLimit, which is shorter than PostingDrain's wait.
    public sealed class PostingGate
    {
        readonly Lock gate = new();
        readonly CancellationTokenSource abandon = new();
        TaskCompletionSource? idle;
        bool closed;
        int inFlight;

        // Shorter in tests
        internal TimeSpan RequestLimit = PostingLimits.RequestLimit;

        public bool IsOpen
        {
            get
            {
                lock (gate)
                    return !closed;
            }
        }

        public CancellationToken Abandon => abandon.Token;

        // The token of one request to Discord, a send or a read of a channel's messages: it ends the request at its limit, or
        // when Abandon does, whatever the request waits for, Discord's answer or a rate limit's end. Discord.Net checks a
        // request's own Timeout only before each try, and HttpClient waits 100 s for an answer, so without it a request Discord
        // never answers, or a long rate limit, would hold the run that long. The caller disposes of it once the request ends.
        public CancellationTokenSource Limit()
        {
            var limit = CancellationTokenSource.CreateLinkedTokenSource(abandon.Token);
            limit.CancelAfter(RequestLimit);
            return limit;
        }

        // Before a claim: false once the host is stopping, when nothing more may be claimed
        public bool TryEnter()
        {
            lock (gate)
            {
                if (closed)
                    return false;

                inFlight++;
                return true;
            }
        }

        // Once the message's settle was tried, after a claim that TryEnter let through
        public void Leave()
        {
            lock (gate)
            {
                inFlight--;

                if (inFlight == 0)
                    idle?.TrySetResult();
            }
        }

        // At the host's first sign of a stop
        public void Close()
        {
            lock (gate)
                closed = true;
        }

        // Closes the gate, waits up to the limit for the message in flight to settle, then abandons what's still under way.
        // True when nothing was left in flight.
        public async Task<bool> DrainAsync(TimeSpan limit, CancellationToken cancellationToken)
        {
            Task? wait = null;

            lock (gate)
            {
                closed = true;

                if (inFlight > 0)
                    wait = (idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }

            try
            {
                if (wait != null)
                    await wait.WaitAsync(limit, cancellationToken);

                return true;
            }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException)
            {
                return false;
            }
            finally
            {
                await abandon.CancelAsync();
            }
        }
    }
}
