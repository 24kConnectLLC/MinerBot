using MinerBot_2._0.Attributes;
using MinerBot_2._0.Models;
using SteamWebAPIAccess;
using System.Globalization;

namespace MinerBot_2._0.Jobs
{
    public class SteamTokenJob
    {
        private readonly ISteamAPIAccess _steamAPI;

        public SteamTokenJob(ISteamAPIAccess steamAPI)
        {
            _steamAPI = steamAPI;
        }

        // Its detail says what Steam did. A run fails when Steam refused or the token file couldn't be saved, which is worth
        // showing; one while the bot isn't signed in to Steam doesn't, since steam.needs_sign_in and its message say so.
        [Job("0 12 * * *")] // Every Day
        public async Task<string> RenewSteamToken()
        {
            var renewal = await _steamAPI.RenewTokenAsync();

            return renewal.Outcome switch
            {
                TokenRenewalOutcome.Renewed => $"Steam renewed the token, which now ends on {Date(renewal.Expires)}",
                TokenRenewalOutcome.Kept => $"Steam kept the token, which ends on {Date(renewal.Expires)}",
                TokenRenewalOutcome.NotSaved => throw new JobFailedException("the token file couldn't be saved first, so nothing was renewed"),
                TokenRenewalOutcome.Failed => throw new JobFailedException($"Steam didn't renew the token ({renewal.FailureType})"),
                _ => "not signed in to Steam, so there was no token to renew"
            };
        }

        // A token whose end can't be read has none to show
        static string Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "a date it doesn't say";
    }
}
