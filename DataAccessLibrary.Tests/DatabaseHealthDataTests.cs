using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DataAccessLibrary.Health;
using DataAccessLibrary.Tests.Database;

namespace DataAccessLibrary.Tests
{
    // The dashboard's readiness check asks each database this. It answers yes or no and never throws, so no driver text,
    // which names the login and the host, can reach the health endpoint. Only the first test needs MariaDB.
    [Collection("Database")]
    public class DatabaseHealthDataTests(MariaDbFixture fixture)
    {
        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

        // Made up, and never connected to except where a test listens on the port itself
        static DatabaseHealthData Probe(int port) => new(new SQLConnectionFactory(new Dictionary<DBConnectionName, string>
        {
            [DBConnectionName.Bot] = $"Server=127.0.0.1;Port={port};Database=bot_db;Uid=user;Pwd=password;SslMode=Disabled;Connection Timeout=10"
        }));

        // The moment it's free, nothing listens on it
        static int ClosedPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        [Fact]
        public async Task BothDatabasesAnswer()
        {
            var db = fixture.Require();
            var health = new DatabaseHealthData(db.Factory);

            Assert.True(await health.Answers(DBConnectionName.Armor, TimeSpan.FromSeconds(5)).WaitAsync(WaitLimit, TestContext.Current.CancellationToken));
            Assert.True(await health.Answers(DBConnectionName.Bot, TimeSpan.FromSeconds(5)).WaitAsync(WaitLimit, TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ClosedPortDoesNotAnswer()
        {
            var watch = Stopwatch.StartNew();

            Assert.False(await Probe(ClosedPort()).Answers(DBConnectionName.Bot, TimeSpan.FromSeconds(10)).WaitAsync(WaitLimit, TestContext.Current.CancellationToken));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The answer took {watch.Elapsed}");
        }

        // A server that takes the connection but never sends MariaDB's greeting costs the limit given, not the ordinary 15
        // seconds or the driver's own 10. Fails if the limit isn't applied.
        [Fact]
        public async Task SilentServerFailsWithinTheLimit()
        {
            using var silent = new TcpListener(IPAddress.Loopback, 0);
            silent.Start();
            var port = ((IPEndPoint)silent.LocalEndpoint).Port;
            var watch = Stopwatch.StartNew();

            Assert.False(await Probe(port).Answers(DBConnectionName.Bot, TimeSpan.FromSeconds(1)).WaitAsync(WaitLimit, TestContext.Current.CancellationToken));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"The answer took {watch.Elapsed}");
        }

        [Fact]
        public async Task MissingConnectionStringDoesNotThrow()
        {
            var health = new DatabaseHealthData(new SQLConnectionFactory(new Dictionary<DBConnectionName, string>
            {
                [DBConnectionName.Bot] = ""
            }));

            Assert.False(await health.Answers(DBConnectionName.Bot, TimeSpan.FromSeconds(5)).WaitAsync(WaitLimit, TestContext.Current.CancellationToken));
            Assert.False(await health.Answers(DBConnectionName.Armor, TimeSpan.FromSeconds(5)).WaitAsync(WaitLimit, TestContext.Current.CancellationToken));
        }
    }
}
