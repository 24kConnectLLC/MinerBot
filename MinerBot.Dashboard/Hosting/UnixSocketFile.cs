using System.Net.Sockets;
using System.Runtime.InteropServices;
using MinerBot.Dashboard.Settings;

namespace MinerBot.Dashboard.Hosting
{
    // The socket file, when Dashboard:UnixSocket is set. A dashboard that stops cleanly deletes its socket, but one that's
    // killed, by the memory limit or at the end of the stop's grace period, leaves it behind, and Kestrel can't bind over it.
    // So before Kestrel binds, a socket that nothing listens on is deleted, and only a socket, never another kind of file.
    // Once Kestrel listens, the socket is opened to its group, nobody, which nginx's workers run as and which it gets from
    // the folder's setgid bit.
    internal sealed class UnixSocketFile(IConfiguration configuration, ILogger<UnixSocketFile> logger) : IHostedLifecycleService
    {
        // Read and write for the dashboard and the group nobody, nginx's workers among it, and nothing for anyone else.
        // Kestrel makes the socket with the process's umask, which leaves the group without the write that connecting needs.
        internal const UnixFileMode Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

        static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(2);

        // Runs before any service starts, the web server among them
        public async Task StartingAsync(CancellationToken cancellationToken)
        {
            if (DashboardSettings.UnixSocket(configuration) is { } path)
                await RemoveLeftoverAsync(path, logger, cancellationToken);
        }

        // Runs once every service has started, so Kestrel has made the socket
        public Task StartedAsync(CancellationToken cancellationToken)
        {
            // Windows has no Unix file modes. Its socket gets the folder's permissions.
            if (DashboardSettings.UnixSocket(configuration) is { } path && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, Mode);

            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        // Deletes the socket at the path when nothing answers on it. Anything else is left as it is, and Kestrel's bind then
        // fails with "address already in use": another kind of file, which the settings check refuses first, or a socket a
        // running dashboard still listens on, which a second one must never take from it.
        internal static async Task RemoveLeftoverAsync(string path, ILogger logger, CancellationToken cancellationToken)
        {
            if (!UnixSockets.IsSocket(path))
                return;

            if (await AnswersAsync(path, cancellationToken))
            {
                logger.LogWarning("Something still listens on the socket at {Path}, so it's left as it is, and this dashboard can't listen there", path);
                return;
            }

            try
            {
                File.Delete(path);
                logger.LogInformation("Deleted the socket at {Path}, which a dashboard that was stopped without closing it left behind", path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("The socket at {Path}, which a dashboard that was stopped without closing it left behind, couldn't be deleted: {Type}", path, e.GetType().Name);
            }
        }

        // Whether something accepts a connection there. A socket left behind refuses at once. One that takes longer than the
        // limit counts as answering, so it's never deleted.
        static async Task<bool> AnswersAsync(string path, CancellationToken cancellationToken)
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(ConnectLimit);

            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), limit.Token);
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return true;
            }
        }
    }

    internal static class UnixSockets
    {
        // statx and the parts of its answer read here, from Linux's own headers. Its layout is the same on every processor.
        const int CurrentFolder = -100;            // AT_FDCWD
        const int DontFollowLinks = 0x100;         // AT_SYMLINK_NOFOLLOW
        const uint TypeWanted = 0x1;               // STATX_TYPE, both what's asked for and, in stx_mask, what came back
        const int AnswerBytes = 256;               // sizeof(struct statx)
        const int ModeOffset = 28;                 // where stx_mode is in it
        const int TypeBits = 0xF000;               // S_IFMT
        const int SocketType = 0xC000;             // S_IFSOCK

        [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
        static extern int Statx(int folder, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] answer);

        // FindFirstFileW and the parts of its answer read here, from Windows' own headers. It reads the folder's entry for
        // the path, so a link isn't followed.
        const int FindAnswerBytes = 592;           // sizeof(WIN32_FIND_DATAW)
        const uint ReparsePoint = 0x400;           // FILE_ATTRIBUTE_REPARSE_POINT, in dwFileAttributes at the start
        const int TagOffset = 36;                  // where dwReserved0 is, which holds a reparse point's tag
        const uint UnixSocketTag = 0x80000023;     // IO_REPARSE_TAG_AF_UNIX
        const nint NothingFound = -1;              // INVALID_HANDLE_VALUE

        // FindFirstFileW takes these as wildcards in the path's last part once Windows has normalized the path, as
        // Path.GetFullPath does (. and .. taken out, / made \, a \\?\ path left as it is), so they could find another
        // file, and it stops at a NUL anywhere. No socket's name holds any of them.
        const string Wildcards = "*?<>\"";

        [DllImport("kernel32", EntryPoint = "FindFirstFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern nint FindFirstFile(string path, [Out] byte[] answer);

        [DllImport("kernel32", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FindClose(nint search);

        // Whether the path is a Unix socket itself, and not a link to one or any other kind of file. .NET can't tell a
        // socket from a file, so the system is asked: on Linux the kernel, and on Windows, which the tests use, the folder's
        // entry, where a socket is a reparse point with the AF_UNIX tag. Other reparse points, such as an app execution
        // alias or a file that's only online, aren't sockets. Anywhere else, and whenever it can't tell, the answer is no,
        // so nothing that might be another kind of file is ever deleted.
        internal static bool IsSocket(string path)
        {
            if (OperatingSystem.IsLinux())
            {
                var answer = new byte[AnswerBytes];

                try
                {
                    return Statx(CurrentFolder, path, DontFollowLinks, TypeWanted, answer) == 0
                        && (BitConverter.ToUInt32(answer, 0) & TypeWanted) != 0
                        && (BitConverter.ToUInt16(answer, ModeOffset) & TypeBits) == SocketType;
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
                {
                    return false;
                }
            }

            if (OperatingSystem.IsWindows())
            {
                if (path.Length == 0 || path.Contains('\0'))
                    return false;

                string full;

                try
                {
                    full = Path.GetFullPath(path);
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
                {
                    return false;
                }

                if (Path.GetFileName(full.AsSpan()).IndexOfAny(Wildcards) >= 0)
                    return false;

                var answer = new byte[FindAnswerBytes];
                var search = FindFirstFile(path, answer);

                if (search == NothingFound)
                    return false;

                FindClose(search);
                return (BitConverter.ToUInt32(answer, 0) & ReparsePoint) != 0 && BitConverter.ToUInt32(answer, TagOffset) == UnixSocketTag;
            }

            return false;
        }
    }
}
