using System.Buffers.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Tests.Support;
using static MinerBot.Dashboard.Security.CloudflareAccessKeys;

namespace MinerBot.Dashboard.Tests
{
    // Cloudflare's keys as the dashboard keeps them: fetched on first need, kept fresh for an hour, fetched at most once
    // every 30 seconds and one fetch at a time, usable for a day while fetches fail, and never usable when none is good.
    // The app's own registrations, with the fake Cloudflare and a fake clock.
    public class CloudflareAccessKeysTests(AccessTestKeys keys) : IClassFixture<AccessTestKeys>
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        // Short, so the hanging cases end quickly
        static readonly CloudflareAccessLimits Short = new() { FetchLimit = TimeSpan.FromSeconds(1), WaitLimit = TimeSpan.FromSeconds(2) };

        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

        sealed class Run(FakeCloudflareAccess access, FakeClock clock, ServiceProvider provider, RecordingLoggerProvider logs) : IDisposable
        {
            public FakeCloudflareAccess Access => access;
            public FakeClock Clock => clock;
            public RecordingLoggerProvider Logs => logs;
            public CloudflareAccessKeys Keys => provider.GetRequiredService<CloudflareAccessKeys>();

            public async Task<KeyLookup> LookupAsync(string kid) => (await Keys.FindAsync(kid, Token).WaitAsync(WaitLimit, Token)).Lookup;

            public void Dispose() => provider.Dispose();
        }

        Run Start(CloudflareAccessLimits? limits = null)
        {
            var access = new FakeCloudflareAccess(keys);
            var clock = new FakeClock();
            var logs = new RecordingLoggerProvider();
            access.Clock = clock;

            return new Run(access, clock, access.Services(clock, limits ?? Short, logs), logs);
        }

        [Fact]
        public async Task NothingIsFetchedUntilFirstNeed()
        {
            using var run = Start();

            _ = run.Keys;
            Assert.Equal(0, run.Access.Fetches);

            Assert.Equal(KeyLookup.Found, await run.LookupAsync(run.Access.CurrentKeyId));
            Assert.Equal(1, run.Access.Fetches);
            Assert.Contains(run.Logs.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("signing keys changed, and 1 are usable now"));
        }

