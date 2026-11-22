using System.Globalization;
using System.Text;
using System.Text.Json;
using DataAccessLibrary;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Requests;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Settings;
using MinerBot.Shared.Requests;
using MySql.Data.MySqlClient;

namespace MinerBot.Dashboard.Requests
{
    // Who asks the bot: their Discord ID, the name the dashboard has for them, and one of RequestRoles' words
    internal sealed record RequestActor(ulong Id, string Name, string Role);

    // What's asked: one kind of the request list, with the server, the channel and the text that kind takes. FormKey is
    // the key a page made when it drew its form, so the same form sent twice is one request; without one, a new key is made.
    internal sealed record RequestOrder(string Kind, ulong? GuildId = null, ulong? ChannelId = null, string? Argument = null, string? FormKey = null);

    // A request that went in. Confirmed is false when MinerBot's database stopped answering at the commit, so it may or may
    // not be there: Id is then 0, and the caller looks it up by its key. Repeated is true when the form's key was used
    // already: this is that first request, and nothing more was written.
    internal sealed record QueuedRequest(ulong Id, string RequestKey, string Kind, DateTime CreatedAt, DateTime ExpiresAt, bool Confirmed, bool Repeated);

    // A request worked out and signed, before any transaction: its kind, its fields as the table will hold them, the
    // signature, and who asked, with the name as the request carries it. KeyFromForm is set when a page gave the key, so a
    // second sending of the same form finds the first rather than failing.
    internal sealed record PreparedRequest(RequestKind Kind, RequestFields Fields, byte[] Signature, RequestActor Actor, bool KeyFromForm);

    // The dashboard has no request-signing key, so it sends the bot nothing
    internal sealed class RequestsOffException() : Exception("Requests to the bot are off: the dashboard has no request-signing key");

    // A request the bot would refuse as it stands, so nothing was written. The problem is in RequestRules' words, which never
    // quote the request's own text.
    internal sealed class RequestRefusedException(string problem) : Exception("The bot would refuse this request: " + problem)
    {
        public string Problem { get; } = problem;
    }

    // A form's key that's in the table for another person's request, or another kind's: a page that gave one key to two forms
    // is a mistake to stop, not to answer. Still an InvalidOperationException, as it was before it had a name.
    internal sealed class FormKeyTakenException() : InvalidOperationException("A form's request key is in the table for another request");

