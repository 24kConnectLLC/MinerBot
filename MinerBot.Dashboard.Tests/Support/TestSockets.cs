using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MinerBot.Dashboard.Tests.Support
{
    // Real Unix sockets for the tests of Dashboard:UnixSocket, and links and other files in their place, in a folder of the
    // test's own. A socket's path holds at most 100 bytes, so the folder is in the system's temporary folder, which is
    // short, rather than under the test output.
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

        // A link at the path to the target, or false where the system won't let the test make one: Windows lets only an
        // administrator, or anyone in developer mode
        public static bool TryLink(string path, string target)
        {
            try
            {
                File.CreateSymbolicLink(path, target);
                return true;
            }
            catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // The tag of an app execution alias, such as the python3.exe Windows keeps in WindowsApps: a reparse point that's
        // neither a link nor a socket, as a file that's only online or a deduplicated one is too
        public const uint AppExecutionAliasTag = 0x8000001B;   // IO_REPARSE_TAG_APPEXECLINK

        const uint SetReparsePoint = 0x000900A4;               // FSCTL_SET_REPARSE_POINT

        [DllImport("kernel32", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool DeviceIoControl(SafeFileHandle file, uint code, byte[] input, int inputBytes, nint output, int outputBytes, out int returned, nint overlapped);

        // Makes the file a reparse point with this tag and four bytes of data, on Windows, where it needs no administrator
        public static void Tag(string path, uint tag)
        {
            // The tag, the data's length and a reserved word, then the data
            var buffer = new byte[12];
            BitConverter.GetBytes(tag).CopyTo(buffer, 0);
            BitConverter.GetBytes((ushort)4).CopyTo(buffer, 4);

            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Write);

            if (!DeviceIoControl(file, SetReparsePoint, buffer, buffer.Length, 0, 0, out _, 0))
                throw new Win32Exception();
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
