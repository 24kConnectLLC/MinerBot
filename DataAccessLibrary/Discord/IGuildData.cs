using DataAccessLibrary.Discord.Models;

namespace DataAccessLibrary.Discord
{
    public interface IGuildData
    {
        Task DeleteGuild(ulong guildId);
        Task<IEnumerable<Guild>> GetAllGuilds();
        Task<Guild> GetGuild(string guildId);
        Task InsertGuild(Guild guild);
        Task UpdateGuild(Guild guild);
    }
}