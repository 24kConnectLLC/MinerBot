using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace MinerBot.Dashboard.Tests.Support
{
    // Connection strings for a database that isn't there: a loopback port nothing listens on, with a made-up login,
    // password and database names that a test can look for in what the app shows
    internal sealed class DeadDatabase
    {
        public int Port { get; } = ClosedPort();
        public string Login { get; } = "dead_login_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        public string Password { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        public string SiteDatabase { get; } = "dead_site_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        public string BotDatabase { get; } = "dead_bot_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

        public string SiteConnection => Connection(SiteDatabase);
        public string BotConnection => Connection(BotDatabase);

        string Connection(string database) =>
            $"Server=127.0.0.1;Port={Port};Database={database};Uid={Login};Pwd={Password};SslMode=Disabled;Connection Timeout=3";

        // The moment it's free, nothing listens on it
        public static int ClosedPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
