using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MinerBot.Shared.Requests
{
    // The signature on each request the dashboard leaves the bot: HMAC-SHA256, with a key only the two apps hold, over the
    // request's fields in one fixed layout. The dashboard signs a request once, and the bot verifies it as it reads it
    // back, so anything that wrote or changed the row without the key is refused.
    //
    // Version 1's layout: the 19 bytes of "minerbot-request-v1", then the ten fields of RequestFields in order, each
    // written as 0x00 for null, or as 0x01, its length in bytes as four bytes, most significant first, and its UTF-8. The
    // length in front keeps a character from moving from one field into the next, and the first byte keeps null apart from
    // an empty text. IDs are written in decimal, and times as yyyy-MM-ddTHH:mm:ss.fffZ.
    public static class RequestSigning
    {
        // The key's bytes. Setup writes it as 64 hexadecimal digits in small letters, which is what openssl rand -hex 32
        // makes, and which the scripts can compare as text.
        public const int KeyLength = 32;

        // HMAC-SHA256's length, which bot_commands.signature holds
        public const int SignatureLength = 32;

        // So the key signs nothing but a request in this layout
        static readonly byte[] Prefix = "minerbot-request-v1"u8.ToArray();

        // Strict, so a lone surrogate is refused rather than written as U+FFFD, which would let two different texts sign
        // alike
        static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        // Null when the setting holds a key the apps can use. Otherwise what's wrong with it, in words that follow the
        // setting's name, and never the value itself.
        public static string? KeyProblem(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return "isn't set";

            if (text.Length != KeyLength * 2 || !text.All(char.IsAsciiHexDigitLower))
                return "isn't 64 hexadecimal digits in small letters";

            return null;
        }

        // The key's bytes, or null when KeyProblem says it can't be used
        public static byte[]? ParseKey(string? text) => KeyProblem(text) == null ? Convert.FromHexString(text!) : null;

        // The bytes the signature covers. An ArgumentException for fields that can't be written: a text that's missing
        // where the table has no NULL, a lone surrogate, a local time, or a time past the millisecond, which DATETIME(3)
        // would drop.
        public static byte[] CanonicalBytes(RequestFields fields)
        {
            ArgumentNullException.ThrowIfNull(fields);

            using var bytes = new MemoryStream(256);
            bytes.Write(Prefix);

            Write(bytes, Required(fields.RequestKey, "request key"));
            Write(bytes, Required(fields.Kind, "kind"));
            Write(bytes, Number(fields.ActorId));
            Write(bytes, Required(fields.ActorName, "name"));
            Write(bytes, Required(fields.ActorRole, "role"));
            Write(bytes, fields.GuildId is { } guildId ? Number(guildId) : null);
            Write(bytes, fields.ChannelId is { } channelId ? Number(channelId) : null);
            Write(bytes, fields.Argument);
            Write(bytes, Time(fields.CreatedAt, "made"));
            Write(bytes, Time(fields.ExpiresAt, "expiry"));

            return bytes.ToArray();
        }

        public static byte[] Sign(ReadOnlySpan<byte> key, RequestFields fields)
        {
            if (key.Length != KeyLength)
                throw new ArgumentException($"A request-signing key is {KeyLength} bytes", nameof(key));

            return HMACSHA256.HashData(key, CanonicalBytes(fields));
        }

        // True only for the signature these fields have under this key. Never throws: a key or a signature of another
        // length, or fields that can't be written, are false. The signatures are compared in a time that doesn't depend on
        // where they first differ, so a run of guesses can't learn one a byte at a time.
        public static bool Verify(ReadOnlySpan<byte> key, RequestFields? fields, ReadOnlySpan<byte> signature)
        {
            if (fields == null || key.Length != KeyLength || signature.Length != SignatureLength)
                return false;

            byte[] bytes;

            try
            {
                bytes = CanonicalBytes(fields);
            }
            catch (ArgumentException)
            {
                return false;
            }

            Span<byte> expected = stackalloc byte[SignatureLength];
            HMACSHA256.HashData(key, bytes, expected);

            return CryptographicOperations.FixedTimeEquals(expected, signature);
        }

        static void Write(MemoryStream bytes, string? text)
        {
            if (text == null)
            {
                bytes.WriteByte(0);
                return;
            }

            var encoded = Utf8.GetBytes(text);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)encoded.Length);

            bytes.WriteByte(1);
            bytes.Write(length);
            bytes.Write(encoded);
        }

        static string Required(string? text, string what) =>
            text ?? throw new ArgumentException($"A request's {what} can't be missing", nameof(text));

        static string Number(ulong value) => value.ToString(CultureInfo.InvariantCulture);

        static string Time(DateTime time, string what)
        {
            if (time.Kind == DateTimeKind.Local)
                throw new ArgumentException($"A request's {what} time is UTC, and this one is local", nameof(time));

            if (time.Ticks % TimeSpan.TicksPerMillisecond != 0)
                throw new ArgumentException($"A request's {what} time goes to the millisecond, which is all the table keeps", nameof(time));

            return time.ToString(TimeFormat, CultureInfo.InvariantCulture);
        }
    }
}
