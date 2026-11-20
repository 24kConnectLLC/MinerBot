using System.Security.Cryptography;

namespace MinerBot.Shared.Requests
{
    // What the dashboard signs of a request, as bot_commands holds it: every field the bot acts on or reports. The row's ID
    // isn't one, because the dashboard can't know it before its insert, so the random key it makes stands in for it. Times
    // are UTC to the millisecond.
    public sealed record RequestFields(string RequestKey, string Kind, ulong ActorId, string ActorName, string ActorRole, ulong? GuildId,
        ulong? ChannelId, string? Argument, DateTime CreatedAt, DateTime ExpiresAt)
    {
        // 16 random bytes as 32 hexadecimal digits in small letters, which the table's unique key holds to one row
        public static string NewKey() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        // A new request as the dashboard writes it: made now, cut to the millisecond, and expiring after its kind's expiry.
        // DATETIME(3) drops what's past the millisecond without a word, and the bot verifies the row as it reads it back,
        // so a signature over the whole time would never match.
        public static RequestFields New(string requestKey, RequestKind kind, ulong actorId, string actorName, string actorRole, ulong? guildId,
            ulong? channelId, string? argument, DateTime now)
        {
            ArgumentNullException.ThrowIfNull(kind);

            var createdAt = ToMillisecond(now);
            return new RequestFields(requestKey, kind.Word, actorId, actorName, actorRole, guildId, channelId, argument, createdAt, createdAt + kind.Expiry);
        }

        // The time as DATETIME(3) keeps it, with its kind
        public static DateTime ToMillisecond(DateTime time) => new(time.Ticks - time.Ticks % TimeSpan.TicksPerMillisecond, time.Kind);
    }
}
