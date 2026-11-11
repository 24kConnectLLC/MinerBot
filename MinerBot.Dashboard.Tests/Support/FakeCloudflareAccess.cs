using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Settings;

namespace MinerBot.Dashboard.Tests.Support
{
    // RSA keys for the Access tests, made once per test class, because a 2048-bit key takes a moment to make
    public sealed class AccessTestKeys : IDisposable
    {
        // What the fake Cloudflare signs with, and the one it changes to when it rotates
        public RSA Signer { get; } = RSA.Create(2048);
        public RSA Next { get; } = RSA.Create(2048);

        // Someone else's, for signatures that must fail
        public RSA Other { get; } = RSA.Create(2048);

        // Too short to be kept
        public RSA Weak { get; } = RSA.Create(1024);

        public void Dispose()
        {
            Signer.Dispose();
            Next.Dispose();
            Other.Dispose();
            Weak.Dispose();
        }
    }

    // How the fake key server fails, when a test asks it to. Public, because a theory takes it.
    public enum AccessFailure
    {
        None,
        Throw,
        ServerError,
        Redirect,
        NotOk,
        NotJson,
        TooBig,
        NoKeys
    }

    // Cloudflare Access for one team, in process: the certs endpoint the dashboard fetches its keys from, and tokens signed
    // the way Cloudflare signs them, at the time of the clock it's given. Any other address throws, so a test that forgot
    // the fake can't reach the network. Every fetch is counted, and every token it made is kept for the leak tests.
    internal sealed class FakeCloudflareAccess(AccessTestKeys keys) : HttpMessageHandler
    {
        public const string TeamDomain = "test-team.cloudflareaccess.com";
        public const string Issuer = "https://" + TeamDomain;
        public const string CertsAddress = Issuer + "/cdn-cgi/access/certs";

        // Like Cloudflare's, 64 hex characters, made up for each fake
        public string CurrentKeyId { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        public string NextKeyId { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        public string Audience { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        readonly Lock gate = new();
        readonly List<string> minted = [];
        bool rotated;
        int fetches;

        // The time tokens are made at, which is the app's clock in the tests that start it
        public TimeProvider Clock { get; set; } = TimeProvider.System;

        public AccessFailure Failure { get; set; }

        // Never answers until the caller gives up
        public bool Hang { get; set; }

        // How long each answer takes, so requests that arrive together overlap
        public TimeSpan Delay { get; set; }

        // Served as it is instead of the keys, for the tests of which keys are kept
        public string? Document { get; set; }

        public int Fetches => Volatile.Read(ref fetches);

        public IReadOnlyList<string> Minted
        {
            get
            {
                lock (gate)
                    return [.. minted];
            }
        }

        // As Cloudflare does: the new key is served beside the old one, which it keeps for a while
        public void Rotate()
        {
            lock (gate)
                rotated = true;
        }

        public void FailWith(AccessFailure failure) => Failure = failure;

        // The dashboard's Access client goes to this fake. Added after the factory's own, so it wins over its Discord
        // defaults, and the limits given replace the real ones.
        public void Install(IServiceCollection services, CloudflareAccessLimits? limits = null)
        {
            services.AddHttpClient(CloudflareAccessKeys.ClientName).ConfigurePrimaryHttpMessageHandler(() => new NonDisposingHandler(this));

            if (limits != null)
                services.AddSingleton(limits);
        }

        // Only the Access part of the app, for the tests that need no web host: the real registrations, with this fake
        // as Cloudflare and the given clock and limits
        public ServiceProvider Services(TimeProvider clock, CloudflareAccessLimits? limits = null, ILoggerProvider? logs = null)
        {
            var services = new ServiceCollection();
            services.AddLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Debug);

                if (logs != null)
                    logging.AddProvider(logs);
            });
            services.AddSingleton(clock);
            services.AddOptions<CloudflareAccessOptions>().Configure(options =>
            {
                options.TeamDomain = TeamDomain;
                options.Audience = Audience;
            });
            services.AddCloudflareAccess();
            Install(services, limits);

            return services.BuildServiceProvider();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri?.AbsoluteUri != CertsAddress)
                throw new InvalidOperationException($"The fake Cloudflare has nothing at {request.Method} {request.RequestUri}, so nothing was sent");

            Interlocked.Increment(ref fetches);

            if (Hang)
                await Task.Delay(Timeout.Infinite, cancellationToken);

            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);

