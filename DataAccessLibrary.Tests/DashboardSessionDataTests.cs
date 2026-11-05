using System.Security.Cryptography;
using System.Text;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Dashboard.Models;
using DataAccessLibrary.Tests.Database;

namespace DataAccessLibrary.Tests
{
    [Collection("Database")]
    public class DashboardSessionDataTests(MariaDbFixture fixture)
    {
        static readonly DateTime Noon = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

        async Task<MariaDbFixture> StartAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.SessionTable);
            return db;
        }

        // Like the dashboard's: 256 random bits written as base64url
        static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        static DashboardSession Session(ulong userId = 1000000000000000001, DateTime? expiresAt = null, byte[]? ticket = null) => new()
        {
            user_id = userId,
            user_name = "Test Admin",
            created_at = Noon,
            last_seen_at = Noon,
            expires_at = expiresAt ?? Noon.AddHours(12),
            ip = "203.0.113.7",
            user_agent = "Test Browser/1.0",
            ticket = ticket ?? [1, 2, 3]
        };

        static void AssertSame(DashboardSession expected, DashboardSession? actual)
        {
            Assert.NotNull(actual);
            Assert.Equal(expected.user_id, actual.user_id);
            Assert.Equal(expected.user_name, actual.user_name);
            Assert.Equal(expected.created_at, actual.created_at);
            Assert.Equal(expected.last_seen_at, actual.last_seen_at);
            Assert.Equal(expected.expires_at, actual.expires_at);
            Assert.Equal(expected.ip, actual.ip);
            Assert.Equal(expected.user_agent, actual.user_agent);
            Assert.Equal(expected.ticket, actual.ticket);
        }

        // Two sign-ins of one person at the same moment, each adding its session and then capping the rest, deadlock on each
        // other's new row. The one that loses runs again, so both sign in.
        [Fact]
        public async Task TwoSignInsOfOnePersonAtOnceBothSucceed()
        {
            var db = await StartAsync();
            var sessions = db.Sessions();
            var firstAdded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondAdded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Both add their session before either caps the rest, the order in which they deadlock
            Task<bool> SignIn(string key, TaskCompletionSource done, TaskCompletionSource other) => sessions.RunInTransaction(async transaction =>
            {
                await sessions.CreateSession(key, Session(), transaction);

                done.TrySetResult();
                await other.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

                await sessions.DeleteOldestSessions(Session().user_id, 5, transaction);
                return true;
            });

            var first = NewKey();
            var second = NewKey();
            var results = await Task.WhenAll(SignIn(first, firstAdded, secondAdded), SignIn(second, secondAdded, firstAdded))
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal([true, true], results);
            Assert.NotNull(await sessions.GetSession(first, Noon));
            Assert.NotNull(await sessions.GetSession(second, Noon));
        }

        // Fails if the key itself is stored, which would let anyone who reads the table sign in
        [Fact]
        public async Task KeyIsStoredOnlyAsItsHash()
        {
            var db = await StartAsync();
            var key = NewKey();

            await db.Sessions().CreateSession(key, Session());

            var hash = Assert.Single(await db.RootQueryAsync<byte[]>($"SELECT id_hash FROM {MariaDbFixture.SessionTable}"));
            Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(key)), hash);

            var row = Assert.Single(await db.RootQueryAsync<dynamic>($"SELECT * FROM {MariaDbFixture.SessionTable}"));
            var keyBytes = Encoding.UTF8.GetBytes(key);

            foreach (var column in (IDictionary<string, object?>)row)
            {
                var bytes = column.Value switch
                {
                    byte[] raw => raw,
                    null => [],
                    var value => Encoding.UTF8.GetBytes(Convert.ToString(value) ?? "")
                };

                Assert.True(bytes.AsSpan().IndexOf(keyBytes) < 0, $"The column {column.Key} holds the key");
                Assert.DoesNotContain(key, Encoding.UTF8.GetString(bytes));
            }
        }

        [Fact]
        public async Task SessionRoundTripsWithABigTicket()
        {
            var db = await StartAsync();
            var sessions = db.Sessions();
            var key = NewKey();
            var session = Session(ticket: RandomNumberGenerator.GetBytes(100 * 1024));
            session.last_seen_at = Noon.AddMilliseconds(123);

            await sessions.CreateSession(key, session);

            AssertSame(session, await sessions.GetSession(key, Noon));
            Assert.Null(await sessions.GetSession(NewKey(), Noon));
        }

        // Fails if the read forgets the session's end
        [Fact]
        public async Task EndedSessionIsNotFound()
        {
            var db = await StartAsync();
            var sessions = db.Sessions();
            var key = NewKey();

            await sessions.CreateSession(key, Session(expiresAt: Noon.AddHours(1)));

            Assert.NotNull(await sessions.GetSession(key, Noon.AddHours(1).AddMilliseconds(-1)));
            Assert.Null(await sessions.GetSession(key, Noon.AddHours(1)));
            Assert.Null(await sessions.GetSession(key, Noon.AddDays(1)));
        }

        [Fact]
        public async Task TouchAndRenewChangeOnlyTheirOwnColumns()
        {
            var db = await StartAsync();
            var sessions = db.Sessions();
            var key = NewKey();
            var session = Session();
            await sessions.CreateSession(key, session);

            Assert.True(await sessions.TouchSession(key, Noon.AddMinutes(5)));
            session.last_seen_at = Noon.AddMinutes(5);
            AssertSame(session, await sessions.GetSession(key, Noon));

            // The same time again still finds the session
            Assert.True(await sessions.TouchSession(key, Noon.AddMinutes(5)));

            // A renewal changes the ticket but never the session's end, which is the latest it can last
            Assert.True(await sessions.RenewSession(key, [9, 8, 7]));
            session.ticket = [9, 8, 7];
            AssertSame(session, await sessions.GetSession(key, Noon));

            Assert.False(await sessions.TouchSession(NewKey(), Noon));
            Assert.False(await sessions.RenewSession(NewKey(), [1]));
        }

        // A sign-in can cap how many sessions one person has: the ones used most recently stay
        [Fact]
        public async Task DeleteOldestSessionsKeepsTheMostRecentlyUsed()
        {
            var db = await StartAsync();
            var sessions = db.Sessions();
            var keys = Enumerable.Range(0, 4).Select(_ => NewKey()).ToList();

            // Signed in in this order, but the first was used last
            for (var i = 0; i < keys.Count; i++)
            {
                var session = Session(userId: 1);
                session.created_at = Noon.AddMinutes(i);
                session.last_seen_at = i == 0 ? Noon.AddHours(1) : Noon.AddMinutes(i);
                await sessions.CreateSession(keys[i], session);
            }

            var otherUser = NewKey();
            await sessions.CreateSession(otherUser, Session(userId: 2));

            Assert.Equal(2, await sessions.DeleteOldestSessions(1, 2));

            Assert.NotNull(await sessions.GetSession(keys[0], Noon));
            Assert.Null(await sessions.GetSession(keys[1], Noon));
            Assert.Null(await sessions.GetSession(keys[2], Noon));
            Assert.NotNull(await sessions.GetSession(keys[3], Noon));

            // Keeping as many as there are changes nothing, and keeping none ends every one of that user's
            Assert.Equal(0, await sessions.DeleteOldestSessions(1, 2));
            Assert.Equal(0, await sessions.DeleteOldestSessions(1, 5));
            Assert.Equal(2, await sessions.DeleteOldestSessions(1, 0));
            Assert.NotNull(await sessions.GetSession(otherUser, Noon));
        }

        // Ending a session commits together with its audit row on MinerBot's own connection, or neither happens. Fails if a
        // write ignores the transaction it's given.
        [Fact]
        public async Task SessionChangesAndTheirAuditRowCommitTogether()
        {
            var db = await StartAsync();
            await db.ClearAsync(MariaDbFixture.AuditTable);
            var sessions = db.Sessions();
            var (kept, ended, added) = (NewKey(), NewKey(), NewKey());
            await sessions.CreateSession(kept, Session());
            await sessions.CreateSession(ended, Session());

            await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.RunInTransaction<bool>(async transaction =>
            {
                await sessions.CreateSession(added, Session(), transaction);
                Assert.True(await sessions.DeleteSession(kept, transaction));
                Assert.Equal(2, await sessions.DeleteOldestSessions(1000000000000000001, 0, transaction));
                await db.Audit().AddAudit(Audit("session.end"), transaction);
                throw new InvalidOperationException("The page stopped halfway");
            }));

            Assert.NotNull(await sessions.GetSession(kept, Noon));
            Assert.NotNull(await sessions.GetSession(ended, Noon));
            Assert.Null(await sessions.GetSession(added, Noon));
            Assert.Equal(0, (await db.Audit().ListAudit(new AuditQuery())).Total);

            var deleted = await sessions.RunInTransaction(async transaction =>
            {
                var count = await sessions.DeleteOtherSessions(1000000000000000001, kept, transaction);
                await db.Audit().AddAudit(Audit("session.end_others"), transaction);
                return count;
            });

            Assert.Equal(1, deleted);
            Assert.Null(await sessions.GetSession(ended, Noon));
            Assert.Equal(["session.end_others"], (await db.Audit().ListAudit(new AuditQuery())).Rows.Select(row => row.action));
        }

        static AuditEntry Audit(string action) => new()
        {
            at = Noon,
            actor_id = 1000000000000000001,
            actor_name = "Test Owner",
            actor_role = AuditRole.Owner,
            action = action,
            outcome = AuditOutcome.Ok,
            target_type = "session"
        };

        [Fact]
        public async Task DeleteSessionSaysWhetherItWasThere()
        {
            var db = await StartAsync();
            var sessions = db.Sessions();
            var key = NewKey();
            await sessions.CreateSession(key, Session());

            Assert.True(await sessions.DeleteSession(key));
            Assert.False(await sessions.DeleteSession(key));
            Assert.Null(await sessions.GetSession(key, Noon));
        }

        [Fact]
        public async Task DeleteOtherSessionsKeepsTheOneNamedAndOtherUsers()
        {
            var db = await StartAsync();
            var sessions = db.Sessions();
            var (first, kept, third, otherUser) = (NewKey(), NewKey(), NewKey(), NewKey());

            await sessions.CreateSession(first, Session(userId: 1));
            await sessions.CreateSession(kept, Session(userId: 1));
            await sessions.CreateSession(third, Session(userId: 1));
            await sessions.CreateSession(otherUser, Session(userId: 2));

            Assert.Equal(2, await sessions.DeleteOtherSessions(1, kept));

            Assert.Null(await sessions.GetSession(first, Noon));
            Assert.Null(await sessions.GetSession(third, Noon));
            Assert.NotNull(await sessions.GetSession(kept, Noon));
            Assert.NotNull(await sessions.GetSession(otherUser, Noon));
        }

        [Fact]
        public async Task DeleteExpiredTakesOnlySessionsEndedBeforeTheCutoff()
        {
            var db = await StartAsync();
            var sessions = db.Sessions();
            var (before, at, after) = (NewKey(), NewKey(), NewKey());

            await sessions.CreateSession(before, Session(expiresAt: Noon.AddMilliseconds(-1)));
            await sessions.CreateSession(at, Session(expiresAt: Noon));
            await sessions.CreateSession(after, Session(expiresAt: Noon.AddMilliseconds(1)));

            Assert.Equal(1, await sessions.DeleteExpiredSessions(Noon));

            var left = await db.RootQueryAsync<DateTime>($"SELECT expires_at FROM {MariaDbFixture.SessionTable} ORDER BY expires_at");
            Assert.Equal([Noon, Noon.AddMilliseconds(1)], left);
        }

        // The browser sends the user agent, so it can be any length, and strict mode would fail the sign-in over it
        [Fact]
        public async Task LongTextIsClipped()
        {
            var db = await StartAsync();
            var sessions = db.Sessions();
            var key = NewKey();
            var session = Session();
            session.user_agent = new string('a', 5000);
            session.user_name = new string('n', 150);

            await sessions.CreateSession(key, session);

            var stored = await sessions.GetSession(key, Noon);
            Assert.Equal(new string('a', 200), stored?.user_agent);
            Assert.Equal(new string('n', 100), stored?.user_name);
        }
    }
}
