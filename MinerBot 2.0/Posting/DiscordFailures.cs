using DataAccessLibrary.Posting;
using Discord.Net;
using MinerBot.Shared.Delivery;

namespace MinerBot_2._0.Posting
{
    // How a send that didn't go ended: failed, with a reason, when Discord answered, or in doubt, when it didn't, so the
    // message may have been made. Counts says it was this server's own refusal, which counts against its kind. Discord's
    // numbers come with it, the HTTP status and Discord's own code, never Discord's text.
    public sealed record SendFailure(string State, string? Reason, ushort? HttpStatus, uint? DiscordCode, bool Counts)
    {
        public bool InDoubt => State == DeliveryState.InDoubt;
    }

    // Reads a failed send by Discord's numbers, through MinerBot.Shared's DeliveryReasons, never by Discord.Net's names for
    // them, which aren't Discord's, and never by its message, which can carry Discord's words or a token. Stopping says the
    // bot's stop abandoned the send. The owner's direct messages keep Discord.Net's default retries and go by Classify, but
    // for a TimeoutException, which their job reads as a message that never went; the posts go by ClassifyPost.
    public static class DiscordFailures
    {
        public static SendFailure Classify(Exception e, bool stopping = false) => e switch
        {
            // A rate limit that reached the bot: Discord didn't act, and it's about every send, not this channel
            RateLimitedException => new SendFailure(DeliveryState.Failed, DeliveryReasons.RateLimited, 429, null, Counts: false),

            HttpException http => Answered((int)http.HttpCode, (int?)http.DiscordCode),

            // The bot's stop, while the send was under way
            OperationCanceledException when stopping => new SendFailure(DeliveryState.InDoubt, null, null, null, Counts: false),

            // No answer in time, or the connection failing: the message may have been made. A request that got no answer ends
            // as an OperationCanceledException, at the request's own limit or at HttpClient's.
            OperationCanceledException or TimeoutException or IOException or HttpRequestException =>
                new SendFailure(DeliveryState.InDoubt, DeliveryReasons.NoAnswer, null, null, Counts: false),

            // Anything else can't say whether it went, so it's in doubt too, and never sent again on a guess
            _ => new SendFailure(DeliveryState.InDoubt, null, null, null, Counts: false)
        };

        // A post's send, with Deliverer.SendOptions and PostingGate.Limit's token. Under RetryRatelimit Discord.Net throws a
        // TimeoutException only before a try, never for one under way: once the request's time ran out after Discord answered
        // 429 or a global rate limit held it, or once its token was cancelled; and it tries again only after a 429, which
        // Discord didn't act on. So nothing was made: it's the rate limit that reached the bot, or the stop that came first,
        // and its rows wait again. Its own REST client never throws one, as HttpClient ends a request with no answer as an
        // OperationCanceledException. Nothing was made either when the connection never opened: Discord's name not found,
        // the connection refused or unreachable, or its TLS failing, all before any of the request was written, as .NET
        // never sends a request with a body again on another connection. Tests pin both, through Discord.Net's request queue
        // and with its own REST client, so an upgrade that changes either fails them. A connection that hangs, or a name
        // lookup slower than the request's limit, ends at that limit as a cancel, as a request Discord never answered does,
        // so it stays in doubt: Discord.Net's client has no connect timeout of its own that would tell them apart. So does a
        // 429 whose wait outlasts that limit, though nothing was made: Discord.Net's wait for it ends as a cancel too. The
        // look in the channel settles each, and while Discord answers none of the posts' requests it waits past its hour
        // (DeliveryVerifier).
        public static SendFailure ClassifyPost(Exception e, bool stopping) => e switch
        {
            // Discord.Net's RateLimitedException is a TimeoutException, with Discord's own 429 behind it
            RateLimitedException => Classify(e, stopping),

            TimeoutException => new SendFailure(DeliveryState.Failed, stopping ? DeliveryReasons.NotSent : DeliveryReasons.RateLimited, null, null, Counts: false),

            HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError } =>
                new SendFailure(DeliveryState.Failed, DeliveryReasons.NotSent, null, null, Counts: false),

            _ => Classify(e, stopping)
        };

        // Discord's answer: its own trouble (5xx) leaves the message in doubt, a rate limit or a refused token is about every
        // send, and any other refusal is the server's own
        static SendFailure Answered(int httpStatus, int? discordCode)
        {
            var reason = DeliveryReasons.ForAnswer(httpStatus, discordCode);
            var status = (ushort)Math.Clamp(httpStatus, 0, ushort.MaxValue);
            var code = DeliveryReasons.CodeOf(discordCode) is { } known and > 0 ? (uint?)known : null;

            return reason switch
            {
                DeliveryReasons.NoAnswer => new SendFailure(DeliveryState.InDoubt, reason, status, code, Counts: false),
                DeliveryReasons.RateLimited or DeliveryReasons.Unauthorized => new SendFailure(DeliveryState.Failed, reason, status, code, Counts: false),
                _ => new SendFailure(DeliveryState.Failed, reason, status, code, Counts: true)
            };
        }
    }
}
