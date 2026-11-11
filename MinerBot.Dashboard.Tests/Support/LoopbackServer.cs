using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MinerBot.Dashboard.Tests.Support
{
    // A one-request HTTP server on a free loopback port, or on a Unix socket, for the healthcheck command: it answers with
    // what it was given, or never answers at all, and keeps the request it read
    internal sealed class LoopbackServer : IAsyncDisposable
    {
        readonly Socket listener;
        readonly CancellationTokenSource stop = new();
        readonly Task<string> serving;

        LoopbackServer(string? answer, string? unixSocket)
        {
            if (unixSocket == null)
            {
                listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                Port = ((IPEndPoint)listener.LocalEndPoint!).Port;
            }
            else
            {
                listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(unixSocket));
            }

            listener.Listen();
            serving = ServeAsync(answer);
        }

        // Only on TCP
        public int Port { get; }

        // The request line and headers, once they've arrived
        public Task<string> Request => serving;

        public static LoopbackServer Answering(int status, string reason, string body, string? unixSocket = null) =>
            new($"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}", unixSocket);

        public static LoopbackServer Silent() => new(null, null);

        async Task<string> ServeAsync(string? answer)
        {
            using var client = await listener.AcceptAsync(stop.Token);
            using var stream = new NetworkStream(client, ownsSocket: false);
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
            listener.Dispose();

            try
            {
                await serving.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
            {
            }

            stop.Dispose();
        }
    }
}
