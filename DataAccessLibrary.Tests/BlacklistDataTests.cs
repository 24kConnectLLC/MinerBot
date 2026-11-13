using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Blacklist;
using DataAccessLibrary.Blacklist.Models;
using DataAccessLibrary.Tests.Database;

namespace DataAccessLibrary.Tests
{
    // The website's blacklist holds 32-bit Steam account numbers in a DECIMAL(10,0). Made-up players only.
    [Collection("Database")]
    public class BlacklistDataTests(MariaDbFixture fixture)
    {
        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

        const string Emoji = "\U0001F600";

        async Task<MariaDbFixture> StartAsync(params BlacklistModel[] players)
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.BlacklistTable, MariaDbFixture.AuditTable);

            foreach (var player in players)
                await db.Blacklist().InsertPlayerBan(player);

            return db;
        }

        static BlacklistModel Player(uint accountNumber, string? username, string? reason = "Testing", DateTime? expires = null, string? notes = "Made up") => new()
        {
            steam_id = accountNumber,
            username = username,
            reason = reason,
            expire_date = expires,
            notes = notes
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

        // The columns hold 60, 60 and 120 characters, which MariaDB counts as code points, so an emoji is one
        [Fact]
        public async Task TextAtTheColumnsLimitsFits()
        {
            var db = await StartAsync();
            var blacklist = db.Blacklist();
            var player = Player(12345, new string('n', BlacklistData.UsernameLimit), new string('r', BlacklistData.ReasonLimit),
                notes: new string('o', BlacklistData.NotesLimit));

            await blacklist.InsertPlayerBan(player);
            Assert.Equivalent(player, await blacklist.GetBannedPlayer(12345), strict: true);

            player.username = string.Concat(Enumerable.Repeat(Emoji, BlacklistData.UsernameLimit));
            player.reason = string.Concat(Enumerable.Repeat(Emoji, BlacklistData.ReasonLimit));
            player.notes = string.Concat(Enumerable.Repeat(Emoji, BlacklistData.NotesLimit));
            Assert.True(await blacklist.UpdatePlayerBan(player));
            Assert.Equivalent(player, await blacklist.GetBannedPlayer(12345), strict: true);
        }

        // MariaDB's default mode stores dates no DateTime can hold, which anything else that writes the table can send.
        // MySql.Data throws reading one, so without the guard each of these reads fails, and the whole list with it.
        [Fact]
        public async Task ZeroDatesBreakNoRead()
        {
            var db = await StartAsync(Player(10, "Good Date", expires: new DateTime(2027, 5, 5, 1, 2, 3)), Player(11, "No Date"));
            await db.RootExecuteAsync($@"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username, expire_date) VALUES
                (1, 'Zero Date', '0000-00-00 00:00:00'), (2, 'Zero Month', '2027-00-10 00:00:00'), (3, 'Zero Day', '2027-05-00 01:02:03'),
                (4, 'Zero Year', '0000-01-01 00:00:00'), (5000000000, 'Zero Number', '0000-00-00 00:00:00')");

            // A session that allows invalid dates stores a day past the month's end, as some website settings do
            await db.RootExecuteAsync($"SET STATEMENT sql_mode = 'ALLOW_INVALID_DATES' FOR INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username, expire_date) VALUES (5, 'Past The End', '2027-02-30 00:00:00')");

            var blacklist = db.Blacklist();
            uint[] unreadable = [1, 2, 3, 4, 5];

            void Readable(BlacklistModel? player, DateTime? expires)
            {
                Assert.NotNull(player);
                Assert.False(player.expire_date_unreadable);
                Assert.Equal(expires, player.expire_date);
            }

            void Unreadable(BlacklistModel? player)
            {
                Assert.NotNull(player);
                Assert.True(player.expire_date_unreadable, $"{player.steam_id}'s date reads as {player.expire_date}");
                Assert.Null(player.expire_date);
            }

            var listed = (await blacklist.GetBlacklist()).ToDictionary(player => player.steam_id);
            Assert.Equal([1u, 2u, 3u, 4u, 5u, 10u, 11u], listed.Keys.Order());
            Assert.All(unreadable, account => Unreadable(listed[account]));
            Readable(listed[10], new DateTime(2027, 5, 5, 1, 2, 3));
            Readable(listed[11], null);

            var page = await blacklist.SearchBlacklist(null, null, 0, 25);
            Assert.Equal(7, page.Total);
            Assert.All(page.Rows.Where(player => unreadable.Contains(player.steam_id)), Unreadable);
            Unreadable(Assert.Single((await blacklist.SearchBlacklist(3, null, 0, 25)).Rows));

            foreach (var account in unreadable)
                Unreadable(await blacklist.GetBannedPlayer(account));

            Readable(await blacklist.GetBannedPlayer(10), new DateTime(2027, 5, 5, 1, 2, 3));

            // The locked reads, inside a transaction
            Assert.True(await blacklist.RunInTransaction(async transaction =>
            {
                Unreadable(await blacklist.GetBannedPlayer(1, transaction));
                var number = await blacklist.GetOutOfRangeBan(5000000000, transaction);
                Assert.True(number?.expire_date_unreadable);
                Assert.Null(number?.expire_date);
                return true;
            }));

            var row = Assert.Single(await blacklist.ListOutOfRangeBans());
            Assert.True(row.expire_date_unreadable);
            Assert.Null(row.expire_date);
            Assert.True((await blacklist.GetOutOfRangeBan(5000000000))?.expire_date_unreadable);
        }

        // The rows no list shows, whole, for the page that offers to remove them: smallest number first, and at most 100
        [Fact]
        public async Task OutOfRangeRowsAreReadWhole()
        {
            var db = await StartAsync(Player(12345, "Test Player"));
            await db.RootExecuteAsync($@"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username, reason, expire_date, notes) VALUES
                (5000000000, 'Cut Player', 'Typo', '2027-01-02 03:04:05', 'Typed in elsewhere'), (-1, NULL, NULL, NULL, NULL)");

            var blacklist = db.Blacklist();
            var rows = await blacklist.ListOutOfRangeBans();

            Assert.Equal([-1m, 5000000000m], rows.Select(row => row.steam_id));
            Assert.Equivalent(new OutOfRangeBan
            {
                steam_id = 5000000000,
                username = "Cut Player",
                reason = "Typo",
                expire_date = new DateTime(2027, 1, 2, 3, 4, 5),
                notes = "Typed in elsewhere"
            }, rows[1], strict: true);
            Assert.Equivalent(new OutOfRangeBan { steam_id = -1 }, rows[0], strict: true);

            Assert.Equal("Cut Player", (await blacklist.GetOutOfRangeBan(5000000000))?.username);

            // A real ban is never one, and a number that matches nothing is no row, without an error
            Assert.Null(await blacklist.GetOutOfRangeBan(12345));
            Assert.Null(await blacklist.GetOutOfRangeBan(4294967296.5m));
            Assert.Null(await blacklist.GetOutOfRangeBan(79228162514264337593543950335m));

            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id) SELECT 6000000000 + seq FROM {MariaDbFixture.SiteDatabase}.seq_1_to_{BlacklistData.OutOfRangeLimit}");
            var capped = await blacklist.ListOutOfRangeBans();

            Assert.Equal(BlacklistData.OutOfRangeLimit, capped.Count);
            Assert.Equal([-1m, 5000000000m, 6000000001m], capped.Take(3).Select(row => row.steam_id));
        }

        // A removal checks the row is as the page showed it, then removes it, so the locked read holds it until then
        [Fact]
        public async Task LockedReadOfAnOutOfRangeRowHoldsIt()
        {
            var db = await StartAsync();
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username) VALUES (5000000000, 'Cut Player')");
            var blacklist = db.Blacklist();
            var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var removing = blacklist.RunInTransaction(async transaction =>
            {
                Assert.NotNull(await blacklist.GetOutOfRangeBan(5000000000, transaction));
                read.SetResult();
                await release.Task;
                return await blacklist.RemoveOutOfRangeSteamId(5000000000, transaction);
            });

            try
            {
                await read.Task.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
                var other = db.RootExecuteAsync($"UPDATE {MariaDbFixture.BlacklistTable} SET username = 'Renamed' WHERE steam_id = 5000000000");

                Assert.Equal(1, await fixture.LockWaitsAsync("ihvph_blacklist", 1, WaitLimit));
                release.SetResult();

                Assert.True(await removing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
                await other.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }
            finally
            {
                release.TrySetResult();
            }

            Assert.Empty(await blacklist.ListOutOfRangeBans());
        }

        static AuditEntry Audit(string action, string target, DateTime? at = null) => new()
        {
            at = at ?? DateTime.UtcNow,
            actor_id = 1000000000000000001,
            actor_name = "Test Admin",
            actor_role = AuditRole.Admin,
            action = action,
            outcome = AuditOutcome.Ok,
            target_type = "blacklist",
            target_id = target
        };

        async Task<List<string>> AuditActionsAsync() => await fixture.RootQueryAsync<string>($"SELECT action FROM {MariaDbFixture.AuditTable} ORDER BY id");

        // Each change the blacklist's pages make, with its audit row in MinerBot's database, in one transaction on the
        // website's connection: both land
        [Fact]
        public async Task EachChangeCommitsWithItsAuditRow()
        {
            var db = await StartAsync();
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username) VALUES (5000000000, 'Cut Player')");
            var blacklist = db.Blacklist();
            var audit = db.Audit();

            await blacklist.RunInTransaction(async transaction =>
            {
                Assert.Null(await blacklist.GetBannedPlayer(12345, transaction));
                await blacklist.InsertPlayerBan(Player(12345, "Test Player"), transaction);
                await audit.AddAudit(Audit("blacklist.add", "12345"), transaction);
                return true;
            });

            Assert.Equal("Test Player", (await blacklist.GetBannedPlayer(12345))?.username);
            Assert.Equal(["blacklist.add"], await AuditActionsAsync());

            await blacklist.RunInTransaction(async transaction =>
            {
                Assert.NotNull(await blacklist.GetBannedPlayer(12345, transaction));
                Assert.True(await blacklist.UpdatePlayerBan(Player(12345, "Renamed Player"), transaction));
                await audit.AddAudit(Audit("blacklist.edit", "12345"), transaction);
                return true;
            });

            Assert.Equal("Renamed Player", (await blacklist.GetBannedPlayer(12345))?.username);
            Assert.Equal(["blacklist.add", "blacklist.edit"], await AuditActionsAsync());

            await blacklist.RunInTransaction(async transaction =>
            {
                Assert.NotNull(await blacklist.GetBannedPlayer(12345, transaction));
                Assert.True(await blacklist.RemovePlayerBan(12345, transaction));
                await audit.AddAudit(Audit("blacklist.remove", "12345"), transaction);
                return true;
            });

            Assert.Null(await blacklist.GetBannedPlayer(12345));

            await blacklist.RunInTransaction(async transaction =>
            {
                Assert.NotNull(await blacklist.GetOutOfRangeBan(5000000000, transaction));
                Assert.True(await blacklist.RemoveOutOfRangeSteamId(5000000000, transaction));
                await audit.AddAudit(Audit("blacklist.remove", "5000000000"), transaction);
                return true;
            });

            Assert.Empty(await blacklist.ListOutOfRangeBans());
            Assert.Equal(["blacklist.add", "blacklist.edit", "blacklist.remove", "blacklist.remove"], await AuditActionsAsync());
        }

        // An audit row that can't be saved, here one in local time, which AddAudit refuses before any SQL, stops the change
        // it travels with. Fails if a blacklist write ignores the transaction it's given, and commits on its own.
        [Theory]
        [InlineData("add")]
        [InlineData("edit")]
        [InlineData("remove")]
        [InlineData("number")]
        public async Task RefusedAuditRowUndoesTheChange(string change)
        {
            var db = await StartAsync(Player(12345, "Test Player"));
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.BlacklistTable} (steam_id, username) VALUES (5000000000, 'Cut Player')");
            var blacklist = db.Blacklist();

            await Assert.ThrowsAsync<ArgumentException>(() => blacklist.RunInTransaction<bool>(async transaction =>
            {
                switch (change)
                {
                    case "add": await blacklist.InsertPlayerBan(Player(12346, "New Player"), transaction); break;
                    case "edit": Assert.True(await blacklist.UpdatePlayerBan(Player(12345, "Renamed Player"), transaction)); break;
                    case "remove": Assert.True(await blacklist.RemovePlayerBan(12345, transaction)); break;
                    default: Assert.True(await blacklist.RemoveOutOfRangeSteamId(5000000000, transaction)); break;
                }

                await db.Audit().AddAudit(Audit("blacklist." + change, "12345", DateTime.Now), transaction);
                return true;
            }));

            Assert.Equal([12345u], (await blacklist.GetBlacklist()).Select(player => player.steam_id));
            Assert.Equal("Test Player", (await blacklist.GetBannedPlayer(12345))?.username);
            Assert.Equal([5000000000m], await blacklist.ListOutOfRangeSteamIds());
            Assert.Empty(await AuditActionsAsync());
        }

        // Two adds of one new account at the same moment, as the add page makes them: each locked read finds no row and
        // locks the gap, and their inserts deadlock. The one that loses runs again, finds the other's row, and is refused,
        // with a refused audit row of its own.
        [Fact]
        public async Task TwoAddsOfOneAccountSaveOneAndRefuseTheOther()
        {
            var db = await StartAsync(Player(10, "Test Player"), Player(900000, "Another Player"));
            var blacklist = db.Blacklist();
            var audit = db.Audit();
            var firstChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runs = 0;

            Task<bool> Add(string name, TaskCompletionSource done, TaskCompletionSource other) => blacklist.RunInTransaction(async transaction =>
            {
                Interlocked.Increment(ref runs);

                if (await blacklist.GetBannedPlayer(1000, transaction) != null)
                {
                    var refused = Audit("blacklist.add", "1000");
                    refused.outcome = AuditOutcome.Refused;
                    await audit.AddAudit(refused, transaction);
                    return false;
                }

                // Both check before either adds, the order in which they deadlock
                done.TrySetResult();
                await other.Task.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

                await blacklist.InsertPlayerBan(Player(1000, name), transaction);
                await audit.AddAudit(Audit("blacklist.add", "1000"), transaction);
                return true;
            });

            var results = await Task.WhenAll(Add("First Player", firstChecked, secondChecked), Add("Second Player", secondChecked, firstChecked))
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal([false, true], results.Order());
            Assert.True(runs >= 3, $"The work ran {runs} times, so neither add lost a deadlock and ran again");
            Assert.Equal(3, await RowsAsync());
            Assert.Equal([AuditOutcome.Ok, AuditOutcome.Refused],
                (await fixture.RootQueryAsync<string>($"SELECT outcome FROM {MariaDbFixture.AuditTable} ORDER BY id")).Order(StringComparer.Ordinal));
        }
    }
}
