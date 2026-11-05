using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MinerBot.Dashboard.Tests.Support
{
    // A one-request HTTP server on a free loopback port, for the healthcheck command: it answers with what it was given,
    // or never answers at all, and keeps the request it read
    internal sealed class LoopbackServer : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new();
        readonly Task<string> serving;

        LoopbackServer(string? answer)
        {
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            serving = ServeAsync(answer);
        }

        public int Port { get; }

        // The request line and headers, once they've arrived
        public Task<string> Request => serving;

        public static LoopbackServer Answering(int status, string reason, string body) =>
            new($"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");

        public static LoopbackServer Silent() => new(null);

        async Task<string> ServeAsync(string? answer)
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token);
            var stream = client.GetStream();
            var head = new StringBuilder();
            var buffer = new byte[1024];

            while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, stop.Token);

                if (read == 0)
                    break;

                head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            if (answer == null)
                await Task.Delay(Timeout.Infinite, stop.Token);
            else
                await stream.WriteAsync(Encoding.UTF8.GetBytes(answer), stop.Token);

            return head.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();

            try
            {
                await serving.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
            {
            }

            listener.Dispose();
            stop.Dispose();
        }
    }
}