            return Failure switch
            {
                AccessFailure.Throw => throw new HttpRequestException("The fake Cloudflare refused the connection"),
                AccessFailure.ServerError => Answer(HttpStatusCode.InternalServerError, """{"error":"internal"}"""),
                AccessFailure.Redirect => Redirect(),
                // A good document, but not with 200
                AccessFailure.NotOk => Answer(HttpStatusCode.NonAuthoritativeInformation, KeysDocument()),
                AccessFailure.NotJson => Answer(HttpStatusCode.OK, "<html><body>Not the keys</body></html>", "text/html"),
                // A good document, padded past the 64 KB cap
                AccessFailure.TooBig => Answer(HttpStatusCode.OK, KeysDocument()[..^1] + ",\"padding\":\"" + new string('a', 70 * 1024) + "\"}"),
                AccessFailure.NoKeys => Answer(HttpStatusCode.OK, """{"keys":[]}"""),
                _ => Answer(HttpStatusCode.OK, Document ?? KeysDocument())
            };
        }

        // The keys as Cloudflare's certs document lists them
        public string KeysDocument()
        {
            bool both;

            lock (gate)
                both = rotated;

            var list = both ? new[] { Jwk(CurrentKeyId, keys.Signer), Jwk(NextKeyId, keys.Next) } : [Jwk(CurrentKeyId, keys.Signer)];
            return JsonSerializer.Serialize(new Dictionary<string, object> { ["keys"] = list });
        }

        // One key as Cloudflare lists it. A null field is left out.
        public static Dictionary<string, object?> Jwk(string? kid, RSA key, string? alg = "RS256", string? use = "sig", string kty = "RSA")
        {
            var parameters = key.ExportParameters(false);
            var jwk = new Dictionary<string, object?>
            {
                ["kid"] = kid,
                ["kty"] = kty,
                ["alg"] = alg,
                ["use"] = use,
                ["e"] = Base64Url.EncodeToString(parameters.Exponent),
                ["n"] = Base64Url.EncodeToString(parameters.Modulus)
            };

            foreach (var name in jwk.Where(field => field.Value == null).Select(field => field.Key).ToList())
                jwk.Remove(name);

            return jwk;
        }

        // A token as Cloudflare makes one: RS256 with the current key, for this team and audience, valid from now for ten
        // minutes. A test changes the header or the claims, or signs with another key or hash, to break one rule.
        public string Mint(Action<Dictionary<string, object?>>? claims = null, Action<Dictionary<string, object?>>? header = null, RSA? signWith = null,
            HashAlgorithmName? hash = null)
        {
            var now = Clock.GetUtcNow().ToUnixTimeSeconds();
            var head = new Dictionary<string, object?> { ["alg"] = "RS256", ["kid"] = CurrentKeyId, ["typ"] = "JWT" };
            var payload = new Dictionary<string, object?>
            {
                ["aud"] = new[] { Audience },
                ["email"] = "owner@example.test",
                ["exp"] = now + 600,
                ["iat"] = now,
                ["nbf"] = now,
                ["iss"] = Issuer,
                ["type"] = "app",
                ["sub"] = Guid.NewGuid().ToString()
            };

            header?.Invoke(head);
            claims?.Invoke(payload);

            return Keep(Signed(Encode(head) + "." + Encode(payload), signWith ?? keys.Signer, hash ?? HashAlgorithmName.SHA256));
        }

        // A token from parts the test wrote itself, signed with the current key or the one given
        public string MintRaw(string headerJson, string payloadJson, RSA? signWith = null) =>
            Keep(Signed(Base64Url.EncodeToString(Encoding.UTF8.GetBytes(headerJson)) + "." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payloadJson)), signWith ?? keys.Signer, HashAlgorithmName.SHA256));

        public static string Encode(object value) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(value));

        static string Signed(string text, RSA key, HashAlgorithmName hash) =>
            text + "." + Base64Url.EncodeToString(key.SignData(Encoding.ASCII.GetBytes(text), hash, RSASignaturePadding.Pkcs1));

        string Keep(string token)
        {
            lock (gate)
                minted.Add(token);

            return token;
        }

        static HttpResponseMessage Answer(HttpStatusCode status, string body, string mediaType = "application/json") =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

        static HttpResponseMessage Redirect()
        {
            var response = Answer(HttpStatusCode.Found, "");
            response.Headers.Location = new Uri("https://elsewhere.example.test/cdn-cgi/access/certs");
            return response;
        }
    }
}
