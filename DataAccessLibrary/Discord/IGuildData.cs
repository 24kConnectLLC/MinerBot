using DataAccessLibrary.Discord.Models;

namespace DataAccessLibrary.Discord
{
    public interface IGuildData
    {
        Task DeleteGuild(ulong guildId);
        Task<IEnumerable<Guild>> GetAllGuilds();
        Task<int> GetArmorChannelID(string guildId);
        Task<Guild> GetGuild(string guildId);
        Task<int> GetLobbyChannelID(string guildId);
        Task<int> GetMapChannelID(string guildId);
        Task InsertGuild(Guild guild);
        Task UpdateGuild(Guild guild);
    }
}