using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Tests.Support;
using static MinerBot.Dashboard.Security.CloudflareAccessToken;

namespace MinerBot.Dashboard.Tests
{
    // The token rules on their own, against the key the header names and a fixed time. Every rule has a case of its own,
    // and the reason each refusal gives is checked, so a rule that's dropped or moved fails here.
    public class AccessTokenTests : IClassFixture<AccessTestKeys>
    {
        readonly AccessTestKeys keys;
        readonly FakeClock clock = new();
        readonly FakeCloudflareAccess access;

        public AccessTokenTests(AccessTestKeys keys)
        {
            this.keys = keys;
            access = new FakeCloudflareAccess(keys) { Clock = clock };
        }

        long Now => clock.GetUtcNow().ToUnixTimeSeconds();

        // The whole check with the key the header named, as the middleware runs it after the lookup
        string? Check(string token) => Problem(token, keys.Signer, FakeCloudflareAccess.Issuer, access.Audience, clock.GetUtcNow());

        static string Raw(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

        [Fact]
        public void ValidTokenPasses()
        {
            var token = access.Mint();

            Assert.True(TryReadHeader(token, out var kid, out var problem));
            Assert.Null(problem);
            Assert.Equal(access.CurrentKeyId, kid);
            Assert.Null(Check(token));
        }

        // Cloudflare sends an array, and a single string is valid too. Only the configured AUD counts.
        [Fact]
        public void AudienceAsAStringOrInAnArrayPasses()
        {
            Assert.Null(Check(access.Mint(claims => claims["aud"] = access.Audience)));
            Assert.Null(Check(access.Mint(claims => claims["aud"] = new[] { new string('0', 64), access.Audience })));
        }

        [Fact]
        public void WrongAudienceIsRefused()
        {
            Assert.Equal(WrongAudience, Check(access.Mint(claims => claims["aud"] = new[] { new string('0', 64) })));
            Assert.Equal(WrongAudience, Check(access.Mint(claims => claims["aud"] = new string('0', 64))));
            Assert.Equal(WrongAudience, Check(access.Mint(claims => claims["aud"] = access.Audience.ToUpperInvariant())));
            Assert.Equal(WrongAudience, Check(access.Mint(claims => claims["aud"] = new object[] { 1, true })));
            Assert.Equal(WrongAudience, Check(access.Mint(claims => claims.Remove("aud"))));
        }

        [Fact]
        public void WrongIssuerIsRefused()
        {
            Assert.Equal(WrongIssuer, Check(access.Mint(claims => claims["iss"] = "https://evil-team.cloudflareaccess.com")));
            Assert.Equal(WrongIssuer, Check(access.Mint(claims => claims["iss"] = FakeCloudflareAccess.Issuer + "/")));
            Assert.Equal(WrongIssuer, Check(access.Mint(claims => claims["iss"] = FakeCloudflareAccess.TeamDomain)));
            Assert.Equal(WrongIssuer, Check(access.Mint(claims => claims.Remove("iss"))));
        }

        // A token is good while now < exp + 60 s: one that ended 59 seconds ago passes, 60 seconds ago doesn't
        [Fact]
        public void ExpiredIsRefusedAfterTheSkew()
        {
            Assert.Null(Check(access.Mint(claims => claims["exp"] = Now - 30)));
            Assert.Null(Check(access.Mint(claims => claims["exp"] = Now - 59)));
            Assert.Equal(Expired, Check(access.Mint(claims => claims["exp"] = Now - 60)));
            Assert.Equal(Expired, Check(access.Mint(claims => claims["exp"] = Now - 3600)));
        }

        // A token without an end would never expire
        [Fact]
        public void NoExpiryIsRefused()
        {
            Assert.Equal(Expired, Check(access.Mint(claims => claims.Remove("exp"))));
        }

        // Good once now + 60 s >= nbf: one that starts in 60 seconds passes, in 61 doesn't. Without nbf it's good at once.
        [Fact]
        public void NotValidYetIsRefusedBeyondTheSkew()
        {
            Assert.Null(Check(access.Mint(claims => claims["nbf"] = Now + 30)));
            Assert.Null(Check(access.Mint(claims => claims["nbf"] = Now + 60)));
            Assert.Equal(NotValidYet, Check(access.Mint(claims => claims["nbf"] = Now + 61)));
            Assert.Null(Check(access.Mint(claims => claims.Remove("nbf"))));
        }

        // The algorithm is exactly RS256: none with no signature, HS256 signed with the public key as its secret, and
        // RS512 signed by the right key are all refused before any key is looked up
        [Fact]
        public void AlgNoneIsRefused()
        {
            var none = FakeCloudflareAccess.Encode(new { alg = "none", kid = access.CurrentKeyId }) + "." + FakeCloudflareAccess.Encode(new { iss = FakeCloudflareAccess.Issuer }) + ".";

            Assert.False(TryReadHeader(none, out _, out var problem));
            Assert.Equal(NotRs256, problem);
            Assert.Equal(NotRs256, Check(none));
        }

        [Fact]
        public void Hs256WithThePublicKeyAsItsSecretIsRefused()
        {
            var signed = FakeCloudflareAccess.Encode(new { alg = "HS256", kid = access.CurrentKeyId, typ = "JWT" }) + "." + FakeCloudflareAccess.Encode(new
            {
                iss = FakeCloudflareAccess.Issuer,
                aud = new[] { access.Audience },
                exp = Now + 600
            });
            var token = signed + "." + Base64Url.EncodeToString(HMACSHA256.HashData(keys.Signer.ExportSubjectPublicKeyInfo(), Encoding.ASCII.GetBytes(signed)));

            Assert.False(TryReadHeader(token, out _, out var problem));
            Assert.Equal(NotRs256, problem);
            Assert.Equal(NotRs256, Check(token));
        }

        [Fact]
        public void Rs512FromTheRightKeyIsRefused()
        {
            var token = access.Mint(header: head => head["alg"] = "RS512", hash: HashAlgorithmName.SHA512);

            Assert.Equal(NotRs256, Check(token));
            Assert.Equal(NotRs256, Check(access.Mint(header: head => head["alg"] = "rs256")));
            Assert.Equal(NotRs256, Check(access.Mint(header: head => head.Remove("alg"))));
        }

        // The key is found by kid alone, so a token must name one
        [Fact]
        public void NoKeyIdIsRefused()
        {
            foreach (var token in new[]
            {
                access.Mint(header: head => head.Remove("kid")),
                access.Mint(header: head => head["kid"] = ""),
                access.Mint(header: head => head["kid"] = 7)
            })
            {
                Assert.False(TryReadHeader(token, out var kid, out var problem));
                Assert.Equal(Unreadable, problem);
                Assert.Equal("", kid);
                Assert.Equal(Unreadable, Check(token));
            }
        }

        [Fact]
        public void SignedByAnotherKeyUnderTheSameIdIsRefused()
        {
            Assert.Equal(BadSignature, Check(access.Mint(signWith: keys.Other)));
        }

        // A payload swapped in from another token keeps a signature that no longer matches
        [Fact]
        public void ChangedPayloadIsRefused()
        {
            var parts = access.Mint().Split('.');
            var changed = parts[0] + "." + FakeCloudflareAccess.Encode(new { iss = FakeCloudflareAccess.Issuer, aud = new[] { access.Audience }, exp = Now + 86400 }) + "." + parts[2];

            Assert.Equal(BadSignature, Check(changed));

            var otherHeader = FakeCloudflareAccess.Encode(new { alg = "RS256", kid = access.NextKeyId }) + "." + parts[1] + "." + parts[2];
            Assert.Equal(BadSignature, Check(otherHeader));
        }

        // The signature comes before anything in the payload is read: a payload that's unreadable, or wrong in every way,
        // is only ever a bad signature unless the right key signed it
        [Fact]
        public void SignatureIsCheckedBeforeThePayload()
        {
            var header = """{"alg":"RS256","kid":"key"}""";
            var expiredElsewhere = $$"""{"iss":"https://evil-team.cloudflareaccess.com","aud":["{{new string('0', 64)}}"],"exp":1}""";

            Assert.Equal(BadSignature, Check(access.MintRaw(header, "not json", keys.Other)));
            Assert.Equal(BadSignature, Check(access.MintRaw(header, expiredElsewhere, keys.Other)));
            Assert.Equal(BadSignature, Check(access.MintRaw(header, "[]", keys.Other)));

            // The same payloads signed by the right key are read, and refused for what they say
            Assert.Equal(Unreadable, Check(access.MintRaw(header, "not json", keys.Signer)));
            Assert.Equal(WrongIssuer, Check(access.MintRaw(header, expiredElsewhere, keys.Signer)));
            Assert.Equal(Unreadable, Check(access.MintRaw(header, "[]", keys.Signer)));

            // A payload part that isn't base64url at all, under a good signature of other text
            var valid = access.Mint().Split('.');
            Assert.Equal(BadSignature, Check(valid[0] + ".A." + valid[2]));
        }

        public static TheoryData<string> Garbage => new()
        {
            "",
            "not-a-token",
            "a.b",
            "a.b.c.d",
            "@@@.###.$$$",
            "eyJ.eyJ.e+/=",
            "a b.c.d",
            ".."
        };

        [Theory]
        [MemberData(nameof(Garbage))]
        public void GarbageIsUnreadable(string token)
        {
            Assert.False(TryReadHeader(token, out _, out var problem));
            Assert.Equal(Unreadable, problem);
            Assert.Equal(Unreadable, Check(token));
        }

        // Exactly three parts. A fourth after a good token is refused as the header is read, before any key is looked up,
        // and so is a third dot with nothing after it.
        [Fact]
        public void FourthPartIsUnreadable()
        {
            foreach (var token in new[] { access.Mint() + ".extra", access.Mint() + "." })
            {
                Assert.False(TryReadHeader(token, out var kid, out var problem));
                Assert.Equal(Unreadable, problem);
                Assert.Equal("", kid);
                Assert.Equal(Unreadable, Check(token));
            }
        }

        [Fact]
        public void HeaderThatIsNotAnObjectIsUnreadable()
        {
            var signature = access.Mint().Split('.')[2];

            Assert.Equal(Unreadable, Check(Raw("[]") + "." + Raw("{}") + "." + signature));
            Assert.Equal(Unreadable, Check(Raw("\"RS256\"") + "." + Raw("{}") + "." + signature));
            Assert.Equal(Unreadable, Check(Raw("{\"alg\":") + "." + Raw("{}") + "." + signature));
        }

        // A name given twice can't be read two ways
        [Fact]
        public void DuplicateNamesAreUnreadable()
        {
            var header = $$"""{"alg":"none","kid":"{{access.CurrentKeyId}}","alg":"RS256"}""";
            Assert.Equal(Unreadable, Check(access.MintRaw(header, "{}")));

            var payload = $$"""{"iss":"{{FakeCloudflareAccess.Issuer}}","aud":"{{access.Audience}}","exp":{{Now + 600}},"exp":{{Now - 3600}}}""";
            Assert.Equal(Unreadable, Check(access.MintRaw($$"""{"alg":"RS256","kid":"{{access.CurrentKeyId}}"}""", payload)));
        }

        // A number out of range, a fraction or text is unreadable rather than a time
        [Fact]
        public void TimesThatAreNotWholeSecondsAreUnreadable()
        {
            Assert.Equal(Unreadable, Check(access.Mint(claims => claims["exp"] = (Now + 600).ToString())));
            Assert.Equal(Unreadable, Check(access.Mint(claims => claims["exp"] = long.MaxValue)));
            Assert.Equal(Unreadable, Check(access.Mint(claims => claims["exp"] = Now + 600.5)));
            Assert.Equal(Unreadable, Check(access.Mint(claims => claims["exp"] = null)));
            Assert.Equal(Unreadable, Check(access.Mint(claims => claims["nbf"] = Now.ToString())));
            Assert.Equal(Unreadable, Check(access.Mint(claims => claims["nbf"] = long.MinValue)));
        }

        [Fact]
        public void PayloadThatIsNotAnObjectIsUnreadable()
        {
            var header = $$"""{"alg":"RS256","kid":"{{access.CurrentKeyId}}"}""";

            Assert.Equal(Unreadable, Check(access.MintRaw(header, "[]")));
            Assert.Equal(Unreadable, Check(access.MintRaw(header, "42")));
        }

        // At most 8192 characters, before anything else is looked at
        [Fact]
        public void TooLongIsRefused()
        {
            var longest = "a.b." + new string('c', MaxLength - 4);
            var tooLong = longest + "c";

            Assert.False(TryReadHeader(longest, out _, out var problem));
            Assert.Equal(Unreadable, problem);

            Assert.False(TryReadHeader(tooLong, out _, out problem));
            Assert.Equal(TooLong, problem);
            Assert.Equal(TooLong, Check(tooLong));
            Assert.Equal(TooLong, Check(new string('a', 9000)));
        }
    }
}