    // The dashboard's requests to the bot. Each is checked against MinerBot.Shared's list and rules, signed with the key
    // only the two apps hold, and written with its audit row in one transaction on MinerBot's own connection, so neither
    // goes in without the other: its own, or the caller's, as a sign-in's. The bot takes it within seconds and writes how it
    // ended on the same row, which the caller reads back. A commit the database didn't confirm gets an audit row of its own
    // saying so.
    internal sealed class BotRequests(IBotRequestQueue queue, IAuditLogData audit, IOptions<DashboardOptions> settings, TimeProvider time,
        ILogger<BotRequests> logger)
    {
        // The most of a name a request carries, in characters, which bot_commands.actor_name holds
        const int NameLimit = RequestRules.NameLimit;

        // Prepared, then written in a transaction of its own, as request.sh's command sends one
        public async Task<QueuedRequest> SendAsync(RequestOrder order, RequestActor actor, string? ip, string? traceId)
        {
            var request = Prepare(order, actor);
            return await WriteOwnAsync(request, ip, traceId);
        }

        // The fields and the signature, worked out before any transaction, because a transaction runs again after a deadlock.
        // Writes nothing. The time is now unless given, cut to the millisecond, as the table keeps it, so the bot verifies the
        // row as it reads it back.
        internal PreparedRequest Prepare(RequestOrder order, RequestActor actor, DateTime? now = null)
        {
            ArgumentNullException.ThrowIfNull(order);
            ArgumentNullException.ThrowIfNull(actor);

            // Empty is off, and the settings' check refuses any other form at start-up
            var key = RequestSigning.ParseKey(settings.Value.RequestSigningKey) ?? throw new RequestsOffException();
            var kind = RequestKinds.Find(order.Kind) ?? throw new RequestRefusedException("its kind isn't on the request list");

            var name = CleanName(actor.Name, actor.Id);
            var fields = RequestFields.New(order.FormKey ?? RequestFields.NewKey(), kind, actor.Id, name, actor.Role, order.GuildId, order.ChannelId,
                order.Argument, now ?? time.GetUtcNow().UtcDateTime);

            // The role, the details and everything else the bot checks, so a request it would refuse is never written
            if (RequestRules.Problem(fields) is { } problem)
                throw new RequestRefusedException(problem);

            return new PreparedRequest(kind, fields, RequestSigning.Sign(key, fields), actor with { Name = name }, order.FormKey != null);
        }

        // The sign-in alert for a new session: the owner's message about every sign-in to the admin side, the Owner's own and
        // an Admin's, signing in again included, as the owner chose. Its kind carries no text, server or channel, so the
        // visitor's address never reaches the bot: it's in the alert's audit row, which only the Owner sees. Null while
        // requests are off, so nobody is kept from signing in before setup makes the key. A refusal by the shared rules still
        // throws.
        internal PreparedRequest? PrepareSigninAlert(SignedInUser user, DashboardRole role, DateTime now)
        {
            ArgumentNullException.ThrowIfNull(user);

            if (RequestSigning.ParseKey(settings.Value.RequestSigningKey) == null)
                return null;

            return Prepare(new RequestOrder(RequestKinds.OwnerSigninAlert), new RequestActor(user.Id, user.Name, role.Word()), now);
        }

        // The request and its audit row in the caller's transaction, giving the new ID. Nothing is caught: a failure rolls the
        // caller's whole transaction back, and a run after a deadlock writes the same fields again.
        internal async Task<ulong> WriteAsync(PreparedRequest request, DataTransaction transaction, string? ip, string? traceId)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(transaction);

            var id = await queue.AddRequest(request.Fields, request.Signature, transaction);
            await audit.AddAudit(Entry(request, id, ip, traceId), transaction);
            return id;
        }

        // In a transaction of its own, with the unconfirmed commit's handling, and a form sent twice answered with its first
        // request
        internal async Task<QueuedRequest> WriteOwnAsync(PreparedRequest request, string? ip, string? traceId)
        {
            ArgumentNullException.ThrowIfNull(request);

            var fields = request.Fields;
            var kind = request.Kind;

            // 1 once the work has returned, when the commit may be under way. Until then a failure rolls the whole
            // transaction back, and InTransaction never commits once its caller has stopped waiting. Written on the pool's
            // thread and read on this one, so through Interlocked and Volatile.
            var returned = 0;

            // The audit row the last run wrote, for the row saying it may not have landed. Written before returned is.
            AuditEntry? written = null;
            ulong added;

            try
            {
                added = await DatabaseCalls.Guard(async () =>
                {
                    try
                    {
                        return await queue.RunInTransaction(async transaction =>
                        {
                            // A run after a deadlock starts again, with everything the last one did rolled back
                            Interlocked.Exchange(ref returned, 0);
                            written = null;
                            var id = await queue.AddRequest(fields, request.Signature, transaction);
                            var entry = Entry(request, id, ip, traceId);
                            await audit.AddAudit(entry, transaction);
                            written = entry;
                            Interlocked.Exchange(ref returned, 1);
                            return id;
                        });
                    }
                    // The form's key is in the table already, so this form was sent before. The unique key refused the
                    // insert, and the whole transaction rolled back with it, audit row included.
                    catch (MySqlException e) when (e.Number == (int)MySqlErrorCode.DuplicateKeyEntry && request.KeyFromForm)
                    {
                        return 0UL;
                    }
                }, logger);
            }
            catch (DashboardUnavailableException) when (Volatile.Read(ref returned) == 1)
            {
                // A limit that passed during the commit doesn't stop it, so this can't say nothing was written. Before the
                // work returned, nothing was, and the caller's error says so.
                logger.LogWarning("Request {RequestKey} ({Kind}) may or may not have been saved, because the database stopped answering (request {RequestId})",
                    fields.RequestKey, kind.Word, traceId);

                if (written != null)
                    await UnconfirmedChanges.RecordAsync(audit, written, logger);

                return new QueuedRequest(0, fields.RequestKey, kind.Word, fields.CreatedAt, fields.ExpiresAt, Confirmed: false, Repeated: false);
            }

            if (added == 0)
                return await FirstSendingAsync(request);

            logger.LogInformation("Request {Id} ({Kind}) is queued for the bot (request {RequestId})", added, kind.Word, traceId);
            return new QueuedRequest(added, fields.RequestKey, kind.Word, fields.CreatedAt, fields.ExpiresAt, Confirmed: true, Repeated: false);
        }

