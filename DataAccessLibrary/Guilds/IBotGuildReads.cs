using DataAccessLibrary.Guilds.Models;

namespace DataAccessLibrary.Guilds
{
    // The dashboard's reads of the bot's copy of its servers. Reads only, so the dashboard is never handed a way to write
    // the bot's tables.
    public interface IBotGuildReads
    {
        Task<IReadOnlyList<BotChannelRow>> FindChannels(IReadOnlyCollection<ulong> channelIds);
        Task<BotGuildRow?> GetGuild(ulong guildId);
        Task<IReadOnlyList<BotChannelRow>> ListChannels(ulong guildId);
        Task<IReadOnlyList<GuildEventRow>> ListGuildEvents(ulong guildId, int take);
        Task<IReadOnlyList<BotGuildRow>> ListGuilds();
        Task<IReadOnlyList<GuildDayRow>> ListMemberDays(ulong guildId, DateTime from, DateTime to);
        Task<IReadOnlyList<GuildDayRow>> MemberTrends(DateTime from);
    }
}
