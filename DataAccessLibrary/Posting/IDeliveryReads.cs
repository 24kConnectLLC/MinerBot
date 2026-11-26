using DataAccessLibrary.Posting.Models;

namespace DataAccessLibrary.Posting
{
    public interface IDeliveryReads
    {
        Task<IReadOnlyList<DeliveryStatusRow>> ListStatuses(IReadOnlyCollection<ulong> guildIds);
        Task<IReadOnlyList<DeliveryStatusRow>> ListFailing(uint minFailures);
        Task<IReadOnlyList<DeliveryRow>> ListRecentPosts(ulong guildId, int take);
        Task<IReadOnlyList<DeliveryRow>> ListLastPosts(ulong guildId);
        Task<ItemPosts> GetItemPosts(string source, ulong itemId, DataTransaction? transaction = null);
    }
}
