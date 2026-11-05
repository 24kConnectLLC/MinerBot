using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MinerBot.Dashboard.Security
{
    // The rules a Cloudflare Access token must pass, in the order they run, with .NET's own APIs alone. Each refusal is one
    // of the fixed reasons below, which never quote the token or anything in it. Nothing in the payload is read until the
    // signature has passed.
    internal static class CloudflareAccessToken
    {
        // Cloudflare's tokens are about a kilobyte, so anything this long is someone else's
        public const int MaxLength = 8192;

        // How far Cloudflare's clock and this server's may differ
        public static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);

        // Why a request was refused, for its log line. The check itself finds the first two.
        public const string Missing = "missing";
        public const string MoreThanOne = "more than one";
        public const string TooLong = "too long";
        public const string Unreadable = "unreadable";
        public const string NotRs256 = "not RS256";
        public const string UnknownKey = "unknown key";
        public const string BadSignature = "bad signature";
        public const string WrongIssuer = "wrong issuer";
        public const string WrongAudience = "wrong audience";
        public const string Expired = "expired";
        public const string NotValidYet = "not valid yet";

        const string Algorithm = "RS256";

        // A name given twice is refused, so no part of a token can be read two ways
        static readonly JsonDocumentOptions Json = new() { AllowDuplicateProperties = false };

        // The seconds since 1970 that a date can hold
        static readonly long MinSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
        static readonly long MaxSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

        // The length, the three parts and the header, which names the key to check the signature with. The header is
        // only read here: nothing in it is trusted, and the signature covers it.
        public static bool TryReadHeader(string token, out string kid, out string? problem)
        {
            kid = "";

            if (token.Length > MaxLength)
            {
                problem = TooLong;
                return false;
            }

            if (!TrySplit(token, out var firstDot, out _))
            {
                problem = Unreadable;
                return false;
            }

            problem = null;

            try
            {
                using var header = JsonDocument.Parse(Base64Url.DecodeFromChars(token.AsSpan(0, firstDot)), Json);
                var root = header.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                    problem = Unreadable;
                // Exactly RS256, so none, HS256 signed with the public key as its secret, and every other algorithm are refused
                else if (Text(root, "alg") != Algorithm)
                    problem = NotRs256;
                // The key is found by this alone, and no other key is ever tried
                else if (Text(root, "kid") is not { Length: > 0 } named)
                    problem = Unreadable;
                else
                    kid = named;
            }
            catch (Exception e) when (e is FormatException or JsonException)
            {
                problem = Unreadable;
            }

            return problem == null;
        }

        // Every rule, with the key the header named: the header again, then the signature over the first two parts exactly
        // as they came, and only then the payload's claims. Null when the token passes.
        public static string? Problem(string token, RSA key, string issuer, string audience, DateTimeOffset now)
        {
            if (!TryReadHeader(token, out _, out var problem))
                return problem;

            TrySplit(token, out var firstDot, out var secondDot);

            try
            {
                var signature = Base64Url.DecodeFromChars(token.AsSpan(secondDot + 1));

                if (!key.VerifyData(Encoding.ASCII.GetBytes(token[..secondDot]), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    return BadSignature;
            }
            catch (FormatException)
            {
                return Unreadable;
            }
            catch (CryptographicException)
            {
                return BadSignature;
            }

            try
            {
                using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(token.AsSpan(firstDot + 1, secondDot - firstDot - 1)), Json);
                return ClaimsProblem(payload.RootElement, issuer, audience, now);
            }
            catch (Exception e) when (e is FormatException or JsonException)
            {
                return Unreadable;
            }
        }

        static string? ClaimsProblem(JsonElement claims, string issuer, string audience, DateTimeOffset now)
        {
            if (claims.ValueKind != JsonValueKind.Object)
                return Unreadable;

            if (Text(claims, "iss") != issuer)
                return WrongIssuer;

            if (!HasAudience(claims, audience))
                return WrongAudience;

            // Required: a token without an end would never expire
            if (!claims.TryGetProperty("exp", out var exp))
                return Expired;

            if (!TryTime(exp, out var expires))
                return Unreadable;

            // Good only while now < exp + 60 s
            if (now - Skew >= expires)
                return Expired;

            // Optional, and good once now + 60 s >= nbf
            if (claims.TryGetProperty("nbf", out var nbf))
            {
                if (!TryTime(nbf, out var notBefore))
                    return Unreadable;

                if (now + Skew < notBefore)
                    return NotValidYet;
            }

            return null;
        }

        // Cloudflare sends aud as an array, and a single string is a valid form too
        static bool HasAudience(JsonElement claims, string audience)
        {
            if (!claims.TryGetProperty("aud", out var aud))
                return false;

            if (aud.ValueKind == JsonValueKind.String)
                return aud.ValueEquals(audience);

            return aud.ValueKind == JsonValueKind.Array && aud.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.ValueEquals(audience));
        }

        // A whole number of seconds since 1970 that a date can hold. Anything else, a string or a fraction included, isn't.
        static bool TryTime(JsonElement value, out DateTimeOffset time)
        {
            time = default;

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var seconds) || seconds < MinSeconds || seconds > MaxSeconds)
                return false;

            time = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }

        // Three parts of base64url without padding, separated by exactly two dots
        static bool TrySplit(string token, out int firstDot, out int secondDot)
        {
            firstDot = token.IndexOf('.');
            secondDot = firstDot < 0 ? -1 : token.IndexOf('.', firstDot + 1);

            if (secondDot < 0 || token.IndexOf('.', secondDot + 1) >= 0)
                return false;

            foreach (var c in token)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
                    return false;
            }

            return true;
        }

        // A property that's a string, or null when it's missing or anything else
        internal static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
