namespace SteamWebAPIAccess
{
    // Every web call of the bot goes through this one handler, so the calls share their connections, have a time limit
    // for each attempt, and a GET that timed out or got a server error is sent once more.
    public sealed class WebRetryHandler : DelegatingHandler
    {
        // Shorter in tests
        internal TimeSpan AttemptLimit = TimeSpan.FromSeconds(10);

        // HttpClient's own timeout covers every attempt of a call, so it leaves room for both and is only a backstop
        internal TimeSpan ClientTimeout => AttemptLimit * 2 + TimeSpan.FromSeconds(5);

        // The connections are renewed now and then, so a handler that lives as long as the bot still follows DNS changes
        public WebRetryHandler() : base(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
        }

        internal WebRetryHandler(HttpMessageHandler innerHandler) : base(innerHandler)
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Anything but a GET can change something, so it's never sent twice
            if (request.Method != HttpMethod.Get)
                return await SendAttemptAsync(request, cancellationToken).ConfigureAwait(false);

            try
            {
                var response = await SendAttemptAsync(request, cancellationToken).ConfigureAwait(false);

                if ((int)response.StatusCode < 500)
                    return response;

                response.Dispose();
            }
            catch (TimeoutException)
            {
                // Sent again below, the same as a server error
            }

            // HttpClient only checks that a request is new before it reaches the handlers, so the same GET can go out again
            return await SendAttemptAsync(request, cancellationToken).ConfigureAwait(false);
        }

        async Task<HttpResponseMessage> SendAttemptAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(AttemptLimit);

            HttpResponseMessage? response = null;

            try
            {
                response = await base.SendAsync(request, attempt.Token).ConfigureAwait(false);

                // Read here, so the limit covers the body too
                await response.Content.LoadIntoBufferAsync(attempt.Token).ConfigureAwait(false);

                return response;
            }
            catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
            {
                response?.Dispose();

                throw new TimeoutException($"No answer from {request.RequestUri?.Host} within {AttemptLimit.TotalSeconds:0.#} seconds", e);
            }
            catch
            {
                response?.Dispose();
                throw;
            }
        }
    }
}
