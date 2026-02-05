using DataAccessLibrary.Blacklist.Models;

namespace DataAccessLibrary.Blacklist
{
    public class BlacklistData : DbBaseConnection, IBlacklistData
    {
        public BlacklistData(ISQLConnectionFactory connectionFactory) : base(connectionFactory, DBConnectionName.Blacklist)
        {
        }

        public Task<IEnumerable<BlacklistModel>> GetBlacklist()
        {
            string sql = @"SELECT steam_id, username, reason, expire_date, notes
                            FROM ihvph_blacklist";

            return LoadDataEnum<BlacklistModel, dynamic>(sql, new { });
        }

        public Task<BlacklistModel> GetBannedPlayer(uint steamID)
        {
            string sql = @"SELECT steam_id, username, reason, expire_date, notes
                            FROM ihvph_blacklist
                            WHERE steam_id = @steamID";

            return LoadFirstData<BlacklistModel, dynamic>(sql, new { steamID });
        }

        public Task UpdatePlayerBan(BlacklistModel player)
        {
            string sql = @"UPDATE ihvph_blacklist
                            SET username = @username, reason = @reason, expire_date = @expire_date, notes = @notes
                            WHERE steam_id = @steam_id";

            return SaveData(sql, player);
        }

        public Task InsertPlayerBan(BlacklistModel player)
        {
            string sql = @"INSERT into ihvph_blacklist (steam_id, username, reason, expire_date, notes)
                            values (@steam_id, @username, @reason, @expire_date, @notes)";

            return SaveData(sql, player);
        }

        public Task RemovePlayerBan(ulong steamID)
        {
            string sql = @"DELETE FROM ihvph_blacklist
                            WHERE steam_id = @steam_id";

            return SaveData(sql, new { steamID });
        }
    }
}
