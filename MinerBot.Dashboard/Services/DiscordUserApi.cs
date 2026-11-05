using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Settings;

namespace MinerBot.Dashboard.Services
{
    // Who signed in, as Discord's /users/@me tells it
    internal sealed record DiscordUser(ulong Id, string UserName, string? GlobalName, bool MfaEnabled)
    {
        // Discord's display name when there is one, otherwise the user name
        public string DisplayName => string.IsNullOrWhiteSpace(GlobalName) ? UserName : GlobalName;
    }

    // A sign-in that couldn't go on. The handler's RemoteFailure logs its message, so the message never holds a token,
    // a code or anything Discord sent.
    internal sealed class DiscordSignInException(string message) : Exception(message);

    // The two calls the dashboard makes to Discord's API with a user's token: who signed in, and then revoking the token
    // as soon as it has said so. Through a typed client that follows no redirects and logs nothing.
    internal sealed class DiscordUserApi(HttpClient http, IOptions<DiscordSignInOptions> settings, ILogger<DiscordUserApi> logger)
    {
        public const string UserAgent = "MinerBot.Dashboard";

        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan RevokeLimit = TimeSpan.FromSeconds(5);

        // An answer about one user is a few hundred bytes
        const long MaxResponseSize = 1024 * 1024;

        public static void Configure(HttpClient client)
        {
            client.Timeout = Timeout;
            client.MaxResponseContentBufferSize = MaxResponseSize;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }

        public async Task<DiscordUser> GetCurrentUserAsync(string accessToken, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, settings.Value.UserInformationEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await http.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new DiscordSignInException(string.Create(CultureInfo.InvariantCulture, $"Discord answered {(int)response.StatusCode} when asked who signed in"));

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var user = json.RootElement;

            // The ID comes as a string, because it's too big for a JavaScript number
            if (user.ValueKind != JsonValueKind.Object || !user.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || !ulong.TryParse(id.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var userId) || userId == 0)
                throw new DiscordSignInException("Discord's answer had no user ID");

            return new DiscordUser(userId, Text(user, "username") ?? "", Text(user, "global_name"),
                user.TryGetProperty("mfa_enabled", out var mfa) && mfa.ValueKind == JsonValueKind.True);
        }

        // Best effort, within its own limit, and it never throws: a token that couldn't be revoked still ends with
        // Discord's own expiry. Discord revokes the whole grant, refresh token included, so one call is enough.
        public async Task RevokeAsync(string accessToken, CancellationToken cancellationToken)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(RevokeLimit);

            try
            {
                var discord = settings.Value;

                using var request = new HttpRequestMessage(HttpMethod.Post, discord.RevocationEndpoint)
                {
                    Content = new FormUrlEncodedContent([new("token", accessToken), new("token_type_hint", "access_token")])
                };

                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(discord.ClientId + ":" + discord.ClientSecret)));

                using var response = await http.SendAsync(request, limit.Token);

                if (!response.IsSuccessStatusCode)
                    logger.LogWarning("Discord answered {Status} when asked to revoke a sign-in's token, so it lasts until it expires", (int)response.StatusCode);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                logger.LogWarning("A sign-in's token couldn't be revoked ({Type}), so it lasts until it expires", e.GetType().Name);
            }
        }

        static string? Text(JsonElement user, string name) =>
            user.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
