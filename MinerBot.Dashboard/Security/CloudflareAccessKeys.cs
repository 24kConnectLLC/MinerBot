using System.Buffers.Text;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Settings;

namespace MinerBot.Dashboard.Security
{
    // How long each wait and each set of keys lasts, kept together so the tests can shorten them
    internal sealed record CloudflareAccessLimits
    {
        // One fetch of the keys, from connecting to the end of the answer
        public TimeSpan FetchLimit { get; init; } = TimeSpan.FromSeconds(10);

        // How long a request waits for a fetch that's already running: a little longer than a whole fetch
        public TimeSpan WaitLimit { get; init; } = TimeSpan.FromSeconds(12);

        // Keys this old are fetched again by the next request that needs one
        public TimeSpan FreshFor { get; init; } = TimeSpan.FromHours(1);

        // While fetches fail, keys stay usable this long after the fetch that got them. Cloudflare changes its keys every
        // six weeks and keeps the old one for seven days after.
        public TimeSpan UsableFor { get; init; } = TimeSpan.FromHours(24);

        // The shortest time between two fetches, whatever asks for them, so tokens naming made-up keys can't turn the
        // dashboard into a load on Cloudflare
        public TimeSpan MinRefetch { get; init; } = TimeSpan.FromSeconds(30);

        // Cloudflare's answer is about 2 KB
        public int MaxResponseBytes { get; init; } = 64 * 1024;
    }

    // Cloudflare Access's signing keys for the team in the settings. They're fetched on first need, never at start, so the
    // dashboard starts whatever Cloudflare is doing. With no usable key the lookup says so, and the check fails closed.
    internal sealed class CloudflareAccessKeys(IHttpClientFactory clients, IOptions<CloudflareAccessOptions> settings, CloudflareAccessLimits limits,
        TimeProvider time, ILogger<CloudflareAccessKeys> logger)
    {
        public const string ClientName = "CloudflareAccess";

        // Cloudflare's key IDs are 64 hex characters, so a longer one than this isn't one of its
        internal const int MaxKeyIdLength = 128;

        // Cloudflare signs with 2048-bit RSA keys
        internal const int MinKeyBits = 2048;

        static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(5);

        public enum KeyLookup
        {
            Found,

            // There are keys, and none of them has that ID
            Unknown,

            // There are no usable keys, so nothing can be checked
            Unavailable
        }

        // The keys of one good fetch, and when it finished
        sealed record KeySet(IReadOnlyDictionary<string, RSA> Keys, DateTimeOffset FetchedAt);

        // One fetch at a time
        readonly SemaphoreSlim fetching = new(1, 1);

        // Replaced whole by a good fetch, and read without the semaphore. Keys that are replaced aren't disposed, because a
        // request may still be checking a signature with one; Cloudflare changes them every six weeks.
        volatile KeySet? keys;

        // When the last fetch started, good or not. Used only while holding the semaphore.
        DateTimeOffset? lastFetch;

        public async Task<(KeyLookup Lookup, RSA? Key)> FindAsync(string kid, CancellationToken cancellationToken)
        {
            // Fresh keys with that ID, without waiting for anything
            if (Fresh(keys) is { } fresh && fresh.Keys.TryGetValue(kid, out var key))
                return (KeyLookup.Found, key);

            // Old keys, or no key with that ID: fetch again, unless a fetch started less than MinRefetch ago. A request that
            // finds a fetch running waits for it, at most WaitLimit, and then answers from whatever keys there are.
            if (await fetching.WaitAsync(limits.WaitLimit, cancellationToken))
            {
                try
                {
                    var current = keys;
                    var now = time.GetUtcNow();

                    // Another request may have fetched the key while this one waited
                    var needed = !(Fresh(current) is { } set && set.Keys.ContainsKey(kid));

                    if (needed && (lastFetch is not { } last || now - last >= limits.MinRefetch))
                    {
                        lastFetch = now;
                        await FetchAsync(current);
                    }
                }
                finally
                {
                    fetching.Release();
                }
            }

            if (Usable(keys) is not { } usable)
                return (KeyLookup.Unavailable, null);

            return usable.Keys.TryGetValue(kid, out var found) ? (KeyLookup.Found, found) : (KeyLookup.Unknown, null);
        }

        KeySet? Fresh(KeySet? set) => set != null && time.GetUtcNow() - set.FetchedAt < limits.FreshFor ? set : null;

        KeySet? Usable(KeySet? set) => set != null && time.GetUtcNow() - set.FetchedAt < limits.UsableFor ? set : null;

        // One GET of the team's keys. A failure keeps the keys there are, usable until UsableFor runs out, and is logged
        // with the HTTP status or the exception's type only.
        async Task FetchAsync(KeySet? current)
        {
            string failure;

            try
            {
                // Its own limit, never the request's, so a visitor who goes away can't cut the fetch others are waiting for
                using var client = clients.CreateClient(ClientName);
                using var response = await client.GetAsync(settings.Value.CertsAddress, CancellationToken.None);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    failure = $"HTTP {(int)response.StatusCode}";
                }
                else
                {
                    var found = Parse(await response.Content.ReadAsByteArrayAsync(CancellationToken.None));

                    if (found.Count > 0)
                    {
                        keys = new KeySet(found, time.GetUtcNow());

                        if (current == null || !current.Keys.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(found.Keys))
                            logger.LogInformation("Cloudflare Access's signing keys changed, and {Count} are usable now", found.Count);

                        return;
                    }

                    failure = "no usable key";
                }
            }
            catch (Exception e)
            {
                // Such as a timeout, a refused connection, or an answer over MaxResponseBytes or not JSON
                failure = e.GetType().Name;
            }

            logger.LogWarning("Cloudflare Access's signing keys couldn't be fetched: {Failure}", failure);
        }

