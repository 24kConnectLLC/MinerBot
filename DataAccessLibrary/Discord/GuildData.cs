using DataAccessLibrary.Discord.Models;

namespace DataAccessLibrary.Discord
{
    public class GuildData : DbBaseConnection, IGuildData
    {
        public GuildData(ISQLConnectionFactory connectionFactory) : base(connectionFactory, DBConnectionName.Guild)
        {
            // The bot's own queries keep the connection string exactly as configured
            SendsUtf8mb4 = false;
        }

        public Task<IEnumerable<Guild>> GetAllGuilds()
        {
            string sql = @"SELECT *
                            FROM ihvph_discord_servers";

            return LoadDataEnum<Guild, dynamic>(sql, new { });
        }

        public Task<Guild> GetGuild(string guildId)
        {
            string sql = @"SELECT *
                            FROM ihvph_discord_servers
                            WHERE guild_id = @guildId";

            return LoadFirstData<Guild, dynamic>(sql, new { guildId });
        }

        // One statement on the guild_id primary key, so two saves at once can't both try to insert, and the last one wins
        public Task SaveGuild(Guild guild)
        {
            string sql = @"INSERT INTO ihvph_discord_servers (guild_id, armor_channel_id, map_channel_id, lobby_channel_id, announcements_channel_id)
                            VALUES (@guild_id, @armor_channel_id, @map_channel_id, @lobby_channel_id, @announcements_channel_id)
                            ON DUPLICATE KEY UPDATE armor_channel_id = @armor_channel_id, map_channel_id = @map_channel_id, lobby_channel_id = @lobby_channel_id, announcements_channel_id = @announcements_channel_id";

            return SaveData(sql, guild);
        }

        public Task DeleteGuild(ulong guildId)
        {
            string sql = @"DELETE FROM ihvph_discord_servers
                            WHERE guild_id = @guildId";

            return SaveData(sql, new { guildId });
        }
    }
}
