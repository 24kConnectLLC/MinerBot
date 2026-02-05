using DataAccessLibrary.Discord.Models;

namespace DataAccessLibrary.Discord
{
    public class GuildData : DbBaseConnection, IGuildData
    {
        public GuildData(ISQLConnectionFactory connectionFactory) : base(connectionFactory, DBConnectionName.Guild)
        {
            
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

        public Task<int> GetArmorChannelID(string guildId)
        {
            string sql = @"SELECT armor_channel_id
                            FROM ihvph_discord_servers
                            WHERE guild_id = @guildId";

            return LoadFirstData<int, dynamic>(sql, new { guildId });
        }

        public Task<int> GetMapChannelID(string guildId)
        {
            string sql = @"SELECT map_channel_id
                            FROM ihvph_discord_servers
                            WHERE guild_id = @guildId";

            return LoadFirstData<int, dynamic>(sql, new { guildId });
        }

        public Task<int> GetLobbyChannelID(string guildId)
        {
            string sql = @"SELECT lobby_channel_id
                            FROM ihvph_discord_servers
                            WHERE guild_id = @guildId";

            return LoadFirstData<int, dynamic>(sql, new { guildId });
        }

        public Task UpdateGuild(Guild guild)
        {
            string sql = @"UPDATE ihvph_discord_servers
                            SET armor_channel_id = @armor_channel_id, map_channel_id = @map_channel_id, lobby_channel_id = @lobby_channel_id, announcements_channel_id = @announcements_channel_id
                            WHERE guild_id = @guild_id";

            return SaveData(sql, guild);
        }

        public Task InsertGuild(Guild guild)
        {
            string sql = @"INSERT into ihvph_discord_servers (guild_id, armor_channel_id, map_channel_id, lobby_channel_id, announcements_channel_id)
                            values (@guild_id, @armor_channel_id, @map_channel_id, @lobby_channel_id, @announcements_channel_id)";

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
