using DataAccessLibrary.Blacklist.Models;

namespace DataAccessLibrary.Blacklist
{
    public class BlacklistData : DbBaseConnection, IBlacklistData
    {
        // The columns' sizes, which MariaDB counts in code points. Strict mode refuses a longer value with an error that
        // looks like an outage, so the writes refuse it first. Public so the dashboard's forms stop at the same place.
        public const int UsernameLimit = 60;
        public const int ReasonLimit = 60;
        public const int NotesLimit = 120;

        // The most rows ListOutOfRangeBans gives. There should be none: only something other than the dashboard can type
        // one in, and a page shows them all at once.
        public const int OutOfRangeLimit = 100;

        // The column is DECIMAL(10,0), so it also takes numbers no account number can be, such as a SteamID64 cut to ten
        // digits or a negative one, if they're typed in somewhere else. No player can match those, and reading one into
        // BlacklistModel's uint would fail the whole list, so the lists leave them out and ListOutOfRangeBans finds them.
        const string FitsTheModel = "steam_id BETWEEN 0 AND 4294967295";

        // An end date no DateTime can hold. MariaDB's default mode stores a zero date such as 0000-00-00 00:00:00, or one
        // with a year, month or day of 0, from anything else that writes the table, and a session that allows invalid
        // dates can store a day past the month's end. MySql.Data throws reading any of them, which would fail a whole
        // list, so every read gives such a value as NULL, with a flag the page shows instead.
        const string Unreadable = @"(YEAR(expire_date) = 0 OR MONTH(expire_date) = 0 OR DAYOFMONTH(expire_date) = 0
                            OR DAYOFMONTH(expire_date) > DAYOFMONTH(LAST_DAY(expire_date)))";

        // Every read's columns. The flag is 0 for a real date and for none.
        const string Columns = $@"steam_id, username, reason,
                            CASE WHEN {Unreadable} THEN NULL ELSE expire_date END AS expire_date,
                            COALESCE({Unreadable}, 0) AS expire_date_unreadable,
                            notes";

        public BlacklistData(ISQLConnectionFactory connectionFactory) : base(connectionFactory, DBConnectionName.Blacklist)
        {
        }

        public Task<IEnumerable<BlacklistModel>> GetBlacklist()
        {
            string sql = $@"SELECT {Columns}
                            FROM ihvph_blacklist
                            WHERE {FitsTheModel}";

            return LoadDataEnum<BlacklistModel, dynamic>(sql, new { });
        }

        // In a transaction it's a locking read, so a change can compare the row with what the editor saw and write it, with
        // its audit row, before anyone else can change it. For an account with no row it locks the gap the row would go
        // in, so a second add of the same account waits, or deadlocks and runs again, and then finds the first one's row.
        public Task<BlacklistModel?> GetBannedPlayer(uint steamID, DataTransaction? transaction = null)
        {
            string sql = $@"SELECT {Columns}
                            FROM ihvph_blacklist
                            WHERE steam_id = @steamID";

            if (transaction != null)
                sql += " FOR UPDATE";

            return LoadFirstData<BlacklistModel, dynamic>(sql, new { steamID }, transaction);
        }

        // A player with no row isn't changed, which gives false. A row whose values are already these still counts as
        // one, since MySql.Data counts the rows a statement matched, so a caller that cares compares them first.
        public async Task<bool> UpdatePlayerBan(BlacklistModel player, DataTransaction? transaction = null)
        {
            RequireFits(player);

            string sql = @"UPDATE ihvph_blacklist
                            SET username = @username, reason = @reason, expire_date = @expire_date, notes = @notes
                            WHERE steam_id = @steam_id";

            return await SaveDataCount(sql, Values(player), transaction).ConfigureAwait(false) == 1;
        }

        // Account 0 isn't a Steam account, so a new entry can't use it
        public Task InsertPlayerBan(BlacklistModel player, DataTransaction? transaction = null)
        {
            if (player.steam_id == 0)
                throw new ArgumentOutOfRangeException(nameof(player), "Account number 0 isn't a Steam account");

            RequireFits(player);

            string sql = @"INSERT into ihvph_blacklist (steam_id, username, reason, expire_date, notes)
                            values (@steam_id, @username, @reason, @expire_date, @notes)";

            return SaveDataCount(sql, Values(player), transaction);
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

            string sql = $@"SELECT {Columns}
                            FROM ihvph_blacklist
                            {where}
                            ORDER BY username, steam_id
                            LIMIT @take OFFSET @skip;
                            SELECT COUNT(*)
                            FROM ihvph_blacklist
                            {where};";

            return LoadPage<BlacklistModel, dynamic>(sql, new { accountNumber, pattern, take, skip });
        }

        // The steam_id values the lists leave out, smallest first
        public async Task<IReadOnlyList<decimal>> ListOutOfRangeSteamIds()
        {
            string sql = $@"SELECT steam_id
                            FROM ihvph_blacklist
                            WHERE NOT ({FitsTheModel})
                            ORDER BY steam_id";

            return (await LoadDataEnum<decimal, dynamic>(sql, new { }).ConfigureAwait(false)).ToList();
        }

        // The whole rows the lists leave out, smallest number first, so the blacklist page can show them and offer to
        // remove them
        public async Task<IReadOnlyList<OutOfRangeBan>> ListOutOfRangeBans()
        {
            string sql = $@"SELECT {Columns}
                            FROM ihvph_blacklist
                            WHERE NOT ({FitsTheModel})
                            ORDER BY steam_id
                            LIMIT {OutOfRangeLimit}";

            return (await LoadDataEnum<OutOfRangeBan, dynamic>(sql, new { }).ConfigureAwait(false)).ToList();
        }

        // One of those rows, as GetBannedPlayer reads an entry: in a transaction it's a locking read, so its removal can
        // check it's as the page showed it and keep what it held for the audit row
        public Task<OutOfRangeBan?> GetOutOfRangeBan(decimal steamId, DataTransaction? transaction = null)
        {
            string sql = $@"SELECT {Columns}
                            FROM ihvph_blacklist
                            WHERE steam_id = @steamId AND NOT ({FitsTheModel})";

            if (transaction != null)
                sql += " FOR UPDATE";

            return LoadFirstData<OutOfRangeBan, dynamic>(sql, new { steamId }, transaction);
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

        // What a write saves: the columns, never the flag, which isn't one
        static object Values(BlacklistModel player) => new
        {
            player.steam_id,
            player.username,
            player.reason,
            player.expire_date,
            player.notes
        };

        // Before any SQL, so a value the column can't hold stops the change it travels with in a transaction too
        static void RequireFits(BlacklistModel player)
        {
            foreach (var (text, limit, what) in new[] { (player.username, UsernameLimit, "name"), (player.reason, ReasonLimit, "reason"), (player.notes, NotesLimit, "notes") })
            {
                if (text != null && CodePoints(text) > limit)
                    throw new ArgumentException($"A blacklist entry's {what} holds at most {limit} characters", nameof(player));
            }
        }
    }
}
