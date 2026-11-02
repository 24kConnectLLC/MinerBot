using MinerBot_2._0.Attributes;
using SteamWebAPIAccess;

namespace MinerBot_2._0.Jobs
{
    public class SteamTokenJob
    {
        private readonly ISteamAPIAccess _steamAPI;

        public SteamTokenJob(ISteamAPIAccess steamAPI)
        {
            _steamAPI = steamAPI;
        }

        [Job("0 12 * * *")] // Every Day
        public async Task RenewSteamToken()
        {
            await _steamAPI.RenewTokenAsync();
        }
    }
}
