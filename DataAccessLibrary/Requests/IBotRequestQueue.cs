using DataAccessLibrary.Requests.Models;
using MinerBot.Shared.Requests;

namespace DataAccessLibrary.Requests
{
    // The dashboard's side of its requests to the bot: adding a signed request in a transaction with its audit row, and
    // reading requests and the bot's settings back. Nothing more, so the dashboard is never handed a way to take a request
    // or write how one ended, which only the bot does.
    public interface IBotRequestQueue
    {
        Task<ulong> AddRequest(RequestFields fields, byte[] signature, DataTransaction transaction);
        Task<BotRequestRow?> GetRequest(ulong id);
        Task<BotRequestRow?> GetRequestByKey(string requestKey);
        Task<BotSettingsRow?> GetSettings();
        Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work);
    }
}
