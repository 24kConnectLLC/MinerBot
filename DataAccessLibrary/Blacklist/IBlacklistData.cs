using DataAccessLibrary.Blacklist.Models;

namespace DataAccessLibrary.Blacklist
{
    public interface IBlacklistData
    {
        Task<BlacklistModel?> GetBannedPlayer(uint steamID, DataTransaction? transaction = null);
        Task<IEnumerable<BlacklistModel>> GetBlacklist();
        Task InsertPlayerBan(BlacklistModel player, DataTransaction? transaction = null);
        Task<IReadOnlyList<decimal>> ListOutOfRangeSteamIds();
        Task<bool> RemoveOutOfRangeSteamId(decimal steamId, DataTransaction? transaction = null);
        Task<bool> RemovePlayerBan(uint steamID, DataTransaction? transaction = null);
        Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work);
        Task<Page<BlacklistModel>> SearchBlacklist(uint? accountNumber, string? username, int page, int pageSize);
        Task<bool> UpdatePlayerBan(BlacklistModel player, DataTransaction? transaction = null);
    }
}