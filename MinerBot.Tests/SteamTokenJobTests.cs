using MinerBot_2._0.Jobs;
using MinerBot_2._0.Models;
using SteamWebAPIAccess;

namespace MinerBot.Tests
{
    // The daily renewal of the Steam token says what Steam did. It fails when Steam refused or the token file couldn't be
    // saved, and not while the bot isn't signed in, which steam.needs_sign_in and its message already say.
    public class SteamTokenJobTests
    {
        static readonly DateOnly Ends = new(2027, 5, 1);

        [Theory]
        [InlineData(TokenRenewalOutcome.Renewed, "Steam renewed the token, which now ends on 2027-05-01")]
        [InlineData(TokenRenewalOutcome.Kept, "Steam kept the token, which ends on 2027-05-01")]
        [InlineData(TokenRenewalOutcome.NotSignedIn, "not signed in to Steam, so there was no token to renew")]
        public async Task RenewalSaysWhatSteamDid(TokenRenewalOutcome outcome, string detail)
        {
            var steam = new FakeSteam { Renewal = new TokenRenewal(outcome, outcome == TokenRenewalOutcome.NotSignedIn ? null : Ends) };

            Assert.Equal(detail, await new SteamTokenJob(steam).RenewSteamToken());
            Assert.Equal(1, steam.Renewals);
        }

        [Theory]
        [InlineData(TokenRenewalOutcome.NotSaved, "the token file couldn't be saved first, so nothing was renewed")]
        [InlineData(TokenRenewalOutcome.Failed, "Steam didn't renew the token (TimeoutException)")]
        public async Task RenewalThatFailedFailsTheRun(TokenRenewalOutcome outcome, string detail)
        {
            var steam = new FakeSteam { Renewal = new TokenRenewal(outcome, Ends, "TimeoutException") };

            var failed = await Assert.ThrowsAsync<JobFailedException>(() => new SteamTokenJob(steam).RenewSteamToken());

            Assert.Equal(detail, failed.Message);
        }
    }
}
