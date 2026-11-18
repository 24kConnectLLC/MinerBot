using DataAccessLibrary.Guilds.Models;

namespace DataAccessLibrary.Guilds
{
    public interface IBotGuildData
    {
        Task<bool> LeaveGuild(ulong guildId, DateTime at, ulong runId, uint seq, string? detail);
        Task<IEnumerable<ulong>> ListGuildsIn();
        Task<int> MarkUnavailable(IReadOnlyCollection<ulong> guildIds, DateTime at);
        Task<bool> SaveGuild(BotGuild guild, IReadOnlyList<BotGuildChannel> channels, DateTime at, ulong runId, uint seq);
        Task SaveMemberCount(ulong guildId, uint memberCount, DateTime day, DateTime at);
    }
}
