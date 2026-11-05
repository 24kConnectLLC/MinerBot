using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace MinerBot.Dashboard.Tests.Support
{
    // Discord's three endpoints the dashboard calls, in process. Each request is checked the way Discord would check it,
    // every call is recorded, and any other address throws, so a test that forgot the fake can't reach the network.
    internal sealed class FakeDiscord(string clientId, string clientSecret, string redirectUri) : HttpMessageHandler
    {
        public const string DiscordOrigin = "https://discord.com";
        public const string DiscordAuthorizeEndpoint = DiscordOrigin + "/oauth2/authorize";

        // Where the fake answers: Discord's own addresses, unless a test moves it to show that the dashboard takes every
        // address from its settings. Set before the first request.
        public string Origin { get; set; } = DiscordOrigin;

        // Where the browser is sent, which the fake never serves, since Discord's page is the user's
        public string AuthorizeEndpoint => Origin + "/oauth2/authorize";
        public string TokenEndpoint => Origin + "/api/oauth2/token";
        public string UserEndpoint => Origin + "/api/v10/users/@me";
        public string RevokeEndpoint => Origin + "/api/oauth2/token/revoke";

        public enum Endpoint
        {
            Token,
            User,
            Revoke,
            Unknown
        }

        public sealed record Call(Endpoint Endpoint, string Method, string Address, string? Authorization, string UserAgent, IReadOnlyDictionary<string, string> Form);

        // One authorization, from the code Discord would have sent to the browser to its tokens
        sealed class Grant(string challenge, TestUser user)
        {
            public string Challenge { get; } = challenge;
            public TestUser User { get; } = user;
            public bool CodeUsed { get; set; }
            public bool Revoked { get; set; }
        }

        readonly Lock gate = new();
        readonly Dictionary<string, Grant> byCode = [];
        readonly Dictionary<string, Grant> byToken = [];
        readonly List<Call> calls = [];
        readonly List<string> secrets = [];
        readonly List<string> refusals = [];

        // The scope the token endpoint says it granted
        public string GrantedScope { get; set; } = "identify";

        // An endpoint that answers with this status instead, or never answers until the caller gives up
        public ConcurrentDictionary<Endpoint, HttpStatusCode> Failures { get; } = new();
        public ConcurrentDictionary<Endpoint, bool> Hangs { get; } = new();

        // What a failure answers with: OAuth's standard error by default, and a cookie of Discord's own if a test sets one
        public string FailureBody { get; set; } = """{"error":"server_error"}""";
        public string? FailureCookie { get; set; }

        public IReadOnlyList<Call> Calls
        {
            get
            {
                lock (gate)
                    return [.. calls];
            }
        }

        public IReadOnlyList<Call> CallsTo(Endpoint endpoint) => Calls.Where(call => call.Endpoint == endpoint).ToList();

        // Every code, token and verifier it handed out or was sent, for the tests that look for leaks
        public IReadOnlyList<string> Secrets
        {
            get
            {
                lock (gate)
                    return [.. secrets];
            }
        }

        // Why it refused a request, for a test's message
        public IReadOnlyList<string> Refusals
        {
            get
            {
                lock (gate)
                    return [.. refusals];
            }
        }

        // What Discord does when the user approves: a code for the browser, tied to the PKCE challenge it was sent
        public string Expect(string codeChallenge, TestUser user)
        {
            var code = NewSecret();

            lock (gate)
            {
                byCode[code] = new Grant(codeChallenge, user);
                secrets.Add(code);
            }

            return code;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var address = request.RequestUri?.AbsoluteUri ?? "";
            var endpoint = address == TokenEndpoint ? Endpoint.Token
                : address == UserEndpoint ? Endpoint.User
                : address == RevokeEndpoint ? Endpoint.Revoke
                : Endpoint.Unknown;

            var form = request.Content == null
                ? new Dictionary<string, string>()
                : QueryHelpers.ParseQuery(await request.Content.ReadAsStringAsync(cancellationToken)).ToDictionary(field => field.Key, field => field.Value.ToString());

            lock (gate)
                calls.Add(new Call(endpoint, request.Method.Method, address, request.Headers.Authorization?.ToString(), request.Headers.UserAgent.ToString(), form));

            if (endpoint == Endpoint.Unknown)
                throw new InvalidOperationException($"The fake Discord has no endpoint at {request.Method} {address}, so nothing was sent");

            if (Hangs.ContainsKey(endpoint))
                await Task.Delay(Timeout.Infinite, cancellationToken);

            if (Failures.TryGetValue(endpoint, out var status))
            {
                var failure = Json(status, FailureBody);

                if (FailureCookie != null)
                    failure.Headers.Add("Set-Cookie", FailureCookie);

                return failure;
            }

            return endpoint switch
            {
                Endpoint.Token when request.Method == HttpMethod.Post => Token(form),
                Endpoint.User when request.Method == HttpMethod.Get => User(request),
                Endpoint.Revoke when request.Method == HttpMethod.Post => Revoke(request, form),
                _ => Json(HttpStatusCode.MethodNotAllowed, """{"message":"405: Method Not Allowed","code":0}""")
            };
        }

        // The code exchange, with the PKCE verifier checked against the challenge the browser carried
        HttpResponseMessage Token(Dictionary<string, string> form)
        {
            lock (gate)
            {
                if (form.GetValueOrDefault("code_verifier") is { Length: > 0 } verifier)
                    secrets.Add(verifier);

                if (form.GetValueOrDefault("grant_type") != "authorization_code")
                    return Refuse("grant_type isn't authorization_code");

                if (form.GetValueOrDefault("client_id") != clientId || form.GetValueOrDefault("client_secret") != clientSecret)
                    return Refuse("the client ID or secret is wrong");

                if (form.GetValueOrDefault("redirect_uri") != redirectUri)
                    return Refuse("the redirect_uri isn't the registered one");

                if (!byCode.TryGetValue(form.GetValueOrDefault("code") ?? "", out var grant) || grant.CodeUsed)
                    return Refuse("the code is unknown or was used before");

                grant.CodeUsed = true;

                if (Challenge(form.GetValueOrDefault("code_verifier") ?? "") != grant.Challenge)
                    return Refuse("the code_verifier doesn't match the challenge");

                var accessToken = NewSecret();
                var refreshToken = NewSecret();
                byToken[accessToken] = grant;
                secrets.Add(accessToken);
                secrets.Add(refreshToken);

                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    access_token = accessToken,
                    token_type = "Bearer",
                    expires_in = 604800,
                    refresh_token = refreshToken,
                    scope = GrantedScope
                }));
            }
        }

        HttpResponseMessage User(HttpRequestMessage request)
        {
            lock (gate)
            {
                var authorization = request.Headers.Authorization;

                if (authorization?.Scheme != "Bearer" || !byToken.TryGetValue(authorization.Parameter ?? "", out var grant) || grant.Revoked)
                    return Json(HttpStatusCode.Unauthorized, """{"message":"401: Unauthorized","code":0}""");

                var user = new Dictionary<string, object?>
                {
                    ["id"] = grant.User.IdText,
                    ["username"] = grant.User.UserName,
                    ["global_name"] = grant.User.GlobalName,
                    ["discriminator"] = "0"
                };

                if (grant.User.MfaEnabled is { } mfa)
                    user["mfa_enabled"] = mfa;

                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(user));
            }
        }

        // Discord revokes the whole grant, refresh token included, and answers 200 even for a token it doesn't know
        HttpResponseMessage Revoke(HttpRequestMessage request, Dictionary<string, string> form)
        {
            lock (gate)
            {
                var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes(clientId + ":" + clientSecret));

                if (request.Headers.Authorization?.Scheme != "Basic" || request.Headers.Authorization.Parameter != expected)
                    return Json(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""");

                if (form.GetValueOrDefault("token_type_hint") != "access_token")
                    return Refuse("token_type_hint isn't access_token");

                if (byToken.TryGetValue(form.GetValueOrDefault("token") ?? "", out var grant))
                    grant.Revoked = true;

                return Json(HttpStatusCode.OK, "{}");
            }
        }

        // Whether a token was revoked, for the tests
        public bool IsRevoked(string accessToken)
        {
            lock (gate)
                return byToken.TryGetValue(accessToken, out var grant) && grant.Revoked;
        }

        HttpResponseMessage Refuse(string reason)
        {
            refusals.Add(reason);
            return Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        }

        static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

        static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(24));

        static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
