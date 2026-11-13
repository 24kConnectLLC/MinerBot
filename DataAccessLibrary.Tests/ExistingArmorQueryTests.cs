using System.Data;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Tests.Database;
using Microsoft.Extensions.Caching.Memory;
using MinerBot.Shared.Armor;

namespace DataAccessLibrary.Tests
{
    // The bot's own armor queries, unchanged, against the website's real table: its 13 colour columns, its BIGINT ID
    // and its nullable Status. Each one fails if a column is renamed or changes type, or a mapping in ArmorStyle drifts.
    [Collection("Database")]
    public class ExistingArmorQueryTests(MariaDbFixture fixture) : IDisposable
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

        // Each part a different colour, so two swapped columns show
        static ArmorSubmission Armor(long id, string? status, string name, string helmet = "Bug") => new()
        {
            ID = id,
            CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5),
            Status = status,
            Author = "Test Author",
            ArmorName = name,
            ArmorDescription = "A made-up armor",
            ArmorScreenshot = "https://murderminershub.com/wp-content/uploads/ArmorPreviews/Test.png",
            ArmorChoice = helmet,
            ArmorSkin = "Default",
            Helmet = 1, Visor = 2, Torso = 3, Shoulders = 4, Biceps = 5, Forearms = 6, Hands = 7,
            Pelvis = 8, Thighs = 9, Legs = 10, Boots = 11, Shields = 12, Other = 13,
            ColorArray = null
        };

        static byte[] Colors(ArmorStyle armor) =>
        [
            armor.Helmet, armor.Visor, armor.Torso, armor.Shoulders, armor.Biceps, armor.Forearms, armor.Hands,
            armor.Pelvis, armor.Thighs, armor.Legs, armor.Boots, armor.Shields, armor.Other
        ];

        static readonly byte[] OneToThirteen = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13];

        [Fact]
        public async Task GetAllArmorReadsOnlyApprovedRowsByName()
        {
            var db = await StartAsync(
                Armor(1, ArmorStatus.Approved, "Zeta Armor"),
                Armor(2, ArmorStatus.Approved, "Alpha Armor"),
                Armor(3, ArmorStatus.Pending, "Pending Armor"),
                Armor(4, ArmorStatus.NotApproved, "Rejected Armor"),
                Armor(5, null, "No Status Armor"));

            var armors = (await db.Armor(cache).GetAllArmor()).ToList();

            Assert.Equal(["Alpha Armor", "Zeta Armor"], armors.Select(armor => armor.ArmorName));

            var alpha = armors[0];
            Assert.Equal(2, alpha.ID);
            Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), alpha.CreatedAt);
            Assert.Equal("Test Author", alpha.Author);
            Assert.Equal("A made-up armor", alpha.ArmorDescription);
            Assert.Equal("Bug", alpha.ArmorChoice);
            Assert.Equal("Default", alpha.ArmorSkin);
            Assert.Equal(OneToThirteen, Colors(alpha));
            Assert.Null(alpha.ColorArray);
        }

        // Dapper leaves a NULL at the property's default
        [Fact]
        public async Task MissingColorsReadAsZero()
        {
            var armor = Armor(1, ArmorStatus.Approved, "Test Armor 1");
            armor.Helmet = null;
            armor.Other = null;

            var db = await StartAsync(armor);

            var read = Assert.Single(await db.Armor(cache).GetAllArmor());
            Assert.Equal(0, read.Helmet);
            Assert.Equal(0, read.Other);
            Assert.Equal(2, read.Visor);
        }

        // Why the approval rules block a colour outside a byte: one such approved armor stops the armor posts and the colour
        // job for every server, and /armor lookup of that armor. /armor list reads only names, so it goes on.
        [Theory]
        [InlineData(300)]
        [InlineData(-1)]
        public async Task ApprovedColorOutsideAByteBreaksGetAllArmor(int color)
        {
            var broken = Armor(2, ArmorStatus.Approved, "Test Armor 2");
            broken.Helmet = color;

            var db = await StartAsync(Armor(1, ArmorStatus.Approved, "Test Armor 1"), broken);

            await Assert.ThrowsAsync<DataException>(() => db.Armor(cache).GetAllArmor());
        }

        [Fact]
        public async Task NamesAreApprovedOnesByName()
        {
            var db = await StartAsync(
                Armor(1, ArmorStatus.Approved, "Zeta Armor", helmet: "Ninja"),
                Armor(2, ArmorStatus.Approved, "Alpha Armor"),
                Armor(3, ArmorStatus.Pending, "Pending Armor", helmet: "Ninja"));
            var armor = db.Armor(cache);

            Assert.Equal(["Alpha Armor", "Zeta Armor"], await armor.GetAllArmorNames());
            Assert.Equal(["Zeta Armor"], await armor.GetArmorNamesByHelmet("Ninja"));
            Assert.Empty(await armor.GetArmorNamesByHelmet("Samurai"));
        }

        // A second call within the time span doesn't query, so an armor approved in between shows only later
        [Fact]
        public async Task CachedNamesLastTheirTimeSpan()
        {
            var db = await StartAsync(Armor(1, ArmorStatus.Approved, "Test Armor 1"));
            var armor = db.Armor(cache);

            Assert.Equal(["Test Armor 1"], await armor.GetAllArmorNamesCache(TimeSpan.FromMinutes(5)));

            await db.AddArmorAsync(Armor(2, ArmorStatus.Approved, "Test Armor 2"));

            Assert.Equal(["Test Armor 1"], await armor.GetAllArmorNamesCache(TimeSpan.FromMinutes(5)));
            Assert.Equal(["Test Armor 1", "Test Armor 2"], await armor.GetAllArmorNames());
        }

        // /armor lookup finds only approved armor, and through the collation, whatever the capitals
        [Fact]
        public async Task GetArmorStyleFindsApprovedArmorIgnoringCapitals()
        {
            var db = await StartAsync(
                Armor(1, ArmorStatus.Approved, "Shadow Knight"),
                Armor(2, ArmorStatus.Pending, "Pending Armor"));
            var armor = db.Armor(cache);

            var found = await armor.GetArmorStyle("shadow KNIGHT");

            Assert.Equal(1, found.ID);
            Assert.Equal(OneToThirteen, Colors(found));
            Assert.Null(await armor.GetArmorStyle("Pending Armor"));
            Assert.Null(await armor.GetArmorStyle("Nothing Like It"));
        }

        [Fact]
        public async Task UpdateColorArraySetsOnlyThatColumn()
        {
            var db = await StartAsync(Armor(1, ArmorStatus.Approved, "Test Armor 1"));
            var armor = db.Armor(cache);
            var before = await db.Submissions().GetSubmission(1);

            await armor.UpdateColorArray(1, "AQIDBAUGBwgJCgsMDQ==");

            var after = await db.Submissions().GetSubmission(1);
            Assert.NotNull(before);
            Assert.Equal("AQIDBAUGBwgJCgsMDQ==", after?.ColorArray);

            before.ColorArray = "AQIDBAUGBwgJCgsMDQ==";
            Assert.Equivalent(before, after, strict: true);
        }

        // Nothing in the bot calls these today, but they have to fit the table's 13 colour columns
        [Fact]
        public async Task InsertUpdateAndDeleteUseEveryColorColumn()
        {
            var db = await StartAsync();
            var armor = db.Armor(cache);
            var style = new ArmorStyle
            {
                Author = "Test Author",
                ArmorName = "Test Armor 8",
                ArmorDescription = "A made-up armor",
                ArmorScreenshot = "https://murderminershub.com/wp-content/uploads/ArmorPreviews/Test-Armor-8.png",
                ArmorChoice = "Bug",
                ArmorSkin = "Default",
                Helmet = 1, Visor = 2, Torso = 3, Shoulders = 4, Biceps = 5, Forearms = 6, Hands = 7,
                Pelvis = 8, Thighs = 9, Legs = 10, Boots = 11, Shields = 12, Other = 13,
                ColorArray = "AQIDBAUGBwgJCgsMDQ=="
            };

            await armor.InsertArmor(style);

            // The website's defaults fill what the insert leaves out
            var inserted = Assert.Single((await db.Submissions().ListByStatus(null, 0, 25)).Rows);
            Assert.Null(inserted.Status);
            Assert.Null(inserted.UserID);
            Assert.True(inserted.CreatedAt > new DateTime(2026, 1, 1));
            Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13],
                new[] { inserted.Helmet, inserted.Visor, inserted.Torso, inserted.Shoulders, inserted.Biceps, inserted.Forearms, inserted.Hands,
                        inserted.Pelvis, inserted.Thighs, inserted.Legs, inserted.Boots, inserted.Shields, inserted.Other });

            style.ID = (int)inserted.ID;
            style.Helmet = 200;
            style.Other = 100;
            style.ArmorName = "Test Armor 8b";
            await armor.UpdateArmor(style);

            var updated = await db.Submissions().GetSubmission(inserted.ID);
            Assert.Equal(200, updated?.Helmet);
            Assert.Equal(100, updated?.Other);
            Assert.Equal(2, updated?.Visor);
            Assert.Equal("Test Armor 8b", updated?.ArmorName);
            Assert.Null(updated?.Status);

            await armor.DeleteArmor(style);
            Assert.Null(await db.Submissions().GetSubmission(inserted.ID));
        }
    }
}
