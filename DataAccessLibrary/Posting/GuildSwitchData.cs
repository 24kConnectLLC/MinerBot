using DataAccessLibrary.Audit;
using DataAccessLibrary.Posting.Models;
using MySql.Data.MySqlClient;

namespace DataAccessLibrary.Posting
{
    // Each server's switches for the bot's armor, map and lobby posts, in MinerBot's own database, as the dashboard reads and
    // saves them. No row means every kind is on. A save goes in the caller's transaction on MinerBot's connection, with its
    // audit row, and applies only to the switches the form was drawn from: each save gives the row its next version, so a
    // form drawn before another save is refused rather than undoing it. The bot reads every row at each notification run,
    // through INotificationData, and deletes a server's row when the server removes MinerBot.
    public class GuildSwitchData : DbBaseConnection, IGuildSwitchData
    {
        // As many servers as the dashboard lists
        public const int ListLimit = 500;

        private readonly DatabaseNames _names;

        public GuildSwitchData(ISQLConnectionFactory connectionFactory, DatabaseNames names) : base(connectionFactory, DBConnectionName.Bot)
        {
            _names = names;
        }

        // Every server's switches, by server ID
        public async Task<IReadOnlyList<GuildSwitches>> ListSwitches()
        {
            string sql = $@"SELECT {NotificationData.SwitchColumns}
                            FROM {_names.Bot}.guild_settings
                            ORDER BY guild_id
                            LIMIT {ListLimit}";

            return (await LoadDataEnum<GuildSwitches, object?>(sql, null).ConfigureAwait(false)).ToList();
        }

        // Null for a server with none. In a transaction, the row stays locked until it ends, and so does the place a new one
        // would go, so a save that depends on what this read can't be split by another.
        public Task<GuildSwitches?> GetSwitches(ulong guildId, DataTransaction? transaction = null)
        {
            string sql = $@"SELECT {NotificationData.SwitchColumns}
                            FROM {_names.Bot}.guild_settings
                            WHERE guild_id = @guildId{(transaction != null ? " FOR UPDATE" : "")}";

            return LoadFirstData<GuildSwitches?, object>(sql, new { guildId }, transaction);
        }

        // In the caller's transaction, with its audit row. Nothing is written when the switches asked for are the ones the server
        // has, or when they changed after the form was drawn: a version that isn't the row's, a row that came after a form
        // drawn without one, or one that another save added at the same moment, which MariaDB refuses as a duplicate.
        public async Task<SwitchSave> SaveSwitches(SwitchChange change, DataTransaction transaction)
        {
            ArgumentNullException.ThrowIfNull(change);
            ArgumentNullException.ThrowIfNull(transaction);
            RequireUtc(change.At, nameof(change));

            if (change.GuildId == 0 || change.ChangedBy == 0)
                throw new ArgumentException("A save is of a server, by someone", nameof(change));

            if (change.Role is not (AuditRole.Owner or AuditRole.ServerAdmin))
                throw new ArgumentException($"Switches are saved by the {AuditRole.Owner} or a {AuditRole.ServerAdmin}", nameof(change));

            var bot = _names.Bot;

            string insert = $@"INSERT INTO {bot}.guild_settings ({NotificationData.SwitchColumns})
                               VALUES (@GuildId, @ArmorOn, @MapsOn, @LobbiesOn, 1, @At, @ChangedBy, @Role)";

            string update = $@"UPDATE {bot}.guild_settings
                               SET armor_on = @ArmorOn, maps_on = @MapsOn, lobbies_on = @LobbiesOn, version = version + 1, changed_at = @At, changed_by = @ChangedBy,
                               changed_by_role = @Role
                               WHERE guild_id = @GuildId AND version = @Version";

            var before = await GetSwitches(change.GuildId, transaction).ConfigureAwait(false);

            if ((before?.armor_on ?? true) == change.ArmorOn && (before?.maps_on ?? true) == change.MapsOn && (before?.lobbies_on ?? true) == change.LobbiesOn)
                return new SwitchSave(SwitchSaveOutcome.Unchanged, before, null);

            if ((before?.version ?? 0) != change.Version)
                return new SwitchSave(SwitchSaveOutcome.Stale, before, null);

            if (before == null)
            {
                try
                {
                    await transaction.Execute(insert, change).ConfigureAwait(false);
                }
                catch (MySqlException e) when (e.Number == (int)MySqlErrorCode.DuplicateKeyEntry)
                {
                    return new SwitchSave(SwitchSaveOutcome.Stale, null, null);
                }
            }
            else if (await transaction.Execute(update, change).ConfigureAwait(false) != 1)
                return new SwitchSave(SwitchSaveOutcome.Stale, before, null);

            return new SwitchSave(SwitchSaveOutcome.Saved, before, new GuildSwitches
            {
                guild_id = change.GuildId,
                armor_on = change.ArmorOn,
                maps_on = change.MapsOn,
                lobbies_on = change.LobbiesOn,
                version = change.Version + 1,
                changed_at = change.At,
                changed_by = change.ChangedBy,
                changed_by_role = change.Role
            });
        }

        // On MinerBot's own connection, so a save and its audit row commit together. The database is named first, so a
        // missing BotConnection fails with that reason before any connection is tried.
        public Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work)
        {
            _ = _names.Bot;
            return InTransaction(work);
        }
    }
}
