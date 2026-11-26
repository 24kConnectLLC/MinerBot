using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.Caching.Memory;
using MinerBot.Shared.Armor;

namespace DataAccessLibrary.Tests
{
    // The bot's reads for its new-submission alerts, against the website's real armor table: the armor waiting for a review,
    // by ID and time only, and the highest armor ID of any status, the alerts' floor at their first look
    [Collection("Database")]
    public class ArmorDataTests(MariaDbFixture fixture) : IDisposable
    {
        readonly MemoryCache cache = new(new MemoryCacheOptions());

        public void Dispose() => cache.Dispose();

        async Task<MariaDbFixture> StartAsync(params ArmorSubmission[] armors)
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.ArmorTable);

            foreach (var armor in armors)
                await db.AddArmorAsync(armor);

            return db;
        }

        static ArmorSubmission Armor(long id, string? status, DateTime createdAt) => new()
        {
            ID = id,
            CreatedAt = createdAt,
            Status = status,
            Author = "Test Author",
            ArmorName = $"Test Armor {id}",
            ArmorDescription = "A made-up armor",
            ArmorScreenshot = "https://murderminershub.com/wp-content/uploads/ArmorPreviews/Test.png",
            ArmorChoice = "Bug",
            ArmorSkin = "Default"
        };

        static DateTime Day(int day) => new(2024, 8, day, 10, 11, 12);

        // Every pending armor, by ID, with when the website saved it, whichever order they came in: the table's collation
        // reads a status with other capitals or a space at the end as the same word, as the bot's other reads do. Nothing a
        // submitter wrote is read, so the model holds only the two.
        [Fact]
        public async Task PendingSubmissionsAreIdAndTimeOnly()
        {
            var db = await StartAsync(
                Armor(30, ArmorStatus.Pending, Day(3)),
                Armor(10, ArmorStatus.Approved, Day(1)),
                Armor(25, ArmorStatus.Pending, Day(5)),
                Armor(20, ArmorStatus.NotApproved, Day(1)),
                Armor(40, null, Day(1)),
                Armor(35, "pending approval ", Day(4)));

            var pending = await db.Armor(cache).GetPendingSubmissions();

            Assert.Equal([(25L, Day(5)), (30L, Day(3)), (35L, Day(4))], pending.Select(armor => (armor.ID, armor.CreatedAt)));
            Assert.Equal(["ID", "CreatedAt"], typeof(PendingSubmission).GetProperties().Select(property => property.Name));
        }

        // The highest ID whatever its status, past an int too, and 0 for an empty table
        [Fact]
        public async Task HighestIdCountsEveryStatus()
        {
            var db = await StartAsync();
            var armor = db.Armor(cache);

            Assert.Equal(0, await armor.GetHighestArmorId());

            await db.AddArmorAsync(Armor(5, ArmorStatus.Approved, Day(1)));
            await db.AddArmorAsync(Armor(9, ArmorStatus.Pending, Day(2)));
            Assert.Equal(9, await armor.GetHighestArmorId());

            await db.AddArmorAsync(Armor(3000000000, ArmorStatus.NotApproved, Day(3)));
            await db.AddArmorAsync(Armor(7, null, Day(4)));
            Assert.Equal(3000000000, await armor.GetHighestArmorId());
        }
    }
}
