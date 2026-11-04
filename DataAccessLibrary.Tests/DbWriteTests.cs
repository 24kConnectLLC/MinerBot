using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using DataAccessLibrary.Blacklist;
using MySql.Data.MySqlClient;

namespace DataAccessLibrary.Tests
{
    // The rules every write follows, and the small helpers the queries share. No database is needed.
    public class DbWriteTests
    {
        // Every connection it makes fails to open with a deadlock, which a read tries once more
        sealed class DeadlockedDatabase : ISQLConnectionFactory
        {
            public int Created;

            public IDbConnection Create(DBConnectionName connectionName)
            {
                Interlocked.Increment(ref Created);
                return new DeadlockedConnection();
            }
        }

        // Dapper's async calls need a DbConnection, which makes its command before it opens
        sealed class DeadlockedConnection : DbConnection
        {
            [AllowNull]
            public override string ConnectionString { get; set; } = "fake";
            public override string Database => "fake";
            public override string DataSource => "fake";
            public override string ServerVersion => "fake";
            public override ConnectionState State => ConnectionState.Closed;

            public override void Open() => throw Deadlock();
            public override Task OpenAsync(CancellationToken cancellationToken) => Task.FromException(Deadlock());
            public override void Close() { }
            public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
            protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
            protected override DbCommand CreateDbCommand() => new MySqlCommand();
        }

        // MySqlException is sealed and only has internal constructors
        static MySqlException Deadlock() =>
            (MySqlException)typeof(MySqlException)
                .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string), typeof(int), typeof(Exception)])!
                .Invoke(["Deadlock found when trying to get lock", (int)MySqlErrorCode.LockDeadlock, null]);

        // Shows the fake makes a read try again, so the writes below would too if they went through the read's path
        [Fact]
        public async Task ReadIsTriedAgainAfterADeadlock()
        {
            var database = new DeadlockedDatabase();

            await Assert.ThrowsAsync<MySqlException>(() => new BlacklistData(database).GetBlacklist());

            Assert.Equal(2, database.Created);
        }

        [Fact]
        public async Task CountedWriteIsNeverTriedAgain()
        {
            var database = new DeadlockedDatabase();

            await Assert.ThrowsAsync<MySqlException>(() => new BlacklistData(database).RemovePlayerBan(12345));

            Assert.Equal(1, database.Created);
        }

        [Fact]
        public async Task TransactionIsNeverTriedAgain()
        {
            var database = new DeadlockedDatabase();
            var ran = false;

            await Assert.ThrowsAsync<MySqlException>(() => new BlacklistData(database).RunInTransaction(_ =>
            {
                ran = true;
                return Task.FromResult(0);
            }));

            Assert.Equal(1, database.Created);
            Assert.False(ran);
        }

        [Theory]
        [InlineData("plain", "plain")]
        [InlineData("100%", "100!%")]
        [InlineData("a_b", "a!_b")]
        [InlineData("Wow!", "Wow!!")]
        [InlineData("!%_", "!!!%!_")]
        [InlineData("back\\slash", "back\\slash")]
        public void LikeWildcardsAreEscaped(string text, string escaped)
        {
            Assert.Equal(escaped, DbBaseConnection.EscapeLike(text));
        }

        [Theory]
        [InlineData(0, 25, 25, 0)]
        [InlineData(2, 25, 25, 50)]
        [InlineData(0, 1, 1, 0)]
        [InlineData(0, 100, 100, 0)]
        [InlineData(int.MaxValue, 100, 100, 214748364700)]
        public void PagesCountFromZero(int page, int pageSize, int take, long skip)
        {
            Assert.Equal((take, skip), DbBaseConnection.RequirePage(page, pageSize));
        }

        [Theory]
        [InlineData(-1, 25)]
        [InlineData(0, 0)]
        [InlineData(0, 101)]
        [InlineData(0, -5)]
        public void PagesOutsideTheLimitsAreRefused(int page, int pageSize)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => DbBaseConnection.RequirePage(page, pageSize));
        }

        [Fact]
        public void LocalTimesAreRefused()
        {
            Assert.Throws<ArgumentException>(() => DbBaseConnection.RequireUtc(DateTime.Now, "at"));

            var utc = DateTime.UtcNow;
            Assert.Equal(utc, DbBaseConnection.RequireUtc(utc, "at"));

            // What the database gives back
            var unspecified = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Unspecified);
            Assert.Equal(unspecified, DbBaseConnection.RequireUtc(unspecified, "at"));
        }

        [Theory]
        [InlineData("abcdef", 3, "abc")]
        [InlineData("abc", 3, "abc")]
        [InlineData("ab", 3, "ab")]
        [InlineData("", 3, "")]
        [InlineData("abc", 0, "")]
        public void TextIsClippedToItsLimit(string text, int limit, string clipped)
        {
            Assert.Equal(clipped, DbBaseConnection.Clip(text, limit));
        }

        // MariaDB counts code points, so an emoji is one, and it's never cut in half
        [Fact]
        public void EmojiAtTheCutIsKeptWhole()
        {
            const string emoji = "\U0001F600";

            Assert.Equal("ab" + emoji, DbBaseConnection.Clip("ab" + emoji + "cd", 3));
            Assert.Equal("ab", DbBaseConnection.Clip("ab" + emoji + "cd", 2));
            Assert.Equal(emoji + emoji, DbBaseConnection.Clip(emoji + emoji + emoji, 2));
            Assert.Null(DbBaseConnection.Clip(null, 3));
            Assert.Equal(3, DbBaseConnection.CodePoints(emoji + "ab"));
        }
    }
}
