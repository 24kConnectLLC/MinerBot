using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace MinerBot.Dashboard.Tests.Support
{
    // A database server that takes the connection and then never says anything, as a hung one would. Its connection
    // string waits a minute, far longer than the dashboard's own limits, so only those can end the wait.
    internal sealed class SilentDatabase : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);

        public SilentDatabase()
        {
            listener.Start();

            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Connection = $"Server=127.0.0.1;Port={port};Database=silent_db;Uid=silent_login;Pwd={Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))};SslMode=Disabled;Connection Timeout=60";
        }

        public string Connection { get; }

        // Closing the listener resets every connection it held, so nothing is left waiting after the test
        public void Dispose() => listener.Dispose();
    }
}
