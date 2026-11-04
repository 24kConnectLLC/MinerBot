using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Tests.Database;
using MinerBot.Shared.Armor;

namespace DataAccessLibrary.Tests
{
    [Collection("Database")]
    public class ArmorReviewDataTests(MariaDbFixture fixture)
    {
        async Task<MariaDbFixture> StartAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.ReviewTable);
            return db;
        }

        static ArmorReview Review(long armorId, string? from, string to, DateTime at, string? reason = null) => new()
        {
            armor_id = armorId,
            from_status = from,
            to_status = to,
            reason = reason,
            reviewed_by = 1000000000000000001,
            reviewer_name = "Test Admin",
            reviewed_at = at
        };

        [Fact]
        public async Task ReviewsComeBackNewestFirstForThatArmor()
        {
            var db = await StartAsync();
            var reviews = db.Reviews();
            var noon = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

            await reviews.AddReview(Review(7, ArmorStatus.Pending, ArmorStatus.Approved, noon.AddMilliseconds(123), "Looks good"));
            await reviews.AddReview(Review(7, ArmorStatus.Approved, ArmorStatus.Pending, noon.AddSeconds(1)));
            await reviews.AddReview(Review(7, ArmorStatus.Pending, ArmorStatus.NotApproved, noon.AddSeconds(1)));
            await reviews.AddReview(Review(8, ArmorStatus.Pending, ArmorStatus.Approved, noon.AddSeconds(2)));

            var listed = await reviews.ListReviews(7);

            // The two at the same moment come in the order they were written, latest first
            Assert.Equal([ArmorStatus.NotApproved, ArmorStatus.Pending, ArmorStatus.Approved], listed.Select(review => review.to_status));
            Assert.All(listed, review => Assert.Equal(7, review.armor_id));
            Assert.True(listed[0].id > listed[1].id);

            var first = listed[2];
            Assert.Equal(noon.AddMilliseconds(123), DateTime.SpecifyKind(first.reviewed_at, DateTimeKind.Utc));
            Assert.Equal("Looks good", first.reason);
            Assert.Equal(ArmorStatus.Pending, first.from_status);
            Assert.Equal(1000000000000000001ul, first.reviewed_by);
            Assert.Equal("Test Admin", first.reviewer_name);

            Assert.Empty(await reviews.ListReviews(9));
        }

        // An armor that had no status, and a review with no reason
        [Fact]
        public async Task NullsAreKept()
        {
            var db = await StartAsync();
            var reviews = db.Reviews();

            await reviews.AddReview(Review(7, null, ArmorStatus.Pending, DateTime.UtcNow));

            var review = Assert.Single(await reviews.ListReviews(7));
            Assert.Null(review.from_status);
            Assert.Null(review.reason);
        }

        [Fact]
        public async Task BigIdsAndLongTextFit()
        {
            var db = await StartAsync();
            var reviews = db.Reviews();
            var review = Review(-5, ArmorStatus.Pending, ArmorStatus.Approved, DateTime.UtcNow, new string('r', 500));
            review.reviewed_by = ulong.MaxValue;
            review.reviewer_name = new string('n', 150);

            await reviews.AddReview(review);

            var stored = Assert.Single(await reviews.ListReviews(-5));
            Assert.Equal(ulong.MaxValue, stored.reviewed_by);
            Assert.Equal(500, stored.reason!.Length);
            Assert.Equal(new string('n', 100), stored.reviewer_name);
        }
    }
}
