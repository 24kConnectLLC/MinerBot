using DataAccessLibrary.Blacklist.Models;

namespace DataAccessLibrary.Blacklist
{
    public class BlacklistData : DbBaseConnection, IBlacklistData
    {
        // The column is DECIMAL(10,0), so it also takes numbers no account number can be, such as a SteamID64 cut to ten
        // digits or a negative one, if they're typed in somewhere else. No player can match those, and reading one into
        // BlacklistModel's uint would fail the whole list, so the lists leave them out and ListOutOfRangeSteamIds finds them.
        const string FitsTheModel = "steam_id BETWEEN 0 AND 4294967295";

        public BlacklistData(ISQLConnectionFactory connectionFactory) : base(connectionFactory, DBConnectionName.Blacklist)
        {
        }

        public Task<IEnumerable<BlacklistModel>> GetBlacklist()
        {
            string sql = $@"SELECT steam_id, username, reason, expire_date, notes
                            FROM ihvph_blacklist
                            WHERE {FitsTheModel}";

            return LoadDataEnum<BlacklistModel, dynamic>(sql, new { });
        }

        // In a transaction it's a locking read, so a change can compare the row with what the editor saw and write it, with
        // its audit row, before anyone else can change it
        public Task<BlacklistModel?> GetBannedPlayer(uint steamID, DataTransaction? transaction = null)
        {
            string sql = @"SELECT steam_id, username, reason, expire_date, notes
                            FROM ihvph_blacklist
                            WHERE steam_id = @steamID";

            if (transaction != null)
                sql += " FOR UPDATE";

            return LoadFirstData<BlacklistModel, dynamic>(sql, new { steamID }, transaction);
        }

        // A player with no row isn't changed, which gives false
        public async Task<bool> UpdatePlayerBan(BlacklistModel player, DataTransaction? transaction = null)
        {
            string sql = @"UPDATE ihvph_blacklist
                            SET username = @username, reason = @reason, expire_date = @expire_date, notes = @notes
                            WHERE steam_id = @steam_id";

            return await SaveDataCount(sql, player, transaction).ConfigureAwait(false) == 1;
        }

        // Account 0 isn't a Steam account, so a new entry can't use it
        public Task InsertPlayerBan(BlacklistModel player, DataTransaction? transaction = null)
        {
            if (player.steam_id == 0)
                throw new ArgumentOutOfRangeException(nameof(player), "Account number 0 isn't a Steam account");

            string sql = @"INSERT into ihvph_blacklist (steam_id, username, reason, expire_date, notes)
                            values (@steam_id, @username, @reason, @expire_date, @notes)";

            return SaveDataCount(sql, player, transaction);
        }

        // False when the player had no row
        public async Task<bool> RemovePlayerBan(uint steamID, DataTransaction? transaction = null)
        {
            string sql = @"DELETE FROM ihvph_blacklist
                            WHERE steam_id = @steamID";

            return await SaveDataCount(sql, new { steamID }, transaction).ConfigureAwait(false) == 1;
        }

        // By account number, or by a part of the name with LIKE's wildcards taken literally. A row that matches either one
        // is found, so the same text can be tried as both, and with neither given every row is listed.
        public Task<Page<BlacklistModel>> SearchBlacklist(uint? accountNumber, string? username, int page, int pageSize)
        {
            var (take, skip) = RequirePage(page, pageSize);

            var name = username?.Trim() ?? "";
            var pattern = name.Length == 0 ? null : $"%{EscapeLike(name)}%";

            string where = $@"WHERE {FitsTheModel}
                            AND ((@accountNumber IS NULL AND @pattern IS NULL)
                            OR steam_id = @accountNumber
                            OR username LIKE @pattern ESCAPE '!')";

            string sql = $@"SELECT steam_id, username, reason, expire_date, notes
                            FROM ihvph_blacklist
                            {where}
                            ORDER BY username, steam_id
                            LIMIT @take OFFSET @skip;
                            SELECT COUNT(*)
                            FROM ihvph_blacklist
                            {where};";

            return LoadPage<BlacklistModel, dynamic>(sql, new { accountNumber, pattern, take, skip });
        }

        // The steam_id values the lists leave out, smallest first, so the blacklist page can show them and offer to remove them
        public async Task<IReadOnlyList<decimal>> ListOutOfRangeSteamIds()
        {
            string sql = $@"SELECT steam_id
                            FROM ihvph_blacklist
                            WHERE NOT ({FitsTheModel})
                            ORDER BY steam_id";

            return (await LoadDataEnum<decimal, dynamic>(sql, new { }).ConfigureAwait(false)).ToList();
        }

        // Only a row the lists leave out, so this can't remove a real ban without going through RemovePlayerBan
        public async Task<bool> RemoveOutOfRangeSteamId(decimal steamId, DataTransaction? transaction = null)
        {
            string sql = $@"DELETE FROM ihvph_blacklist
                            WHERE steam_id = @steamId AND NOT ({FitsTheModel})";

            return await SaveDataCount(sql, new { steamId }, transaction).ConfigureAwait(false) == 1;
        }

        // On the website's connection. The bot's tables are named with their database, so an audit row can commit
        // together with the change.
        public Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work)
        {
            return InTransaction(work);
        }
    }
}
