using DataAccessLibrary.Armor.Models;

namespace DataAccessLibrary.Armor
{
    public interface IArmorReviewData
    {
        Task AddReview(ArmorReview review, DataTransaction? transaction = null);
        Task<IReadOnlyList<ArmorReview>> ListReviews(long armorId);
    }
}
