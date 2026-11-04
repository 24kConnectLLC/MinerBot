using System.Collections.Concurrent;
using System.Net;

namespace SteamWebAPIAccess.Tests
{
    // Stands in for the network under WebRetryHandler. It records each request it gets
    // and answers with the next answer of the test.
    internal sealed class FakeWeb : HttpMessageHandler
    {
        readonly ConcurrentQueue<Func<CancellationToken, Task<HttpResponseMessage>>> answers = new();
        readonly ConcurrentQueue<(HttpMethod Method, Uri? Uri)> requests = new();

        public IReadOnlyList<(HttpMethod Method, Uri? Uri)> Requests => requests.ToList();

        public FakeWeb Answer(HttpStatusCode status, string body = "") =>
            Add(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));

        // A real connection waits like this on a server that never answers, until it's canceled
        public FakeWeb Never() => Add(async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        // The headers arrive, and the body stops after a few bytes
        public FakeWeb BodyThatNeverEnds() =>
            Add(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new EndlessContent() }));

        FakeWeb Add(Func<CancellationToken, Task<HttpResponseMessage>> answer)
        {
            answers.Enqueue(answer);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Enqueue((request.Method, request.RequestUri));

            if (!answers.TryDequeue(out var answer))
                throw new InvalidOperationException($"The test has no answer left for {request.RequestUri}");

            return answer(cancellationToken);
        }

        sealed class EndlessContent : HttpContent
        {
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
                SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            {
                await stream.WriteAsync("start"u8.ToArray(), cancellationToken);

                // A real body can be given up on by closing its connection, so a read that couldn't would hang the tests instead
                if (!cancellationToken.CanBeCanceled)
                    throw new IOException("The body was read without a way to give up on it");

                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
        }
    }
}
