using DataAccessLibrary.Posting.Models;

namespace DataAccessLibrary.Posting
{
    public interface IGuildSwitchData
    {
        Task<IReadOnlyList<GuildSwitches>> ListSwitches();
        Task<GuildSwitches?> GetSwitches(ulong guildId, DataTransaction? transaction = null);
        Task<SwitchSave> SaveSwitches(SwitchChange change, DataTransaction transaction);
        Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work);
    }
}
