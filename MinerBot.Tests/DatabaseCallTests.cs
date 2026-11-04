using System.Net;
using System.Net.Sockets;
using DataAccessLibrary;
using DataAccessLibrary.Armor;
using DataAccessLibrary.Discord;
using DataAccessLibrary.Discord.Models;
using Microsoft.Extensions.Caching.Memory;
using MySql.Data.MySqlClient;

namespace MinerBot.Tests
{
    // The calls go through Dapper's real async path, on a fake database or a silent MySQL server.
    // Only the tests where the limit has to fire make it short.
    public class DatabaseCallTests : IDisposable
    {
        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);
        static readonly TimeSpan ShortLimit = TimeSpan.FromMilliseconds(100);

        readonly MemoryCache cache = new(new MemoryCacheOptions());

        public void Dispose() => cache.Dispose();

        ArmorData Armor(ISQLConnectionFactory database, TimeSpan? limit = null)
        {
            var armor = new ArmorData(database, cache);
            armor.CallLimit = limit ?? armor.CallLimit;
            return armor;
        }

        static Guild AnyGuild() => new() { guild_id = 1 };

        // A call that never ends must not pass as one that timed out, so it has to end inside the guard
        static async Task<TimeoutException> TimesOutAsync(Task call)
        {
            var finished = await Task.WhenAny(call, Task.Delay(WaitLimit, TestContext.Current.CancellationToken));

            Assert.Same(call, finished);
            return await Assert.ThrowsAsync<TimeoutException>(() => call);
        }

        [Fact]
        public async Task ReadThatNeverEndsFailsWithATimeout()
        {
            var database = new FakeDatabase().Never();

            var timeout = await TimesOutAsync(Armor(database, ShortLimit).GetAllArmorNames());

            Assert.StartsWith("The database didn't answer within", timeout.Message);
        }

        [Fact]
        public async Task WriteThatNeverEndsFailsWithATimeout()
        {
            var database = new FakeDatabase().Never();

            await TimesOutAsync(new GuildData(database) { CallLimit = ShortLimit }.UpdateGuild(AnyGuild()));
        }

        // A driver that blocks before it hands back a task would hold the caller before a plain limit started
        [Fact]
        public async Task ReadThatBlocksItsThreadStillTimesOut()
        {
            var database = new FakeDatabase().Block();
            var armor = Armor(database, ShortLimit);

            try
            {
                await TimesOutAsync(Task.Run(() => armor.GetAllArmorNames(), TestContext.Current.CancellationToken));
            }
            finally
            {
                database.Gate.Set();
            }
        }

        [Fact]
        public async Task ReadThatTimedOutIsNotTriedAgain()
        {
            var database = new FakeDatabase().Never().Rows();

            await TimesOutAsync(Armor(database, ShortLimit).GetAllArmorNames());

            // On a slow machine the limit can fire before the only try reaches the database
            await database.FirstCall.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
            Assert.Equal(1, database.Calls);
        }

        [Theory]
        [InlineData((int)MySqlErrorCode.UnableToConnectToHost, false)]
        [InlineData((int)MySqlErrorCode.LockWaitTimeout, false)]
        [InlineData((int)MySqlErrorCode.LockDeadlock, false)]
        [InlineData(0, true)]
        public async Task ReadThatHitATransientErrorIsTriedOnceMore(int number, bool lostConnection)
        {
            // MySql.Data reports a dropped connection with no number and the stream's error inside
            var error = FakeDatabase.MySqlError(number, lostConnection ? new IOException("The connection was closed") : null);
            var database = new FakeDatabase().Error(error).Rows();

            var names = await Armor(database).GetAllArmorNames();

            Assert.Equal(FakeDatabase.ArmorNames, names);
            Assert.Equal(2, database.Calls);
        }

        [Fact]
        public async Task ReadIsTriedNoMoreThanTwice()
        {
            var deadlock = FakeDatabase.MySqlError((int)MySqlErrorCode.LockDeadlock);
            var database = new FakeDatabase().Error(deadlock).Error(deadlock).Rows();

            await Assert.ThrowsAsync<MySqlException>(() => Armor(database).GetAllArmorNames());

            Assert.Equal(2, database.Calls);
        }

        [Fact]
        public async Task ReadThatFailedForAnotherReasonIsNotTriedAgain()
        {
            var database = new FakeDatabase().Error(FakeDatabase.MySqlError((int)MySqlErrorCode.NoSuchTable)).Rows();

            await Assert.ThrowsAsync<MySqlException>(() => Armor(database).GetAllArmorNames());

            Assert.Equal(1, database.Calls);
        }

        // The first try may have gone through
        [Fact]
        public async Task WriteThatHitADeadlockIsNotRepeated()
        {
            var database = new FakeDatabase().Error(FakeDatabase.MySqlError((int)MySqlErrorCode.LockDeadlock)).Rows();

            await Assert.ThrowsAsync<MySqlException>(() => new GuildData(database).UpdateGuild(AnyGuild()));

            Assert.Equal(1, database.Calls);
        }

        [Fact]
        public async Task ReadThatWorksIsReadOnce()
        {
            var database = new FakeDatabase().Rows();

            Assert.Equal(FakeDatabase.ArmorNames, await Armor(database).GetAllArmorNames());
            Assert.Equal(1, database.Calls);
        }

        // Reads of one row, such as /armor lookup's and the guild and blacklist ones, take a path of their own
        [Fact]
        public async Task ReadOfOneRowThatNeverEndsFailsWithATimeout()
        {
            var database = new FakeDatabase().Never();

            await TimesOutAsync(Armor(database, ShortLimit).GetArmorStyle("Armor One"));
        }

        [Fact]
        public async Task ReadOfOneRowThatHitATransientErrorIsTriedOnceMore()
        {
            var database = new FakeDatabase().Error(FakeDatabase.MySqlError((int)MySqlErrorCode.LockDeadlock)).Rows();

            var armor = await Armor(database).GetArmorStyle("Armor One");

            Assert.Equal(FakeDatabase.ArmorNames[0], armor.ArmorName);
            Assert.Equal(2, database.Calls);
        }

        // The real driver, on a server that takes the connection and never sends its greeting
        [Fact]
        public async Task SilentMySqlServerTimesOut()
        {
            using var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();

            // Made up, the server never gets as far as asking for them
            var connectionString = $"Server=127.0.0.1;Port={((IPEndPoint)server.LocalEndpoint).Port};Database=minerbot;Uid=user;Pwd=password;SslMode=Disabled";
            var database = new SQLConnectionFactory(new Dictionary<DBConnectionName, string> { [DBConnectionName.Armor] = connectionString });
            var armor = Armor(database, TimeSpan.FromMilliseconds(200));

            await TimesOutAsync(Task.Run(() => armor.GetAllArmorNames(), TestContext.Current.CancellationToken));
        }
    }
}
