using DataAccessLibrary.Discord.Models;

namespace DataAccessLibrary.Discord
{
    // The dashboard's reads of each server's channel settings from the website. Reads only, apart from IGuildData, whose
    // saves and deletes are the bot's.
    public interface IGuildSettingsReads
    {
        Task<Guild?> GetSettings(ulong guildId);
        Task<IReadOnlyList<Guild>> ListSettings();
    }
}
