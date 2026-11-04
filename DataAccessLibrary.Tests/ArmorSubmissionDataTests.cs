using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.Caching.Memory;
using MinerBot.Shared.Armor;
using MySql.Data.MySqlClient;

namespace DataAccessLibrary.Tests
{
    // Reviewing armor whatever its status, against the website's real table. Made-up armor only.
    [Collection("Database")]
    public class ArmorSubmissionDataTests(MariaDbFixture fixture)
    {
        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

        async Task<MariaDbFixture> StartAsync(params ArmorSubmission[] armors)
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.ArmorTable);

            foreach (var armor in armors)
                await db.AddArmorAsync(armor);

            return db;
        }

        static ArmorSubmission Armor(long id, string? status, string name, DateTime createdAt, string author = "Test Author") => new()
        {
            ID = id,
            CreatedAt = createdAt,
            Status = status,
            Author = author,
            ArmorName = name
        };

        static DateTime Day(int day) => new(2026, 1, day, 10, 0, 0);

        async Task<string?> StatusAsync(long id) =>
            (await fixture.RootQueryAsync<string?>($"SELECT Status FROM {MariaDbFixture.ArmorTable} WHERE ID = @id", new { id })).Single();

        // Three pending, two of them sent in the same second, and one of every other status
        static ArmorSubmission[] MixedArmor() =>
        [
            Armor(101, ArmorStatus.Pending, "Test Armor 1", Day(1)),
            Armor(103, ArmorStatus.Pending, "Test Armor 3", Day(2)),
            Armor(102, ArmorStatus.Pending, "Test Armor 2", Day(2)),
            Armor(104, ArmorStatus.Approved, "Test Armor 4", Day(5)),
            Armor(105, ArmorStatus.Approved, "Test Armor 5", Day(3)),
            Armor(106, ArmorStatus.NotApproved, "Test Armor 6", Day(4)),
            Armor(107, null, "Test Armor 7", Day(6))
        ];

        static IEnumerable<long> Ids(Page<ArmorSubmission> page) => page.Rows.Select(armor => armor.ID);

        // Fails for a wrong order, a total over every status, or paging that's off by one
        [Fact]
        public async Task ListByStatusPagesOldestPendingFirst()
        {
            var db = await StartAsync(MixedArmor());
            var armor = db.Submissions();

            var first = await armor.ListByStatus(ArmorStatus.Pending, 0, 2);
            var second = await armor.ListByStatus(ArmorStatus.Pending, 1, 2);
            var past = await armor.ListByStatus(ArmorStatus.Pending, 2, 2);

            Assert.Equal([101, 102], Ids(first));
            Assert.Equal([103], Ids(second));
            Assert.Empty(past.Rows);
            Assert.All(new[] { first, second, past }, page => Assert.Equal(3, page.Total));
        }

        [Fact]
        public async Task OtherStatusesComeNewestFirst()
        {
            var db = await StartAsync(MixedArmor());
            var armor = db.Submissions();

            var approved = await armor.ListByStatus(ArmorStatus.Approved, 0, 25);
            var notApproved = await armor.ListByStatus(ArmorStatus.NotApproved, 0, 25);
            var all = await armor.ListByStatus(null, 0, 25);

            Assert.Equal([104, 105], Ids(approved));
            Assert.Equal(2, approved.Total);
            Assert.Equal([106], Ids(notApproved));

            // Every row, the one with no status included
            Assert.Equal([107, 104, 106, 105, 103, 102, 101], Ids(all));
            Assert.Equal(7, all.Total);
        }

        // Fails if a property is a byte or an int, which Dapper refuses for these values, or is missing, which Dapper
        // silently leaves at its default
        [Fact]
        public async Task EveryColumnIsRead()
        {
            var stored = new ArmorSubmission
            {
                ID = 3000000000,
                CreatedAt = new DateTime(2025, 8, 14, 21, 30, 15),
                UserID = 9000000000000000000,
                Status = ArmorStatus.Pending,
                Author = "Test Author",
                ArmorName = "Test Armor 9",
                ArmorDescription = new string('d', 256),
                ArmorScreenshot = "https://murderminershub.com/wp-content/uploads/ArmorPreviews/" + new string('s', 10000) + ".png",
                ArmorChoice = "Bug",
                ArmorSkin = "Default",
                Helmet = 0,
                Visor = 255,
                Torso = 300,
                Shoulders = -1,
                Biceps = null,
                Forearms = 5,
                Hands = 6,
                Pelvis = 7,
                Thighs = 8,
                Legs = 9,
                Boots = 10,
                Shields = 11,
                Other = 12,
                ColorArray = "AAAAAAAAAAAAAAAAAA=="
            };

            var db = await StartAsync(stored);

            Assert.Equivalent(stored, await db.Submissions().GetSubmission(stored.ID), strict: true);
        }

        [Fact]
        public async Task GetSubmissionFindsAnyStatus()
        {
            var db = await StartAsync(MixedArmor());
            var armor = db.Submissions();

            Assert.Equal(ArmorStatus.Pending, (await armor.GetSubmission(101))?.Status);
            Assert.Equal(ArmorStatus.NotApproved, (await armor.GetSubmission(106))?.Status);

            var noStatus = await armor.GetSubmission(107);
            Assert.NotNull(noStatus);
            Assert.Null(noStatus.Status);

            Assert.Null(await armor.GetSubmission(999));
        }

        [Fact]
        public async Task SearchTakesWildcardsLiterally()
        {
            var db = await StartAsync(
                Armor(201, ArmorStatus.Approved, "100% Pure", Day(1)),
                Armor(202, ArmorStatus.Approved, "1000 Pure", Day(2)),
                Armor(203, ArmorStatus.Approved, "a_b", Day(3)),
                Armor(204, ArmorStatus.Approved, "axb", Day(4)),
                Armor(205, ArmorStatus.Approved, "Wow!", Day(5)),
                Armor(206, ArmorStatus.Pending, "Plain", Day(6), author: "Test Maker"),
                Armor(207, ArmorStatus.Approved, "Pure Gold", Day(7)));
            var armor = db.Submissions();

            Assert.Equal([201], Ids(await armor.Search("100%", null, 0, 25)));
            Assert.Equal([201], Ids(await armor.Search("  100%  ", null, 0, 25)));
            Assert.Equal([203], Ids(await armor.Search("a_b", null, 0, 25)));
            Assert.Equal([205], Ids(await armor.Search("!", null, 0, 25)));
            Assert.Equal([206], Ids(await armor.Search("test MAKER", null, 0, 25)));
            Assert.Equal([206], Ids(await armor.Search("plain", ArmorStatus.Pending, 0, 25)));
            Assert.Empty((await armor.Search("pure", ArmorStatus.Pending, 0, 25)).Rows);
            Assert.Equal([201], Ids(await armor.Search("%", null, 0, 25)));

            var pure = await armor.Search("PURE", ArmorStatus.Approved, 0, 2);
            Assert.Equal([207, 202], Ids(pure));
            Assert.Equal(3, pure.Total);
            Assert.Equal([201], Ids(await armor.Search("PURE", ArmorStatus.Approved, 1, 2)));

            // No text is the same as the list
            Assert.Equal(Ids(await armor.ListByStatus(ArmorStatus.Approved, 0, 25)), Ids(await armor.Search(" ", ArmorStatus.Approved, 0, 25)));
        }

        [Fact]
        public async Task ChangeStatusOnlyFromTheExpectedStatus()
        {
            var db = await StartAsync(MixedArmor());
            var armor = db.Submissions();

            Assert.True(await armor.ChangeStatus(101, ArmorStatus.Pending, ArmorStatus.Approved));
            Assert.Equal(ArmorStatus.Approved, await StatusAsync(101));

            // Someone else already changed it
            Assert.False(await armor.ChangeStatus(101, ArmorStatus.Pending, ArmorStatus.NotApproved));
            Assert.Equal(ArmorStatus.Approved, await StatusAsync(101));

            // An armor with no status
            Assert.False(await armor.ChangeStatus(107, ArmorStatus.Pending, ArmorStatus.Approved));
            Assert.True(await armor.ChangeStatus(107, null, ArmorStatus.Pending));
            Assert.Equal(ArmorStatus.Pending, await StatusAsync(107));

            Assert.False(await armor.ChangeStatus(999, ArmorStatus.Pending, ArmorStatus.Approved));

            // Back to pending, the undo
            Assert.True(await armor.ChangeStatus(101, ArmorStatus.Approved, ArmorStatus.Pending));
            Assert.Equal(ArmorStatus.Pending, await StatusAsync(101));
        }

        // Fails for a version that reads the status and then writes it, which would let both win
        [Fact]
        public async Task TwoStatusChangesRace()
        {
            var db = await StartAsync(Armor(301, ArmorStatus.Pending, "Test Armor 31", Day(1)));
            var armor = db.Submissions();

            await using var holder = new MySqlConnection(db.RootConnection);
            await holder.OpenAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await RawAsync(holder, "START TRANSACTION");
            await RawAsync(holder, $"SELECT ID FROM {MariaDbFixture.ArmorTable} WHERE ID = 301 FOR UPDATE");

            var approve = armor.ChangeStatus(301, ArmorStatus.Pending, ArmorStatus.Approved);
            var reject = armor.ChangeStatus(301, ArmorStatus.Pending, ArmorStatus.NotApproved);

            // Both are waiting for the row before it's let go
            Assert.Equal(2, await fixture.LockWaitsAsync("ihvph_armor_styles", 2, WaitLimit));
            await RawAsync(holder, "COMMIT");

            var results = await Task.WhenAll(approve, reject).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Single(results, applied => applied);
            Assert.Equal(results[0] ? ArmorStatus.Approved : ArmorStatus.NotApproved, await StatusAsync(301));
        }

        static async Task RawAsync(MySqlConnection connection, string sql)
        {
            await using var command = new MySqlCommand(sql, connection);
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task ApprovedNameIsTakenIgnoringCapitalsAndSpaces()
        {
            var db = await StartAsync(
                Armor(401, ArmorStatus.Approved, "Shadow Knight", Day(1)),
                Armor(402, ArmorStatus.Pending, "Pending Name", Day(2)),
                Armor(403, ArmorStatus.NotApproved, "Rejected Name", Day(3)));
            var armor = db.Submissions();

            Assert.True(await armor.IsApprovedNameTaken("  shadow knight ", 0));
            Assert.True(await armor.IsApprovedNameTaken("SHADOW KNIGHT", 999));

            // Its own name doesn't count
            Assert.False(await armor.IsApprovedNameTaken("Shadow Knight", 401));

            // Only approved armor holds a name
            Assert.False(await armor.IsApprovedNameTaken("Pending Name", 0));
            Assert.False(await armor.IsApprovedNameTaken("Rejected Name", 0));

            Assert.False(await armor.IsApprovedNameTaken("Shadow Knights", 0));
            Assert.False(await armor.IsApprovedNameTaken("Shadow%", 0));
            Assert.False(await armor.IsApprovedNameTaken("Shadow_Knight", 0));
        }

        // The column's collation already ignores spaces at the end, but not in front, so this needs the TRIM on the stored name
        [Fact]
        public async Task StoredNameWithASpaceInFrontIsTaken()
        {
            var db = await StartAsync(Armor(404, ArmorStatus.Approved, " Shadow Knight", Day(1)));
            var armor = db.Submissions();

            Assert.True(await armor.IsApprovedNameTaken("Shadow Knight", 0));
            Assert.True(await armor.IsApprovedNameTaken("shadow knight ", 0));
        }

        // Fails if the check in a transaction doesn't lock, or the transaction lets go of rows it passed, either of which
        // would let two approvals of the same name both pass
        [Fact]
        public async Task TwoApprovalsOfTheSameNameTakeTurns()
        {
            var db = await StartAsync(Armor(601, ArmorStatus.Pending, "Frost", Day(1)), Armor(602, ArmorStatus.Pending, "frost ", Day(2)));
            var armor = db.Submissions();
            var firstChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // As the review page will: the check comes first in the transaction, then the change
            Task<bool> Approve(long id, string name, Func<Task> afterCheck) => armor.RunInTransaction(async transaction =>
            {
                if (await armor.IsApprovedNameTaken(name, id, transaction))
                    return false;

                await afterCheck();
                return await armor.ChangeStatus(id, ArmorStatus.Pending, ArmorStatus.Approved, transaction);
            });

            var first = Approve(601, "Frost", async () =>
            {
                firstChecked.SetResult();
                await release.Task;
            });

            try
            {
                await firstChecked.Task.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
                var second = Approve(602, "frost ", () => Task.CompletedTask);

                // The second approval's check waits for the first one's locks
                Assert.Equal(1, await fixture.LockWaitsAsync("ihvph_armor_styles", 1, WaitLimit));
                release.SetResult();

                var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                Assert.Equal([true, false], results);
            }
            finally
            {
                release.TrySetResult();
            }

            Assert.Equal(ArmorStatus.Approved, await StatusAsync(601));
            Assert.Equal(ArmorStatus.Pending, await StatusAsync(602));
        }

        // In a transaction the check runs on its connection, so it sees the transaction's own approval
        [Fact]
        public async Task NameCheckInATransactionSeesItsOwnChange()
        {
            var db = await StartAsync(Armor(701, ArmorStatus.Pending, "Test Armor 71", Day(1)));
            var armor = db.Submissions();

            var taken = await armor.RunInTransaction(async transaction =>
            {
                Assert.False(await armor.IsApprovedNameTaken("Test Armor 71", 0, transaction));
                Assert.True(await armor.ChangeStatus(701, ArmorStatus.Pending, ArmorStatus.Approved, transaction));
                return await armor.IsApprovedNameTaken("Test Armor 71", 0, transaction);
            });

            Assert.True(taken);
        }

        // The website's column has no limit, so a list cuts the screenshot one character past what the rules look at, and
        // they still refuse it. The review page's own read has all of it.
        [Fact]
        public async Task ListsCutAHugeScreenshot()
        {
            var huge = Armor(801, ArmorStatus.Pending, "Test Armor 81", Day(1));
            huge.ArmorScreenshot = "https://murderminershub.com/wp-content/uploads/" + new string('s', 100000) + ".png";
            var plain = Armor(802, ArmorStatus.Pending, "Test Armor 82", Day(2));
            plain.ArmorScreenshot = "https://murderminershub.com/wp-content/uploads/ArmorPreviews/Test-Armor-82.png";

            var db = await StartAsync(huge, plain);
            var armor = db.Submissions();
            var cut = huge.ArmorScreenshot[..(ArmorRules.ScreenshotLimit + 1)];

            var listed = (await armor.ListByStatus(ArmorStatus.Pending, 0, 25)).Rows;
            Assert.Equal([cut, plain.ArmorScreenshot], listed.Select(row => row.ArmorScreenshot));
            Assert.Equal(cut, (await armor.Search("Test Armor 81", null, 0, 25)).Rows.Single().ArmorScreenshot);
            Assert.Contains(ArmorProblemKind.ScreenshotNotAllowed, ArmorRules.Check(listed[0], nameTaken: false).Warnings.Select(problem => problem.Kind));

            Assert.Equal(huge.ArmorScreenshot, (await armor.GetSubmission(801))?.ArmorScreenshot);
        }

        // The column's collation decides, the same way /armor lookup finds a name, so these can't be told apart there either
        [Fact]
        public async Task NamesLookupCantTellApartAreTaken()
        {
            var db = await StartAsync(Armor(401, ArmorStatus.Approved, "Shadow Knight", Day(1)));
            var armor = db.Submissions();

            Assert.True(await armor.IsApprovedNameTaken("Sh\u00E1dow Knight", 0));
            Assert.True(await armor.IsApprovedNameTaken("Shadow\u200B Knight", 0));

            using var cache = new MemoryCache(new MemoryCacheOptions());
            Assert.NotNull(await db.Armor(cache).GetArmorStyle("Sh\u00E1dow Knight"));
        }
    }
}
