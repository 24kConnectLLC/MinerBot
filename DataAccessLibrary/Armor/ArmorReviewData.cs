using DataAccessLibrary.Armor.Models;

namespace DataAccessLibrary.Armor
{
    // Who changed an armor's status, when and why. The table is in MinerBot's own database and its SQL names it there,
    // so a review can also be written inside a transaction on the website's connection, together with the change.
    public class ArmorReviewData : DbBaseConnection, IArmorReviewData
    {
        const int ReasonLimit = 500;
        const int ReviewerNameLimit = 100;

        private readonly DatabaseNames _names;

        public ArmorReviewData(ISQLConnectionFactory connectionFactory, DatabaseNames names) : base(connectionFactory, DBConnectionName.Bot)
        {
            _names = names;
        }

        // A reason that's too long is refused rather than cut, because it's what the reviewer wrote. The name is only
        // for display, so it's cut.
        public Task AddReview(ArmorReview review, DataTransaction? transaction = null)
        {
            ArmorSubmissionData.RequireStatus(review.to_status, nameof(review.to_status), allowNone: false);
            ArmorSubmissionData.RequireStatus(review.from_status, nameof(review.from_status), allowNone: true);
            RequireUtc(review.reviewed_at, nameof(review.reviewed_at));

            if (review.reason != null && CodePoints(review.reason) > ReasonLimit)
                throw new ArgumentException($"A reason holds at most {ReasonLimit} characters", nameof(review));

            string sql = $@"INSERT INTO {_names.Bot}.armor_reviews (armor_id, from_status, to_status, reason, reviewed_by, reviewer_name, reviewed_at)
                            VALUES (@armor_id, @from_status, @to_status, @reason, @reviewed_by, @reviewer_name, @reviewed_at)";

            return SaveDataCount(sql, new
            {
                review.armor_id,
                review.from_status,
                review.to_status,
                review.reason,
                review.reviewed_by,
                reviewer_name = Clip(review.reviewer_name, ReviewerNameLimit),
                review.reviewed_at
            }, transaction);
        }

        // Newest first, with the order they were written breaking ties
        public async Task<IReadOnlyList<ArmorReview>> ListReviews(long armorId)
        {
            string sql = $@"SELECT id, armor_id, from_status, to_status, reason, reviewed_by, reviewer_name, reviewed_at
                            FROM {_names.Bot}.armor_reviews
                            WHERE armor_id = @armorId
                            ORDER BY reviewed_at DESC, id DESC";

            return (await LoadDataEnum<ArmorReview, dynamic>(sql, new { armorId }).ConfigureAwait(false)).ToList();
        }
    }
}
