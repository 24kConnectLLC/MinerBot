using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OAuth;
using MinerBot.Dashboard.Settings;

namespace MinerBot.Dashboard.Auth
{
    // ASP.NET Core's own OAuth handler, set up for Discord: the code grant with PKCE, the identify scope alone, and no
    // tokens kept. The handler creates and checks the state and the correlation cookie itself.
    internal static class DiscordSignIn
    {
        public const string Scope = "identify";

        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        public static void Configure(OAuthOptions options, DiscordSignInOptions discord)
        {
            options.ClientId = discord.ClientId;
            options.ClientSecret = discord.ClientSecret;
            options.AuthorizationEndpoint = discord.AuthorizationEndpoint;
            options.TokenEndpoint = discord.TokenEndpoint;
            options.UserInformationEndpoint = discord.UserInformationEndpoint;
            options.CallbackPath = DashboardAuth.CallbackPath;

            // The user's ID, name and two-factor flag, and nothing about their servers, email or anything else
            options.Scope.Clear();
            options.Scope.Add(Scope);

            // The handler makes a verifier, keeps it in the protected state and sends Discord an S256 challenge
            options.UsePkce = true;

            // The access token is revoked as soon as the user has been read, so it's never kept anywhere
            options.SaveTokens = false;

            // One request for the code, never tried again, because a code works only once. The default handler follows
            // redirects, and a 307 would send the code and the client secret somewhere else.
            options.BackchannelTimeout = Timeout;
            options.BackchannelHttpHandler = new SocketsHttpHandler { AllowAutoRedirect = false };

            options.SignInScheme = DashboardAuth.CookieScheme;

            // __Host-, so murderminershub.com can't plant a correlation cookie from a sign-in it started itself and sign a
            // visitor in as its own account. Lax still arrives on Discord's top-level redirect back.
            options.CorrelationCookie.Name = DashboardAuth.CorrelationCookiePrefix;
            options.CorrelationCookie.Path = "/";
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
            options.CorrelationCookie.HttpOnly = true;

            options.EventsType = typeof(DiscordSignInEvents);
        }

        // The token is used only when Discord granted exactly identify, as a bearer token. A different scope means the
        // request was changed on the way, or Discord changed, and either way the token isn't trusted.
        internal static bool GrantedOnlyIdentify(OAuthTokenResponse tokens)
        {
            if (!string.Equals(tokens.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
                return false;

            if (tokens.Response is not { } response || !response.RootElement.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.String)
                return false;

            var granted = scope.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return granted.Length > 0 && granted.All(name => name == Scope);
        }
    }
}