        // Reused for an hour, and fetched again by the first request after
        [Fact]
        public async Task FreshKeysAreReusedForAnHourAndFetchedAgainAfter()
        {
            using var run = Start();
            var kid = run.Access.CurrentKeyId;

            Assert.Equal(KeyLookup.Found, await run.LookupAsync(kid));
            run.Clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));
            Assert.Equal(KeyLookup.Found, await run.LookupAsync(kid));
            Assert.Equal(1, run.Access.Fetches);

            run.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(KeyLookup.Found, await run.LookupAsync(kid));
            Assert.Equal(2, run.Access.Fetches);

            // The same keys again change nothing worth a line
            Assert.Single(run.Logs.Entries, entry => entry.Message.Contains("signing keys changed"));
        }

        // A token naming a key Cloudflare doesn't have fetches once, then not again for 30 seconds, whatever asks
        [Fact]
        public async Task UnknownKeyIdFetchesAtMostOnceEvery30Seconds()
        {
            using var run = Start();

            Assert.Equal(KeyLookup.Unknown, await run.LookupAsync("made-up-key"));
            Assert.Equal(1, run.Access.Fetches);

            Assert.Equal(KeyLookup.Unknown, await run.LookupAsync("made-up-key"));
            Assert.Equal(KeyLookup.Unknown, await run.LookupAsync("another-made-up-key"));
            run.Clock.Advance(TimeSpan.FromSeconds(29));
            Assert.Equal(KeyLookup.Unknown, await run.LookupAsync("made-up-key"));
            Assert.Equal(1, run.Access.Fetches);

            run.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(KeyLookup.Unknown, await run.LookupAsync("made-up-key"));
            Assert.Equal(2, run.Access.Fetches);

            // Known keys are still found without a fetch meanwhile
            Assert.Equal(KeyLookup.Found, await run.LookupAsync(run.Access.CurrentKeyId));
            Assert.Equal(2, run.Access.Fetches);
        }

        // The 30 seconds count from the last fetch, good or not, and hold for old keys too
        [Fact]
        public async Task FailedFetchAlsoWaits30SecondsBeforeTheNext()
        {
            using var run = Start();
            run.Access.FailWith(AccessFailure.ServerError);

            Assert.Equal(KeyLookup.Unavailable, await run.LookupAsync(run.Access.CurrentKeyId));
            run.Access.FailWith(AccessFailure.None);
            run.Clock.Advance(TimeSpan.FromSeconds(29));
            Assert.Equal(KeyLookup.Unavailable, await run.LookupAsync(run.Access.CurrentKeyId));
            Assert.Equal(1, run.Access.Fetches);

            run.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(KeyLookup.Found, await run.LookupAsync(run.Access.CurrentKeyId));
            Assert.Equal(2, run.Access.Fetches);
        }

        [Fact]
        public async Task RotationToANewKeyIsPickedUp()
        {
            using var run = Start();

            Assert.Equal(KeyLookup.Found, await run.LookupAsync(run.Access.CurrentKeyId));
            run.Access.Rotate();
            run.Clock.Advance(TimeSpan.FromSeconds(30));

            Assert.Equal(KeyLookup.Found, await run.LookupAsync(run.Access.NextKeyId));
            Assert.Equal(KeyLookup.Found, await run.LookupAsync(run.Access.CurrentKeyId));
            Assert.Equal(2, run.Access.Fetches);
            Assert.Contains(run.Logs.Entries, entry => entry.Message.Contains("signing keys changed, and 2 are usable now"));

            // Each key is its own, found by its ID alone
            var (_, current) = await run.Keys.FindAsync(run.Access.CurrentKeyId, Token);
            var (_, next) = await run.Keys.FindAsync(run.Access.NextKeyId, Token);
            Assert.Equal(keys.Signer.ExportSubjectPublicKeyInfo(), current!.ExportSubjectPublicKeyInfo());
            Assert.Equal(keys.Next.ExportSubjectPublicKeyInfo(), next!.ExportSubjectPublicKeyInfo());
        }

        // Requests that arrive together wait for the one fetch that runs. Fails if fetches can run side by side.
        [Fact]
        public async Task TwentyParallelUnknownKeyIdsMakeOneFetch()
        {
            using var run = Start(new CloudflareAccessLimits());
            run.Access.Delay = TimeSpan.FromMilliseconds(300);

            var lookups = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => run.LookupAsync("made-up-key-" + i), Token)));

            Assert.All(lookups, lookup => Assert.Equal(KeyLookup.Unknown, lookup));
            Assert.Equal(1, run.Access.Fetches);

            // And with keys already there
            run.Clock.Advance(TimeSpan.FromSeconds(30));
            lookups = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => run.LookupAsync("other-made-up-key-" + i), Token)));

            Assert.All(lookups, lookup => Assert.Equal(KeyLookup.Unknown, lookup));
            Assert.Equal(2, run.Access.Fetches);
        }

        // A request that finds a fetch running waits for it at most WaitLimit, then answers from the keys there are
        [Fact]
        public async Task WaitForARunningFetchIsLimited()
        {
            using var run = Start(new CloudflareAccessLimits { FetchLimit = TimeSpan.FromSeconds(6), WaitLimit = TimeSpan.FromSeconds(1) });
            run.Access.Hang = true;

            var first = Task.Run(() => run.LookupAsync(run.Access.CurrentKeyId), Token);
            var waited = Stopwatch.StartNew();

            while (run.Access.Fetches == 0 && waited.Elapsed < WaitLimit)
                await Task.Delay(20, Token);

            waited.Restart();
            Assert.Equal(KeyLookup.Unavailable, await run.LookupAsync(run.Access.CurrentKeyId));
            var second = waited.Elapsed;

            Assert.False(first.IsCompleted, "The first lookup ended before its fetch's limit");
            Assert.True(second < TimeSpan.FromSeconds(4), $"The second lookup waited {second}");
            Assert.Equal(KeyLookup.Unavailable, await first);
            Assert.Equal(1, run.Access.Fetches);
        }

        // Cloudflare unreachable for a while: the keys of the last good fetch are used for 24 hours after it, then nothing is
        [Fact]
        public async Task AfterAFailureOldKeysWorkFor24HoursThenUnavailable()
        {
            using var run = Start();
            var kid = run.Access.CurrentKeyId;

            Assert.Equal(KeyLookup.Found, await run.LookupAsync(kid));
            run.Access.FailWith(AccessFailure.Throw);

            run.Clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(KeyLookup.Found, await run.LookupAsync(kid));
            Assert.Equal(2, run.Access.Fetches);
            Assert.Contains(run.Logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message == "Cloudflare Access's signing keys couldn't be fetched: HttpRequestException");

            run.Clock.Advance(TimeSpan.FromHours(23) - TimeSpan.FromSeconds(1));
            Assert.Equal(KeyLookup.Found, await run.LookupAsync(kid));

            run.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(KeyLookup.Unavailable, await run.LookupAsync(kid));
            Assert.Equal(KeyLookup.Unavailable, await run.LookupAsync("made-up-key"));

            // Back again: one fetch and the keys are usable at once
            run.Access.FailWith(AccessFailure.None);
            run.Clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(KeyLookup.Found, await run.LookupAsync(kid));
        }

        // Anything but 200 with usable keys is a failure, and with nothing kept from before the lookup fails closed, within
        // the limits
        [Theory]
        [InlineData(AccessFailure.Throw, "HttpRequestException")]
        [InlineData(AccessFailure.ServerError, "HTTP 500")]
        [InlineData(AccessFailure.Redirect, "HTTP 302")]
        [InlineData(AccessFailure.NotOk, "HTTP 203")]
        // System.Text.Json's own subclass of JsonException
        [InlineData(AccessFailure.NotJson, "Json")]
        [InlineData(AccessFailure.TooBig, "HttpRequestException")]
        [InlineData(AccessFailure.NoKeys, "no usable key")]
        public async Task FirstFetchThatFailsGivesUnavailable(AccessFailure failure, string logged)
        {
            using var run = Start();
            run.Access.FailWith(failure);
            var watch = Stopwatch.StartNew();

            Assert.Equal(KeyLookup.Unavailable, await run.LookupAsync(run.Access.CurrentKeyId));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The lookup took {watch.Elapsed}");
            Assert.Contains(run.Logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.StartsWith("Cloudflare Access's signing keys couldn't be fetched: " + logged, StringComparison.Ordinal));
        }

        // A fetch that never answers ends at FetchLimit, shortened here to a second
        [Fact]
        public async Task FirstFetchThatHangsGivesUnavailableWithinTheLimit()
        {
            using var run = Start();
            run.Access.Hang = true;
            var watch = Stopwatch.StartNew();

            Assert.Equal(KeyLookup.Unavailable, await run.LookupAsync(run.Access.CurrentKeyId));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"The lookup took {watch.Elapsed}");
            Assert.Contains(run.Logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.StartsWith("Cloudflare Access's signing keys couldn't be fetched: Task", StringComparison.Ordinal));
        }

        // Only RSA signing keys of at least 2048 bits with an ID, for RS256, are kept. Each of these is left out on its own.
        public static TheoryData<string> UnusableKeys => new()
        {
            "not RSA",
            "1024 bits",
            "1024 bits after zero bytes",
            "2040 bits after a zero byte",
            "use enc",
            "use null",
            "use a number",
            "use true",
            "use a list",
            "alg RS512",
            "alg null",
            "alg a number",
            "no kid",
            "empty kid",
            "kid of 129 characters",
            "no modulus",
            "not base64url",
            "no exponent",
            "empty exponent"
        };

        Dictionary<string, object?> Unusable(string which) => which switch
        {
            "not RSA" => FakeCloudflareAccess.Jwk("key", keys.Signer, kty: "EC"),
            "1024 bits" => FakeCloudflareAccess.Jwk("key", keys.Weak),
            // Leading zero bytes don't count: the weak key written in 256 bytes, and 255 bytes whose first bit is set
            // written after one zero byte
            "1024 bits after zero bytes" => With(FakeCloudflareAccess.Jwk("key", keys.Weak), "n", Base64Url.EncodeToString([.. new byte[128], .. Modulus(keys.Weak)])),
            "2040 bits after a zero byte" => With(FakeCloudflareAccess.Jwk("key", keys.Signer), "n", Base64Url.EncodeToString([0, (byte)(Modulus(keys.Signer)[1] | 0x80), .. Modulus(keys.Signer)[2..]])),
            "use enc" => FakeCloudflareAccess.Jwk("key", keys.Signer, use: "enc"),
            // There, but not a string, so neither sig nor absent
            "use null" => With(FakeCloudflareAccess.Jwk("key", keys.Signer), "use", null),
            "use a number" => With(FakeCloudflareAccess.Jwk("key", keys.Signer), "use", 1),
            "use true" => With(FakeCloudflareAccess.Jwk("key", keys.Signer), "use", true),
            "use a list" => With(FakeCloudflareAccess.Jwk("key", keys.Signer), "use", new[] { "sig" }),
            "alg RS512" => FakeCloudflareAccess.Jwk("key", keys.Signer, alg: "RS512"),
            // There, but not a string, so neither RS256 nor absent
            "alg null" => With(FakeCloudflareAccess.Jwk("key", keys.Signer), "alg", null),
            "alg a number" => With(FakeCloudflareAccess.Jwk("key", keys.Signer), "alg", 256),
            "no kid" => FakeCloudflareAccess.Jwk(null, keys.Signer),
            "empty kid" => FakeCloudflareAccess.Jwk("", keys.Signer),
            "kid of 129 characters" => FakeCloudflareAccess.Jwk(new string('k', MaxKeyIdLength + 1), keys.Signer),
            "no modulus" => Without(FakeCloudflareAccess.Jwk("key", keys.Signer), "n"),
            "not base64url" => With(FakeCloudflareAccess.Jwk("key", keys.Signer), "n", "@@@@"),
            "no exponent" => Without(FakeCloudflareAccess.Jwk("key", keys.Signer), "e"),
            "empty exponent" => With(FakeCloudflareAccess.Jwk("key", keys.Signer), "e", ""),
            _ => throw new ArgumentOutOfRangeException(nameof(which))
        };

        static Dictionary<string, object?> Without(Dictionary<string, object?> jwk, string name)
        {
            jwk.Remove(name);
            return jwk;
        }

        // Jwk leaves a null field out, so this is the only way to write one as null
        static Dictionary<string, object?> With(Dictionary<string, object?> jwk, string name, object? value)
        {
            jwk[name] = value;
            return jwk;
        }

        static byte[] Modulus(RSA key) => key.ExportParameters(false).Modulus!;

        static string Document(params object?[] list) => JsonSerializer.Serialize(new Dictionary<string, object> { ["keys"] = list });

        [Theory]
        [MemberData(nameof(UnusableKeys))]
        public async Task UnusableKeyIsLeftOut(string which)
        {
            var good = FakeCloudflareAccess.Jwk("good", keys.Signer);

            Assert.Empty(Parse(Encoding.UTF8.GetBytes(Document(Unusable(which)))));
            Assert.Equal(["good"], Parse(Encoding.UTF8.GetBytes(Document(Unusable(which), good))).Keys);

            // A set with nothing usable in it is a failed fetch
            using var run = Start();
            run.Access.Document = Document(Unusable(which));

            Assert.Equal(KeyLookup.Unavailable, await run.LookupAsync("key"));
            Assert.Contains(run.Logs.Entries, entry => entry.Message == "Cloudflare Access's signing keys couldn't be fetched: no usable key");
        }

        // alg and use may be left out, and an ID of 128 characters is still one
        [Fact]
        public void KeysWithoutAlgOrUseAreKept()
        {
            var longest = new string('k', MaxKeyIdLength);
            var found = Parse(Encoding.UTF8.GetBytes(Document(
                FakeCloudflareAccess.Jwk("plain", keys.Signer, alg: null, use: null),
                FakeCloudflareAccess.Jwk(longest, keys.Next))));

            Assert.Equal(new[] { "plain", longest }.Order(StringComparer.Ordinal), found.Keys.Order(StringComparer.Ordinal));
        }

        // The first of two keys with one ID is the one kept, so a lookup always finds the same key
        [Fact]
        public void FirstKeyWithAnIdWins()
        {
            var found = Parse(Encoding.UTF8.GetBytes(Document(FakeCloudflareAccess.Jwk("same", keys.Signer), FakeCloudflareAccess.Jwk("same", keys.Next))));

            Assert.Equal(keys.Signer.ExportSubjectPublicKeyInfo(), Assert.Single(found).Value.ExportSubjectPublicKeyInfo());
        }

        [Fact]
        public void DocumentThatIsNotJsonThrows()
        {
            Assert.ThrowsAny<JsonException>(() => Parse("<html>"u8));
            Assert.Empty(Parse("""{"keys":{}}"""u8));
            Assert.Empty(Parse("[]"u8));
        }

        // An entry of the list that isn't an object is left out, and the keys beside it are still kept, so one odd entry
        // can't cost the whole set
        [Fact]
        public async Task EntryThatIsNotAnObjectIsLeftOut()
        {
            var good = FakeCloudflareAccess.Jwk("good", keys.Signer);

            Assert.Equal(["good"], Parse(Encoding.UTF8.GetBytes(Document(null, "x", 5, true, Array.Empty<object>(), good))).Keys);

            using var run = Start();
            run.Access.Document = Document(null, FakeCloudflareAccess.Jwk(run.Access.CurrentKeyId, keys.Signer));

            Assert.Equal(KeyLookup.Found, await run.LookupAsync(run.Access.CurrentKeyId));
            Assert.DoesNotContain(run.Logs.Entries, entry => entry.Level == LogLevel.Warning);
        }

        // A 2048-bit key written after a zero byte, as RFC 7518 says some libraries write one, is kept as itself
        [Fact]
        public void FullKeyAfterAZeroByteIsKept()
        {
            var found = Parse(Encoding.UTF8.GetBytes(Document(With(FakeCloudflareAccess.Jwk("key", keys.Signer), "n", Base64Url.EncodeToString([0, .. Modulus(keys.Signer)])))));

            Assert.Equal(keys.Signer.ExportSubjectPublicKeyInfo(), Assert.Single(found).Value.ExportSubjectPublicKeyInfo());
        }

        // A key this platform's RSA refuses to import is left out on its own, and the rest of the document is kept. Windows
        // refuses a public exponent of more than 8 bytes, and OpenSSL 3's import check refuses e = 1, so each platform has one
        // here that it refuses.
        [Fact]
        public async Task KeyThePlatformRefusesCostsOnlyItself()
        {
            var document = Document(
                With(FakeCloudflareAccess.Jwk("long-exponent", keys.Signer), "e", Base64Url.EncodeToString(Enumerable.Repeat((byte)0xFF, 300).ToArray())),
                With(FakeCloudflareAccess.Jwk("exponent-one", keys.Signer), "e", Base64Url.EncodeToString([1])),
                FakeCloudflareAccess.Jwk("good", keys.Next));

            Assert.Contains("good", Parse(Encoding.UTF8.GetBytes(document)).Keys);

            // And a fetch of that document is a good one
            using var run = Start();
            run.Access.Document = document;

            Assert.Equal(KeyLookup.Found, await run.LookupAsync("good"));
            Assert.DoesNotContain(run.Logs.Entries, entry => entry.Level == LogLevel.Warning);
        }

        // The app's own limits, as AddCloudflareAccess registers them, since every other test here passes its own. A request
        // that finds a fetch running must wait longer than that whole fetch can take, or a second admin request during a
        // slow first fetch gets the 503.
        [Fact]
        public void AppLimitsAreTheDesignedOnes()
        {
            using var provider = new ServiceCollection().AddLogging().AddCloudflareAccess().BuildServiceProvider();
            var limits = provider.GetRequiredService<CloudflareAccessLimits>();

            Assert.Equal(TimeSpan.FromSeconds(10), limits.FetchLimit);
            Assert.Equal(TimeSpan.FromSeconds(12), limits.WaitLimit);
            Assert.Equal(TimeSpan.FromHours(1), limits.FreshFor);
            Assert.Equal(TimeSpan.FromHours(24), limits.UsableFor);
            Assert.Equal(TimeSpan.FromSeconds(30), limits.MinRefetch);
            Assert.Equal(64 * 1024, limits.MaxResponseBytes);
            Assert.True(limits.WaitLimit > limits.FetchLimit, $"A request waits {limits.WaitLimit} for a fetch that may take {limits.FetchLimit}");
        }

        // Only to Cloudflare: no redirects, no proxy and no cookies, and a short connect limit
        [Fact]
        public void HandlerFollowsNoRedirectAndUsesNoProxyOrCookies()
        {
            using var handler = CreateHandler();

            Assert.False(handler.AllowAutoRedirect);
            Assert.False(handler.UseProxy);
            Assert.False(handler.UseCookies);
            Assert.Equal(TimeSpan.FromSeconds(5), handler.ConnectTimeout);
        }

        // The app's own client is built on that handler, with nothing in front of it but the client factory's own
        [Fact]
        public void AppClientUsesThatHandler()
        {
            using var provider = new ServiceCollection().AddLogging().AddCloudflareAccess().BuildServiceProvider();
            HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ClientName);

            while (handler is DelegatingHandler delegating)
                handler = delegating.InnerHandler!;

            var sockets = Assert.IsType<SocketsHttpHandler>(handler);
            Assert.False(sockets.AllowAutoRedirect);
            Assert.False(sockets.UseProxy);
            Assert.False(sockets.UseCookies);
            Assert.Equal(TimeSpan.FromSeconds(5), sockets.ConnectTimeout);
        }

        // The app's own client has the fetch's limit and the 64 KB cap, from the limits
        [Fact]
        public void ClientHasTheLimits()
        {
            using var provider = new FakeCloudflareAccess(keys).Services(new FakeClock());
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

            Assert.Equal(TimeSpan.FromSeconds(10), client.Timeout);
            Assert.Equal(64 * 1024, client.MaxResponseContentBufferSize);
        }
    }
}
