using System.Text;
using SteamKit2;

namespace SteamWebAPIAccess.Tests
{
    public class SteamSignInPolicyTests
    {
        [Theory]
        [InlineData(EResult.Expired)]
        [InlineData(EResult.Revoked)]
        [InlineData(EResult.InvalidPassword)]
        [InlineData(EResult.InvalidSignature)]
        [InlineData(EResult.AccessDenied)]
        public void CodesThatRejectTheToken(EResult result) =>
            Assert.Equal(LogOnFailure.TokenRejected, SteamSignInPolicy.ClassifyLogOnFailure(result));

        [Theory]
        [InlineData(EResult.TryAnotherCM)]
        [InlineData(EResult.ServiceUnavailable)]
        [InlineData(EResult.Timeout)]
        [InlineData(EResult.Busy)]
        [InlineData(EResult.RateLimitExceeded)]
        [InlineData(EResult.AccountLoginDeniedThrottle)]
        [InlineData(EResult.NoConnection)]
        [InlineData(EResult.ConnectFailed)]
        [InlineData(EResult.IOFailure)]
        [InlineData(EResult.RemoteDisconnect)]
        [InlineData(EResult.Pending)]
        [InlineData(EResult.Cancelled)]
        public void CodesThatSayNothingAboutTheToken(EResult result) =>
            Assert.Equal(LogOnFailure.Temporary, SteamSignInPolicy.ClassifyLogOnFailure(result));

        [Theory]
        [InlineData(EResult.Fail)]
        [InlineData(EResult.AccountDisabled)]
        [InlineData(EResult.Banned)]
        [InlineData(EResult.AccountLogonDenied)]
        public void AnyOtherCodeIsLeftToTheOneDayLimit(EResult result) =>
            Assert.Equal(LogOnFailure.Other, SteamSignInPolicy.ClassifyLogOnFailure(result));

        [Theory]
        [InlineData(EResult.InvalidPassword, true)]
        [InlineData(EResult.Fail, true)]
        [InlineData(EResult.RateLimitExceeded, true)]
        [InlineData(EResult.AccountLoginDeniedThrottle, true)]
        [InlineData(EResult.ServiceUnavailable, false)]
        [InlineData(EResult.TryAnotherCM, false)]
        [InlineData(EResult.Timeout, false)]
        public void WhatUsesUpAPasswordAttempt(EResult result, bool usesOneUp) =>
            Assert.Equal(usesOneUp, SteamSignInPolicy.UsesUpPasswordAttempt(result));

        [Theory]
        [InlineData(0, 30)]
        [InlineData(1, 60)]
        [InlineData(2, 120)]
        [InlineData(3, 240)]
        [InlineData(4, 480)]
        [InlineData(5, 900)]
        [InlineData(6, 900)]
        [InlineData(10_000, 900)]
        public void ReconnectDelayDoublesToACap(int attempt, int seconds)
        {
            Assert.Equal(TimeSpan.FromSeconds(seconds), SteamSignInPolicy.GetReconnectDelay(attempt, random: 0));
            Assert.Equal(TimeSpan.FromSeconds(seconds * 1.2), SteamSignInPolicy.GetReconnectDelay(attempt, random: 1));
        }

        [Fact]
        public void EmptyServerListLeavesTheChoiceToSteamKit() =>
            Assert.Null(SteamSignInPolicy.PickServer(Array.Empty<string>(), new Random(1)));

        [Fact]
        public void RetriesCanReachEveryServer()
        {
            string[] servers = ["a", "b", "c"];
            var random = new Random(1);

            var picked = Enumerable.Range(0, 200).Select(_ => SteamSignInPolicy.PickServer(servers, random)).Distinct();

            Assert.Equal(servers, picked.Order());
        }

        // Base64 pads differently at each length, and Steam's tokens drop the padding
        [Theory]
        [InlineData("")]
        [InlineData("x")]
        [InlineData("xx")]
        [InlineData("xxx")]
        [InlineData("???")]
        [InlineData("~~~")]
        public void ReadsTheExpiryOfAToken(string padding)
        {
            var expires = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);

            Assert.Equal(expires, SteamSignInPolicy.ReadTokenExpiry(CreateToken($$"""{"exp":1900000000,"p":"{{padding}}"}""")));
        }

        [Theory]
        [InlineData("")]
        [InlineData("not a token")]
        [InlineData("a.b.c")]
        [InlineData("a.bm90IGpzb24.c")]
        [InlineData("a..c")]
        public void DamagedTokenHasNoExpiry(string token) =>
            Assert.Null(SteamSignInPolicy.ReadTokenExpiry(token));

        [Theory]
        [InlineData("""{"sub":"1"}""")]
        [InlineData("""{"exp":"soon"}""")]
        [InlineData("""{"exp":99999999999999999}""")]
        [InlineData("""[1]""")]
        [InlineData("""1""")]
        public void TokenWithoutAReadableExpiryHasNoExpiry(string payload) =>
            Assert.Null(SteamSignInPolicy.ReadTokenExpiry(CreateToken(payload)));

        static string CreateToken(string payload)
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
            return $"header.{encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_')}.signature";
        }
    }
}
