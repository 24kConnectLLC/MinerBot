using System.Net.Sockets;

namespace MinerBot.Dashboard.Tests.Support
{
    // Real Unix sockets for the tests of Dashboard:UnixSocket, in a folder of the test's own. A socket's path holds at most
    // 100 bytes, so the folder is in the system's temporary folder, which is short, rather than under the test output.
    internal sealed class TestSockets : IDisposable
    {
        public TestSockets() => Folder = Directory.CreateTempSubdirectory("mbsock-").FullName;

        public string Folder { get; }

        public string PathOf(string name) => Path.Combine(Folder, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Folder, recursive: true);
            }
            catch (IOException)
            {
                // Left in the temporary folder, which the system empties
            }
        }

        // A socket that nothing listens on any more, as a dashboard that was killed leaves one. A socket .NET binds is deleted
        // when it's closed, so it's bound under another name and moved to the path first, and the close deletes nothing.
        public static void LeaveSocket(string path)
        {
            var bound = path + ".bound";

            using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                socket.Bind(new UnixDomainSocketEndPoint(bound));
                socket.Listen();
                File.Move(bound, path);
            }

            Assert.True(Path.Exists(path), "The socket left behind isn't there");
            Assert.False(Path.Exists(bound));
        }

        // A socket something listens on, which accepts connections and never answers
        public static Socket Listening(string path)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(path));
            socket.Listen();
            return socket;
        }

        // A client whose every connection goes to the socket, as nginx's does. The address only gives the Host header.
        public static HttpClient Client(string path, string address = "http://localhost")
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectCallback = async (_, cancellationToken) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };

            return new HttpClient(handler) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
        }

        // Whether anything accepts a connection on the TCP port
        public static async Task<bool> TcpAnswersAsync(int port, CancellationToken cancellationToken)
        {
            using var client = new TcpClient();

            try
            {
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port, cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }
}
