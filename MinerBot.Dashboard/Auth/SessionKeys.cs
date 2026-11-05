using System.Buffers.Text;
using System.Security.Cryptography;

namespace MinerBot.Dashboard.Auth
{
    // A session's key: 256 random bits as base64url, which is all the cookie holds. The database keeps only its hash.
    internal static class SessionKeys
    {
        const int KeyBytes = 32;
        public const int Length = 43;

        const string ItemKey = "mb:session-key";

        public static string New() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(KeyBytes));

        // Checked before anything goes near the database, so a made-up or broken cookie costs nothing
        public static bool IsWellFormed(string? key) =>
            key is { Length: Length } && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

        // The key of this request's session, once the ticket store has found it
        public static string? Current(HttpContext context) => context.Items[ItemKey] as string;

        internal static void Remember(HttpContext context, string key) => context.Items[ItemKey] = key;
    }
}