        // The request a form's first sending made, which a second sending shows instead of making another. Only the same
        // person's request of the same kind: a page that gave one key to two forms is a mistake to stop, not to answer.
        async Task<QueuedRequest> FirstSendingAsync(PreparedRequest request)
        {
            var first = await DatabaseCalls.Guard(() => queue.GetRequestByKey(request.Fields.RequestKey), logger);

            if (first == null || first.kind != request.Kind.Word || first.actor_id != request.Actor.Id)
                throw new FormKeyTakenException();

            logger.LogInformation("Request {Id} ({Kind}) was sent again from the same form, so nothing more was written", first.id, first.kind);
            return new QueuedRequest(first.id, first.request_key, first.kind, first.created_at, first.expires_at, Confirmed: true, Repeated: true);
        }

        // The request's audit row: who asked for which kind, about which request, from where
        static AuditEntry Entry(PreparedRequest request, ulong id, string? ip, string? traceId) => new()
        {
            at = request.Fields.CreatedAt,
            actor_id = request.Actor.Id,
            actor_name = request.Actor.Name,
            actor_role = request.Actor.Role,
            action = request.Kind.Word,
            outcome = AuditOutcome.Ok,
            guild_id = request.Fields.GuildId,
            target_type = AuditWords.RequestTarget,
            target_id = id.ToString(CultureInfo.InvariantCulture),
            after_json = Json(request.Fields),
            ip = ip,
            request_id = traceId
        };

        // The name as the request carries it, which the owner's direct message shows: control characters become spaces, a
        // broken character becomes U+FFFD, and it's cut to 100 characters without splitting one. A name with nothing left
        // is the person's ID, so the bot never has to refuse a request for its name.
        internal static string CleanName(string? name, ulong id)
        {
            var cleaned = new StringBuilder();
            var count = 0;

            foreach (var rune in Runes(name ?? ""))
            {
                if (count == NameLimit)
                    break;

                cleaned.Append(Rune.IsControl(rune) ? " " : rune.ToString());
                count++;
            }

            var text = cleaned.ToString();
            return string.IsNullOrWhiteSpace(text) ? "Discord user " + id.ToString(CultureInfo.InvariantCulture) : text;
        }

        // Each character, with half of a surrogate pair on its own read as U+FFFD
        static IEnumerable<Rune> Runes(string text)
        {
            for (var i = 0; i < text.Length;)
            {
                if (Rune.TryGetRuneAt(text, i, out var rune))
                {
                    yield return rune;
                    i += rune.Utf16SequenceLength;
                }
                else
                {
                    yield return Rune.ReplacementChar;
                    i++;
                }
            }
        }

        // Only what the request carries beyond its kind, its server and who asked, which the audit row has in columns of
        // its own: the channel, as text so a page never rounds it, and the text the request takes
        static string? Json(RequestFields fields)
        {
            var values = new Dictionary<string, string>();

            if (fields.ChannelId is { } channel)
                values["Channel"] = channel.ToString(CultureInfo.InvariantCulture);

            if (fields.Argument != null)
                values["Text"] = fields.Argument;

            return values.Count == 0 ? null : JsonSerializer.Serialize(values);
        }
    }

    internal static class BotRequestServices
    {
        public static IServiceCollection AddBotRequests(this IServiceCollection services)
        {
            services.AddTransient<BotRequests>();

            // One for the whole dashboard, so every page's sends take the same turns
            services.AddSingleton<PersonTurns>();
            services.AddTransient<RequestSender>();
            services.AddTransient<RequestBoxes>();
            return services;
        }
    }
}
