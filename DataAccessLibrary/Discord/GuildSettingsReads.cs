using DataAccessLibrary.Discord.Models;

namespace DataAccessLibrary.Discord
{
    // Each server's four notification channels as /admin set_channels saved them on the website: its ID and four channel
    // IDs, nothing private, with 0 for a kind that's off. A server that never set them has no row. The columns are named
    // and the list has a limit, unlike the bot's GuildData, and the text goes as utf8mb4, as every dashboard read's does.
    public class GuildSettingsReads : DbBaseConnection, IGuildSettingsReads
    {
        // The most rows the list reads, as many servers as the dashboard lists
        public const int Limit = 500;

        const string Columns = "guild_id, armor_channel_id, map_channel_id, lobby_channel_id, announcements_channel_id";

        public GuildSettingsReads(ISQLConnectionFactory connectionFactory) : base(connectionFactory, DBConnectionName.Guild)
        {
        }

        // Every server's settings, by server ID
        public async Task<IReadOnlyList<Guild>> ListSettings()
        {
            string sql = $@"SELECT {Columns}
                            FROM ihvph_discord_servers
                            ORDER BY guild_id
                            LIMIT @take";

            return (await LoadDataEnum<Guild, object>(sql, new { take = Limit }).ConfigureAwait(false)).ToList();
        }

        // Null for a server that never set its channels
        public Task<Guild?> GetSettings(ulong guildId)
        {
            string sql = $@"SELECT {Columns}
                            FROM ihvph_discord_servers
                            WHERE guild_id = @guildId";

            return LoadFirstData<Guild?, object>(sql, new { guildId });
        }
    }
}
