using DataAccessLibrary.Blacklist.Models;
using DataAccessLibrary.Tests.Database;

namespace DataAccessLibrary.Tests
{
    // The website's blacklist holds 32-bit Steam account numbers in a DECIMAL(10,0). Made-up players only.
    [Collection("Database")]
    public class BlacklistDataTests(MariaDbFixture fixture)
    {
        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

        async Task<MariaDbFixture> StartAsync(params BlacklistModel[] players)
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.BlacklistTable);

            foreach (var player in players)
                await db.Blacklist().InsertPlayerBan(player);

            return db;
        }

        static BlacklistModel Player(uint accountNumber, string? username, string? reason = "Testing", DateTime? expires = null, string? notes = "Made up") => new()
        {
            steam_id = accountNumber,
            username = username!,
            reason = reason!,
            expire_date = expires,
            notes = notes!
        };

        static IEnumerable<uint> Accounts(Page<BlacklistModel> page) => page.Rows.Select(player => player.steam_id);

        async Task<long> RowsAsync() => (await fixture.RootQueryAsync<long>($"SELECT COUNT(*) FROM {MariaDbFixture.BlacklistTable}")).Single();

        // Two new bans at the same moment, each checking first that its player isn't banned yet, lock the same gap between
        // two rows and then deadlock adding their rows into it. The one that loses runs again, so both are saved.
        [Fact]
        public async Task TwoNewBansAtOnceAreBothSaved()
        {
            var db = await StartAsync(Player(10, "Test Player"), Player(900000, "Another Player"));
            var blacklist = db.Blacklist();
            var firstChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Both check before either adds, the order in which they deadlock
            Task<bool> Ban(uint accountNumber, TaskCompletionSource done, TaskCompletionSource other) => blacklist.RunInTransaction(async transaction =>
            {
                if (await blacklist.GetBannedPlayer(accountNumber, transaction) != null)
                    return false;

                done.TrySetResult();
                await other.Task.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

                await blacklist.InsertPlayerBan(Player(accountNumber, "New Player"), transaction);
                return true;
            });

            var results = await Task.WhenAll(Ban(1000, firstChecked, secondChecked), Ban(2000, secondChecked, firstChecked))
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal([true, true], results);
            Assert.NotNull(await blacklist.GetBannedPlayer(1000));
            Assert.NotNull(await blacklist.GetBannedPlayer(2000));
        }

        // Before the fix this failed with MySql.Data's "Parameter '@steam_id' must be defined"
        [Fact]
        public async Task RemovePlayerBanRemovesTheRow()
        {
            var db = await StartAsync(Player(12345, "Test Player"), Player(12346, "Another Player"));
            var blacklist = db.Blacklist();

            Assert.True(await blacklist.RemovePlayerBan(12345));
            Assert.Null(await blacklist.GetBannedPlayer(12345));
            Assert.Equal(1, await RowsAsync());

            Assert.False(await blacklist.RemovePlayerBan(12345));
        }

        // The biggest account number fits the column's ten digits, and the code's uint
        [Fact]
        public async Task BiggestAccountNumberSurvivesEveryCall()
        {
            var db = await StartAsync();
            var blacklist = db.Blacklist();
            var player = Player(uint.MaxValue, "Test Player", expires: new DateTime(2027, 1, 2, 3, 4, 5));

            await blacklist.InsertPlayerBan(player);

            Assert.Equivalent(player, await blacklist.GetBannedPlayer(uint.MaxValue), strict: true);
            Assert.Equivalent(player, Assert.Single(await blacklist.GetBlacklist()), strict: true);

            player.username = "Renamed Player";
            player.expire_date = null;
            Assert.True(await blacklist.UpdatePlayerBan(player));
            Assert.Equivalent(player, await blacklist.GetBannedPlayer(uint.MaxValue), strict: true);

            Assert.True(await blacklist.RemovePlayerBan(uint.MaxValue));
            Assert.Equal(0, await RowsAsync());
        }

        [Fact]
        public async Task UpdateOfAMissingPlayerChangesNothing()
        {
            var db = await StartAsync();

            Assert.False(await db.Blacklist().UpdatePlayerBan(Player(12345, "Test Player")));
            Assert.Equal(0, await RowsAsync());
        }

        [Fact]
        public async Task GetBlacklistReturnsEveryRowWithEveryColumn()
        {
            var players = new[]
            {
                Player(1, "First Player", "Cheating", new DateTime(2027, 1, 1, 0, 0, 0), "First note"),
                Player(12345, null, null, null, null),
                Player(4294967295, "Last Player", "Griefing", null, "Last note")
            };

            var db = await StartAsync(players);

            var listed = (await db.Blacklist().GetBlacklist()).OrderBy(player => player.steam_id).ToList();

            Assert.Equal(players.Length, listed.Count);
            for (var i = 0; i < players.Length; i++)
                Assert.Equivalent(players[i], listed[i], strict: true);
        }

        [Fact]
        public async Task SearchFindsAnAccountNumberOrAPartOfAName()
        {
            var db = await StartAsync(
                Player(12345, "Test Player"),
                Player(12346, "100% Cheater"),
                Player(12347, "1000 Cheater"),
                Player(12348, "a_b"),
                Player(12349, "axb"),
                Player(12350, null));
            var blacklist = db.Blacklist();

            Assert.Equal([12345u], Accounts(await blacklist.SearchBlacklist(12345, null, 0, 25)));
            Assert.Empty((await blacklist.SearchBlacklist(99999, null, 0, 25)).Rows);
            Assert.Equal([12346u], Accounts(await blacklist.SearchBlacklist(null, "100%", 0, 25)));
            Assert.Equal([12348u], Accounts(await blacklist.SearchBlacklist(null, "A_B", 0, 25)));
            Assert.Equal([12345u], Accounts(await blacklist.SearchBlacklist(null, " test ", 0, 25)));

            // Either one matching is enough, so the same text can be tried as both
            Assert.Equal([12347u, 12345u], Accounts(await blacklist.SearchBlacklist(12345, "1000", 0, 25)));
        }

        // Ordered by name and then account number, with a name left empty first
        [Fact]
        public async Task SearchPagesWithTheTotal()
        {
            var db = await StartAsync(
                Player(5, "Bravo"),
                Player(4, "Alpha"),
                Player(3, "Alpha"),
                Player(2, null),
                Player(1, "Charlie"));
            var blacklist = db.Blacklist();

            var first = await blacklist.SearchBlacklist(null, null, 0, 2);
            var second = await blacklist.SearchBlacklist(null, null, 1, 2);
            var last = await blacklist.SearchBlacklist(null, null, 2, 2);

            Assert.Equal([2u, 3u], Accounts(first));
            Assert.Equal([4u, 5u], Accounts(second));
            Assert.Equal([1u], Accounts(last));
            Assert.All(new[] { first, second, last }, page => Assert.Equal(5, page.Total));

            var alpha = await blacklist.SearchBlacklist(null, "alpha", 0, 1);
            Assert.Equal([3u], Accounts(alpha));
            Assert.Equal(2, alpha.Total);
        }

        // A blacklist change and its audit row can commit together, like an armor's
        [Fact]
        public async Task ChangesCanShareATransaction()
        {
            var db = await StartAsync(Player(12345, "Test Player"));
            var blacklist = db.Blacklist();

            var removed = await blacklist.RunInTransaction(async transaction =>
            {
                var gone = await blacklist.RemovePlayerBan(12345, transaction);
                await blacklist.InsertPlayerBan(Player(12346, "New Player"), transaction);
                return gone && await blacklist.UpdatePlayerBan(Player(12346, "Renamed Player"), transaction);
            });

            Assert.True(removed);
            Assert.Null(await blacklist.GetBannedPlayer(12345));
            Assert.Equal("Renamed Player", (await blacklist.GetBannedPlayer(12346))?.username);
        }

        // Fails if a write ignores the transaction it's given, because then it commits on a connection of its own and the
        // change stays when its audit row can't be written
        [Fact]
        public async Task RollbackUndoesEveryWriteInTheTransaction()
        {
            var db = await StartAsync(Player(12345, "Test Player"), Player(12347, "Third Player"));
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username) VALUES (5000000000, 'Test Typo')");
            var blacklist = db.Blacklist();

            await Assert.ThrowsAsync<InvalidOperationException>(() => blacklist.RunInTransaction<bool>(async transaction =>
            {
                Assert.True(await blacklist.RemovePlayerBan(12345, transaction));
                await blacklist.InsertPlayerBan(Player(12346, "New Player"), transaction);
                Assert.True(await blacklist.UpdatePlayerBan(Player(12347, "Renamed Player"), transaction));
                Assert.True(await blacklist.RemoveOutOfRangeSteamId(5000000000, transaction));
                throw new InvalidOperationException("The audit row couldn't be written");
            }));

            Assert.Equal([12345u, 12347u], (await blacklist.GetBlacklist()).Select(player => player.steam_id).Order());
            Assert.Equal("Third Player", (await blacklist.GetBannedPlayer(12347))?.username);
            Assert.Equal([5000000000m], await blacklist.ListOutOfRangeSteamIds());
        }

        // A read in a transaction sees the transaction's own changes and holds the row, so an edit can be checked against
        // what the editor saw and saved before anyone else's lands
        [Fact]
        public async Task ReadInATransactionHoldsTheRowUntilItEnds()
        {
            var db = await StartAsync(Player(12345, "Test Player"));
            var blacklist = db.Blacklist();
            var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var editing = blacklist.RunInTransaction(async transaction =>
            {
                await blacklist.InsertPlayerBan(Player(12346, "New Player"), transaction);
                Assert.NotNull(await blacklist.GetBannedPlayer(12346, transaction));

                var seen = await blacklist.GetBannedPlayer(12345, transaction);
                read.SetResult();
                await release.Task;

                seen!.reason = "Edited first";
                return await blacklist.UpdatePlayerBan(seen, transaction);
            });

            try
            {
                await read.Task.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
                var other = blacklist.UpdatePlayerBan(Player(12345, "Test Player", reason: "Edited second"));

                // The other edit waits for the row the transaction read
                Assert.Equal(1, await fixture.LockWaitsAsync("ihvph_blacklist", 1, WaitLimit));
                release.SetResult();

                var results = await Task.WhenAll(editing, other).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                Assert.Equal([true, true], results);
            }
            finally
            {
                release.TrySetResult();
            }

            Assert.Equal("Edited second", (await blacklist.GetBannedPlayer(12345))?.reason);
        }

        // The column also takes numbers no account number can be, if they're typed in somewhere else. One of them mustn't
        // break the list, and the blacklist page can still find and remove it.
        [Fact]
        public async Task OutOfRangeSteamIdsDontBreakTheList()
        {
            var db = await StartAsync(Player(12345, "Test Player"));
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username) VALUES (5000000000, 'Cut Player'), (-1, 'Negative Player'), (4294967296, 'Too Big Player')");
            var blacklist = db.Blacklist();

            Assert.Equal([12345u], (await blacklist.GetBlacklist()).Select(player => player.steam_id));

            var page = await blacklist.SearchBlacklist(null, null, 0, 25);
            Assert.Equal([12345u], Accounts(page));
            Assert.Equal(1, page.Total);
            Assert.Empty((await blacklist.SearchBlacklist(null, "Cut", 0, 25)).Rows);

            Assert.Equal([-1m, 4294967296m, 5000000000m], await blacklist.ListOutOfRangeSteamIds());

            Assert.True(await blacklist.RemoveOutOfRangeSteamId(5000000000));
            Assert.False(await blacklist.RemoveOutOfRangeSteamId(5000000000));

            // A real ban only goes through RemovePlayerBan
            Assert.False(await blacklist.RemoveOutOfRangeSteamId(12345));
            Assert.NotNull(await blacklist.GetBannedPlayer(12345));

            Assert.Equal([-1m, 4294967296m], await blacklist.ListOutOfRangeSteamIds());
        }
    }
}
