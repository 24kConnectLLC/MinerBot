using DataAccessLibrary.Blacklist.Models;

namespace DataAccessLibrary.Blacklist
{
    public interface IBlacklistData
    {
        Task<BlacklistModel> GetBannedPlayer(uint steamID);
        Task<IEnumerable<BlacklistModel>> GetBlacklist();
        Task InsertPlayerBan(BlacklistModel player);
        Task RemovePlayerBan(ulong steamID);
        Task UpdatePlayerBan(BlacklistModel player);
    }
}