        // The signing keys in Cloudflare's certs document, by key ID. A key is kept only when it's RSA, its ID has at most
        // 128 characters, its alg is RS256 or absent, its use is sig or absent, and its modulus has at least 2048 bits.
        // Anything else in the document is left out. Throws JsonException when the document isn't JSON.
        internal static IReadOnlyDictionary<string, RSA> Parse(ReadOnlySpan<byte> json)
        {
            var found = new Dictionary<string, RSA>(StringComparer.Ordinal);
            using var document = JsonDocument.Parse(json.ToArray());
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("keys", out var list) || list.ValueKind != JsonValueKind.Array)
                return found;

            foreach (var jwk in list.EnumerateArray())
            {
                if (jwk.ValueKind != JsonValueKind.Object || CloudflareAccessToken.Text(jwk, "kty") != "RSA")
                    continue;

                // The first key with an ID wins, so a lookup by ID always finds the same one
                if (CloudflareAccessToken.Text(jwk, "kid") is not { Length: > 0 and <= MaxKeyIdLength } kid || found.ContainsKey(kid))
                    continue;

                if (jwk.TryGetProperty("alg", out _) && CloudflareAccessToken.Text(jwk, "alg") != "RS256")
                    continue;

                if (jwk.TryGetProperty("use", out _) && CloudflareAccessToken.Text(jwk, "use") != "sig")
                    continue;

                if (PublicKey(jwk) is { } key)
                    found[kid] = key;
            }

            return found;
        }

        static RSA? PublicKey(JsonElement jwk)
        {
            try
            {
                var modulus = Base64Url.DecodeFromChars(CloudflareAccessToken.Text(jwk, "n") ?? "");
                var exponent = Base64Url.DecodeFromChars(CloudflareAccessToken.Text(jwk, "e") ?? "");
                var start = Array.FindIndex(modulus, b => b != 0);

                // The bits from the first one onwards, so leading zero bytes don't count
                if (start < 0 || exponent.Length == 0 || (modulus.Length - start - 1) * 8 + 32 - BitOperations.LeadingZeroCount((uint)modulus[start]) < MinKeyBits)
                    return null;

                var rsa = RSA.Create();

                try
                {
                    rsa.ImportParameters(new RSAParameters { Modulus = modulus[start..], Exponent = exponent });
                    return rsa;
                }
                catch (CryptographicException)
                {
                    rsa.Dispose();
                    return null;
                }
            }
            catch (FormatException)
            {
                return null;
            }
        }

        // Only to Cloudflare: no redirects, no proxy and no cookies, and a connect limit inside the fetch's own
        public static SocketsHttpHandler CreateHandler() => new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = ConnectLimit
        };
    }

    internal static class CloudflareAccessServices
    {
        public static IServiceCollection AddCloudflareAccess(this IServiceCollection services)
        {
            services.TryAddSingleton(new CloudflareAccessLimits());
            services.TryAddSingleton<CloudflareAccessKeys>();

            // The fetch's whole limit and a cap on the answer, and no loggers, which would log each request's address and
            // headers
            services.AddHttpClient(CloudflareAccessKeys.ClientName, (provider, client) =>
                {
                    var limits = provider.GetRequiredService<CloudflareAccessLimits>();
                    client.Timeout = limits.FetchLimit;
                    client.MaxResponseContentBufferSize = limits.MaxResponseBytes;
                })
                .ConfigurePrimaryHttpMessageHandler(() => CloudflareAccessKeys.CreateHandler())
                .RemoveAllLoggers();

            return services;
        }
    }
}
