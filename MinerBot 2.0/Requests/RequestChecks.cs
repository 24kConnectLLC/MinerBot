using DataAccessLibrary.Requests.Models;
using MinerBot.Shared.Requests;

namespace MinerBot_2._0.Requests
{
    // What the checks found of a request: accepted, with its kind, or refused or expired, with the result for its row.
    // Rejected is set for the checks that mean the row isn't one the dashboard signed for this bot, which have an event.
    internal sealed record Verdict(RequestKind? Kind, string? State, string? Result, Rejection? Rejected)
    {
        public bool Accepted => State == null;
    }

    // The checks of a request before its limit and its kind's own conditions, in order, from the row, the bot's key and
    // clock, and the request keys this run took. Nothing in a row is trusted until its signature passed, so a forged row
    // is refused as forged, whatever else is wrong with it, and its kind word isn't repeated. No refusal repeats the
    // request's own text.
    internal static class RequestChecks
    {
        // A request may be made this far past the bot's clock: both apps run on the host's
        internal static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

        public static Verdict Check(BotRequestRow row, byte[]? key, DateTime now, Func<string, bool> used)
        {
            // 0. The bot has a key
            if (key == null)
                return Refused(RequestTexts.NoKey);

            // 1. The row is in the form the dashboard writes
            var fields = row.Fields();

            if (RequestRules.FormProblem(fields) != null || row.signature is not { Length: RequestSigning.SignatureLength })
                return Refused(RequestTexts.NotInForm, Rejection.Form);

            // 2. Its signature
            if (!RequestSigning.Verify(key, fields, row.signature))
                return Refused(RequestTexts.BadSignature, Rejection.Signature);

            // 3. A key this run took, of a row deleted and written again
            if (used(row.request_key))
                return Refused(RequestTexts.AlreadyUsed);

            // 4. The kind is on the list
            if (RequestKinds.Find(row.kind) is not { } kind)
                return Refused(RequestTexts.UnknownKind, Rejection.Kind);

            // 5. The role may ask for it
            if (!kind.Allows(row.actor_role))
                return Refused(RequestTexts.RoleRefused(kind), Rejection.Role, kind);

            // 6. It was made to last no longer than its kind allows, and not later than the bot's clock, so a signed row can't
            // be held back to use later
            if (!kind.FitsExpiry(row.created_at, row.expires_at))
                return Refused(RequestTexts.TooLong(kind), Rejection.TooLong, kind);

            if (row.created_at > now + ClockSkew)
                return Refused(RequestTexts.FromTheFuture, Rejection.Future, kind);

            // 7. It hasn't expired
            if (now >= row.expires_at)
                return new Verdict(kind, RequestStates.Expired, RequestTexts.Expired(row.expires_at), null);

            // 8. Its server, channel and text fit its kind
            if (RequestRules.DetailsProblem(fields) is { } rule)
                return Refused(RequestTexts.DetailsDontFit(rule), Rejection.Details, kind);

            return new Verdict(kind, null, null, null);
        }

        static Verdict Refused(string result, Rejection? rejected = null, RequestKind? kind = null) => new(kind, RequestStates.Refused, result, rejected);
    }
}
