using SteamKit2;
using System.Text.Json;

namespace SteamWebAPIAccess
{
    internal enum LogOnFailure
    {
        TokenRejected,
        Temporary,
        Other
    }

    // The rules for recovering from a failed Steam sign-in, kept apart from the connection so they can be tested on their own
    internal static class SteamSignInPolicy
    {
        static readonly TimeSpan FirstReconnectDelay = TimeSpan.FromSeconds(30);
        static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromMinutes(15);
        const double MaxJitter = 0.2;
        const int MaxDoublings = 10;

        public static LogOnFailure ClassifyLogOnFailure(EResult result) => result switch
        {
            EResult.Expired or EResult.Revoked or EResult.InvalidPassword or EResult.InvalidSignature or EResult.AccessDenied
                => LogOnFailure.TokenRejected,

            EResult.TryAnotherCM or EResult.ServiceUnavailable or EResult.Timeout or EResult.Busy or EResult.RateLimitExceeded
                or EResult.AccountLoginDeniedThrottle or EResult.NoConnection or EResult.ConnectFailed or EResult.IOFailure
                or EResult.RemoteDisconnect or EResult.Pending or EResult.Cancelled
                => LogOnFailure.Temporary,

            _ => LogOnFailure.Other
        };

        // Steam trouble says nothing about the password, but being told to slow down does count
        public static bool UsesUpPasswordAttempt(EResult result) =>
            ClassifyLogOnFailure(result) != LogOnFailure.Temporary || result is EResult.RateLimitExceeded or EResult.AccountLoginDeniedThrottle;

        // 30 seconds doubling to 15 minutes, plus up to 20% so several restarts don't retry in step. random is from 0 to 1.
        public static TimeSpan GetReconnectDelay(int attempt, double random)
        {
            var delay = FirstReconnectDelay * Math.Pow(2, Math.Min(attempt, MaxDoublings));

            if (delay > MaxReconnectDelay)
                delay = MaxReconnectDelay;

            return delay * (1 + random * MaxJitter);
        }

        // Null, which leaves the choice to SteamKit, when the list is empty
        public static T? PickServer<T>(IEnumerable<T> servers, Random random) where T : class
        {
            var list = servers.ToList();

            return list.Count == 0 ? null : list[random.Next(list.Count)];
        }

        // Null when the token can't be read, which is the same as not having one
        public static DateTimeOffset? ReadTokenExpiry(string token)
        {
            try
            {
                var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');

                if (payload.Length % 4 != 0)
                    payload += new string('=', 4 - payload.Length % 4);

                using var json = JsonDocument.Parse(Convert.FromBase64String(payload));

                return DateTimeOffset.FromUnixTimeSeconds(json.RootElement.GetProperty("exp").GetInt64());
            }
            catch (Exception e) when (e is IndexOutOfRangeException or FormatException or JsonException
                or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }
}
