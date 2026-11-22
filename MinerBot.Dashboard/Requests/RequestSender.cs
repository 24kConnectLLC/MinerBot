using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Settings;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Requests
{
    // What a page asks the sender for: the kind from the handler's own constant, never from the form; the key the page drew
    // the form with; the server, the channel and the text the kind takes; what was typed to confirm it; and whether nobody
    // pressed anything, as for the cache clear after an armor review, whose refusal for a person's limit isn't recorded
    internal sealed record SendOrder(string Kind, string? FormKey, ulong? GuildId = null, ulong? ChannelId = null, string? Argument = null,
        Confirmation? Typed = null, bool Automatic = false);

    public enum SendRefusal
    {
        Forbidden,
        MadeUp,
        Off,
        NotFresh,
        NotConfirmed,
        Rule,
        PersonLimit,
        BotLimit,
        Waiting,
        KeyTaken
    }

    // How a send ended
    public abstract record SendOutcome
    {
        // Written, or found as this form's first sending with the same details, when nothing more was written
        public sealed record Sent(ulong Id, bool Repeated) : SendOutcome;

        // This form's key, sent before by the same person for the same kind, with other details
        public sealed record SentBefore(ulong Id) : SendOutcome;

        // The commit wasn't confirmed, so the request may or may not be there, under this key
        public sealed record Unsure(string Key) : SendOutcome;

        // Nothing was written, with why in the words a page shows. For a limit, when another fits; for a request that waits,
        // its ID.
        public sealed record Refused(SendRefusal Why, string Words, DateTime? After = null, ulong? Waiting = null) : SendOutcome;
    }

    // The key a form is drawn with: a new one for every form on every render, refusals included, since a refusal wrote
    // nothing, apart from the form drawn again after a save the database didn't confirm, which keeps the key it was sent
    // with, so pressing it again finds the first request rather than writing a second
    internal static class FormKeys
    {
        public static string For(SendOutcome? outcome) => outcome is SendOutcome.Unsure unsure ? unsure.Key : RequestFields.NewKey();
    }

    // Every request a page sends goes through here, in one order of checks that stops at the first refusal, so no page can
    // skip one: the kind, the role, the form, a key already used, requests on, a fresh sign-in, the typed confirmation, the
    // shared rules, the person's limit, the bot's, then the write. A key already used comes before every limit, so a double
    // click shows the first request rather than a refusal. From that look to the write, one person's sends take turns, so
    // forms posted at once each count the ones written before them, and a limit that refuses looks for the key again, for a
    // first sending the look missed. Pages get it from RequestServices, as they get ArmorReviews; only it and RequestCommand
    // call BotRequests.
    internal sealed class RequestSender(BotRequests requests, IBotRequestQueue queue, IBotRequestReads reads, IAuditLogData audit,
        PersonTurns turns, IOptions<DashboardOptions> settings, TimeProvider time, ILogger<RequestSender> logger)
    {
        public async Task<SendOutcome> SendAsync(SendOrder order, HttpContext context)
        {
            ArgumentNullException.ThrowIfNull(order);
            ArgumentNullException.ThrowIfNull(context);

            // 1. The kind is the handler's own constant, so one that isn't on the list is a bug
            var kind = RequestKinds.Find(order.Kind) ?? throw new InvalidOperationException("A page asked to send a kind that isn't on the request list");
            var now = time.GetUtcNow();
            var actor = RequestActors.Of(context);

            // 2. The role, from the lists on this request, refused and recorded before anything else is looked at
            if (actor == null || !kind.Allows(actor.Role))
            {
                await RefusedAsync(context, kind, actor, null, RequestWords.ForbiddenDetail(kind), now);
                return new SendOutcome.Refused(SendRefusal.Forbidden, RequestWords.Forbidden);
            }

            // 3. The form: a key in the form the table holds, checked here so a made-up one never reaches SQL, and a server or
            // a channel that's a Discord ID. Not recorded: it's a form someone made up, as every page's 400 is.
            if (order.FormKey is not { Length: 32 } key || !key.All(char.IsAsciiHexDigitLower) || order.GuildId == 0 || order.ChannelId == 0)
                return new SendOutcome.Refused(SendRefusal.MadeUp, RequestWords.MadeUp);

            // A reason or a status text without the white space around it, which the shared rules refuse, and an empty box as
            // no text
            var argument = string.IsNullOrWhiteSpace(order.Argument) ? null : order.Argument.Trim();

            // The person's turn, held to the end: their sends made at the same moment would otherwise all count before any
            // was written and all pass their limit, which the bot doesn't keep. Taken before the key's look, so a form sent
            // twice at once finds its first sending there rather than a check after it: the limit it just used up, or a fresh
            // sign-in that ran out while the first was sent.
            using var turn = await turns.TakeAsync(actor.Id);

            // 4. A key already used: this form sent again, by a double click, or from a page a browser kept
            if (await DatabaseCalls.Guard(() => queue.GetRequestByKey(key), logger) is { } first)
                return FirstSending(first, kind, actor, order, argument);

            // 5. Requests on
            if (RequestSigning.ParseKey(settings.Value.RequestSigningKey) == null)
                return new SendOutcome.Refused(SendRefusal.Off, RequestWords.Off);

            // 6. A sign-in from the last 15 minutes, for the controls that need one
            if (FreshSignIn.Needs(kind, argument) && !FreshSignIn.IsFresh(context, now))
            {
                await RefusedAsync(context, kind, actor, order.GuildId, RequestWords.NotFreshDetail, now);
                return new SendOutcome.Refused(SendRefusal.NotFresh, RequestWords.NotFresh(FreshSignIn.SignedInAt(context), argument != null));
            }

            // 7. What was typed to confirm it. Not recorded: the form goes back to correct a field.
            if (NotConfirmed(kind, order, argument) is { } typing)
                return new SendOutcome.Refused(SendRefusal.NotConfirmed, typing);

            // 8. The shared rules, which the bot checks again, in the rule's own words, which never quote the text
            PreparedRequest prepared;

            try
            {
                prepared = requests.Prepare(new RequestOrder(kind.Word, order.GuildId, order.ChannelId, argument, key), actor, now.UtcDateTime);
            }
            catch (RequestsOffException)
            {
                return new SendOutcome.Refused(SendRefusal.Off, RequestWords.Off);
            }
            catch (RequestRefusedException e)
            {
                return new SendOutcome.Refused(SendRefusal.Rule, RequestWords.Rule(e.Problem));
            }

            // 9. The person's own limit, recorded unless nobody pressed anything
            var asked = await DatabaseCalls.Guard(() => reads.ListAskedBy(actor.Id, (now - PersonLimits.Window).UtcDateTime), logger);
            var person = PersonLimits.Check(kind, asked, now.UtcDateTime);

            if (person.Reached)
            {
                if (await SentMeanwhileAsync(key, kind, actor, order, argument) is { } meanwhile)
                    return meanwhile;

                if (!order.Automatic)
                    await RefusedAsync(context, kind, actor, order.GuildId, RequestWords.PersonLimitDetail, now);

                return new SendOutcome.Refused(SendRefusal.PersonLimit, RequestWords.PersonLimit(person), person.After);
            }

            // 10. The bot's limit. Not recorded: the bot would refuse it anyway, and these are its own words. The requests
            // that wait are read before the ones the bot took, so one the bot takes between the two reads is in both and
            // counted once, as taken, rather than in neither.
            var waiting = await DatabaseCalls.Guard(() => reads.ListWaiting(now.UtcDateTime), logger);
            var claims = await DatabaseCalls.Guard(() => reads.ListClaimsSince((now - BotLimits.Longest).UtcDateTime), logger);
            var bot = BotLimits.Check(kind, order.GuildId, claims, waiting, now.UtcDateTime);

            if (bot.State != BotLimitState.Available && await SentMeanwhileAsync(key, kind, actor, order, argument) is { } sentMeanwhile)
                return sentMeanwhile;

            if (bot.State == BotLimitState.Limit)
                return new SendOutcome.Refused(SendRefusal.BotLimit, RequestWords.BotLimit(kind, bot), bot.After);

            if (bot is { State: BotLimitState.Waiting, Waiting: { } queuedBefore })
                return new SendOutcome.Refused(SendRefusal.Waiting, RequestWords.Waiting(queuedBefore), bot.After, queuedBefore.id);

            // 11. The write. A DashboardUnavailableException before the work returned isn't caught: the 503 page then says,
            // truly, that nothing was changed.
            QueuedRequest queued;

            try
            {
                queued = await requests.WriteOwnAsync(prepared, context.Connection.RemoteIpAddress?.ToString(), context.TraceIdentifier);
            }
            catch (FormKeyTakenException)
            {
                // Another person's request, or another kind's, took this key between the look above and the write
                return new SendOutcome.Refused(SendRefusal.KeyTaken, RequestWords.KeyTaken);
            }

            if (!queued.Confirmed)
                return new SendOutcome.Unsure(key);

            // The same form, sent again at the same moment, wrote first: its details decide, as above
            if (queued.Repeated)
                return await DatabaseCalls.Guard(() => queue.GetRequestByKey(key), logger) is { } raced
                    ? FirstSending(raced, kind, actor, order, argument)
                    : new SendOutcome.Sent(queued.Id, Repeated: true);

            return new SendOutcome.Sent(queued.Id, Repeated: false);
        }

        // A form's key that's in the table already: this form's first sending, when it's the same person's request of the same
        // kind with the same details; with other details, the form was used already; anything else isn't this form's
        static SendOutcome FirstSending(BotRequestRow first, RequestKind kind, RequestActor actor, SendOrder order, string? argument)
        {
            if (first.actor_id != actor.Id || first.kind != kind.Word)
                return new SendOutcome.Refused(SendRefusal.KeyTaken, RequestWords.KeyTaken);

            if (first.guild_id != order.GuildId || first.channel_id != order.ChannelId || first.argument != argument)
                return new SendOutcome.SentBefore(first.id);

            return new SendOutcome.Sent(first.id, Repeated: true);
        }

        // This form's request, landed since the look at step 4. The person's turn keeps their other sends from writing between
        // that look and this one's write, so it's a first sending whose save the database didn't confirm: that send ended as
        // Unsure while its commit was under way, which then landed while this one was between the look and the limits, which
        // count it. As at step 4, that request is the answer rather than a refusal. Null when the key is still free. Read only
        // once a limit has refused, so a send that passes them reads the key once.
        async Task<SendOutcome?> SentMeanwhileAsync(string key, RequestKind kind, RequestActor actor, SendOrder order, string? argument) =>
            await DatabaseCalls.Guard(() => queue.GetRequestByKey(key), logger) is { } first ? FirstSending(first, kind, actor, order, argument) : null;

        // Null when what was typed confirms the control, or it needs nothing typed; otherwise what to type. A form sent
        // without the box is refused like one with the wrong word.
        static string? NotConfirmed(RequestKind kind, SendOrder order, string? argument)
        {
            if (Confirmations.WordFor(kind) is { } word)
                return order.Typed is Confirmation.Word typed && Confirmations.MatchesWord(typed.Typed, word) ? null : RequestWords.NotConfirmed(word);

            if (Confirmations.NeedsServer(kind))
                return order.Typed is Confirmation.Server server && server.Id == order.GuildId && Confirmations.MatchesServer(server.Typed, server.Name, server.Id)
                    ? null
                    : RequestWords.NotConfirmedServer;

            // The status text's own form, which is never sent empty: only Back to the default sends no text
            if (order.Typed is Confirmation.StatusText && argument == null)
                return RequestWords.NoStatusText;

            return null;
        }

        Task RefusedAsync(HttpContext context, RequestKind kind, RequestActor? actor, ulong? guildId, string detail, DateTimeOffset now) =>
            DatabaseCalls.Guard(() => audit.AddAudit(Refusal(context, kind, actor, guildId, detail, now)), logger);

        // A refusal the audit log records, on its own, as a refused sign-in's is: the person, the kind as the action, the
        // request with no ID since none was written, why, the address and the trace ID. The controls page's gate writes the
        // same row for an Admin's try at an Owner control, without reaching the sender.
        internal static AuditEntry Refusal(HttpContext context, RequestKind kind, RequestActor? actor, ulong? guildId, string detail, DateTimeOffset now) => new()
        {
            at = now.UtcDateTime,
            actor_id = actor?.Id,
            actor_name = actor?.Name,
            actor_role = actor?.Role ?? AuditRole.Visitor,
            action = kind.Word,
            outcome = AuditOutcome.Refused,
            guild_id = guildId,
            target_type = AuditWords.RequestTarget,
            detail = detail,
            ip = context.Connection.RemoteIpAddress?.ToString(),
            request_id = context.TraceIdentifier
        };
    }
}
