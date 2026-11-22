using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // The helpers every database test starts with. xunit orders the Database collection's classes by an ID hashed from the
    // test DLL's path, so any class may run before any other, and whatever it left behind must be gone.
    [Collection("Database")]
    public class RowsTests(MariaDbFixture fixture)
    {
        // Every sign-in writes a request to the bot, and every approval may, and the dashboard's limits count them before
        // it sends one, so a request or a pause left by an earlier test would change what a later one counts and shows
        [Fact]
        public async Task ReadyAsyncClearsRequestsAndSettings()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.RequestTable, MariaDbFixture.SettingsTable);
            await db.RootExecuteAsync($@"INSERT INTO {MariaDbFixture.RequestTable} (request_key, kind, actor_id, actor_name, actor_role, created_at, expires_at, signature)
                                         VALUES (REPEAT('a', 32), 'caches.clear', 1, 'Test Owner', 'owner', NOW(3), NOW(3), REPEAT('s', 32))");
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.SettingsTable} (id, posts_paused, pause_reason, updated_at) VALUES (1, 1, 'Testing', NOW(3))");
            Assert.Equal((1L, 1L), (await CountAsync(db, MariaDbFixture.RequestTable), await CountAsync(db, MariaDbFixture.SettingsTable)));

            await fixture.ReadyAsync();

            Assert.Equal((0L, 0L), (await CountAsync(db, MariaDbFixture.RequestTable), await CountAsync(db, MariaDbFixture.SettingsTable)));
        }

        static async Task<long> CountAsync(MariaDbFixture db, string table) => (await db.RootQueryAsync<long>($"SELECT COUNT(*) FROM {table}")).Single();
    }
}
