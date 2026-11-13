using System.Data.Common;
using System.Security.Claims;
using System.Security.Cryptography;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Dashboard.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Security;

namespace MinerBot.Dashboard.Auth
{
    // The sessions behind the cookie, in MinerBot's own database. The cookie holds only a random key, and the row holds
    // the key's hash and the ticket, encrypted with Data Protection under a purpose that includes the key, so a ticket
    // opens only for its own cookie. Every limit is enforced here, on the server, from the row: 30 minutes unused, 12 hours
    // in all, at most 5 sessions a person, and only for someone on the lists right now.
    internal sealed class DbTicketStore(IDashboardSessionData sessions, IAuditLogData audit, IDataProtectionProvider protection,
        RoleLists roles, TimeProvider time, ILogger<DbTicketStore> logger) : ITicketStore
    {
        public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(30);
        public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

        // last_seen_at is written at most this often, so each signed-in request costs one read and a write a minute
        public static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);

        public const int MaxSessionsPerPerson = 5;
        public const string TicketPurpose = "MinerBot.Dashboard.SessionTicket.v1";

        readonly IDataProtector tickets = protection.CreateProtector(TicketPurpose);

        // A new sign-in. Everything is worked out before the transaction, because it may run again after a deadlock and
        // must change nothing but the database.
        public async Task<string> StoreAsync(AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken)
        {
            var user = SignedInUser.From(ticket.Principal) ?? throw new InvalidOperationException("A session is only stored for a Discord sign-in");
            var role = user.MfaEnabled ? roles.RoleOf(user.Id) : DashboardRole.None;

            if (role == DashboardRole.None)
                throw new InvalidOperationException("Only the dashboard's Owners and Admins, with two-factor on, get a session");

            var key = SessionKeys.New();
            var now = time.GetUtcNow();
            var blob = Protect(key, user, now);

            var row = new DashboardSession
            {
                user_id = user.Id,
                user_name = user.Name,
                created_at = now.UtcDateTime,
                last_seen_at = now.UtcDateTime,
                expires_at = (now + Lifetime).UtcDateTime,
                ip = httpContext.Connection.RemoteIpAddress?.ToString(),
                user_agent = httpContext.Request.Headers.UserAgent.ToString(),
                ticket = blob
            };

            var signedIn = Entry(httpContext, now, user.Id, user.Name, role.Word(), AuditWords.SignIn, AuditOutcome.Ok, null);

            // The session, the cap on how many one person has, and the audit row commit together, or none of them does
            await DatabaseCalls.Guard(() => sessions.RunInTransaction(async transaction =>
            {
                await sessions.CreateSession(key, row, transaction);
                await sessions.DeleteOldestSessions(user.Id, MaxSessionsPerPerson, transaction);
                await audit.AddAudit(signedIn, transaction);
                return true;
            }), logger);

            // Ended sessions go at each sign-in, since no daily job exists yet: those past their 12 hours, and those unused for
            // longer than the idle limit, which RetrieveAsync would refuse anyway. Best effort: the sign-in has already happened.
            try
            {
                await sessions.DeleteExpiredSessions(now.UtcDateTime, (now - IdleLimit).UtcDateTime);
            }
            catch (Exception e) when (e is DbException or TimeoutException)
            {
                logger.LogWarning("Ended sessions couldn't be deleted at this sign-in ({Type}), so the next one tries again", e.GetType().Name);
            }

            return key;
        }

        // Called once for each request that carries the cookie
        public async Task<AuthenticationTicket?> RetrieveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken)
        {
            // A made-up or broken cookie never reaches the database
            if (!SessionKeys.IsWellFormed(key))
                return null;

            var now = time.GetUtcNow();
            var session = await DatabaseCalls.Guard(() => sessions.GetSession(key, now.UtcDateTime), logger);

            // No such session, or past its 12 hours, which the read itself checks
            if (session == null)
                return null;

            var lastSeen = Utc(session.last_seen_at);

            if (now - lastSeen >= IdleLimit)
            {
                await DatabaseCalls.Guard(() => sessions.DeleteSession(key), logger);
                return null;
            }

            // Taken off the lists since signing in: every session of theirs ends now, not only this one
            if (roles.RoleOf(session.user_id) == DashboardRole.None)
            {
                await EndForUnlistedAsync(session, httpContext, now);
                return null;
            }

            var ticket = Open(key, session.ticket);

            if (ticket == null || SignedInUser.From(ticket.Principal)?.Id != session.user_id)
            {
                await DatabaseCalls.Guard(() => sessions.DeleteSession(key), logger);
                return null;
            }

            if (now - lastSeen >= TouchEvery && !await DatabaseCalls.Guard(() => sessions.TouchSession(key, now.UtcDateTime), logger))
                return null;

            // The row's times, never the ticket's, and no refresh, so the end can't slide. MySql.Data reads DATETIME back
            // with no kind, which would otherwise be taken as local time.
            ticket.Properties.IssuedUtc = Utc(session.created_at);
            ticket.Properties.ExpiresUtc = Utc(session.expires_at);
            ticket.Properties.AllowRefresh = false;

            SessionKeys.Remember(httpContext, key);
            return ticket;
        }

        // Only if something asks for a refresh, which nothing does with sliding expiration off. The session's end stays where
        // the sign-in put it.
        public async Task RenewAsync(string key, AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken)
        {
            if (!SessionKeys.IsWellFormed(key) || SignedInUser.From(ticket.Principal) is not { } user)
                return;

            var blob = Protect(key, user, ticket.Properties.IssuedUtc ?? time.GetUtcNow());
            await DatabaseCalls.Guard(() => sessions.RenewSession(key, blob), logger);
        }

        // The cookie handler calls this at sign-out and for a ticket past its end, so it's never audited. Sign-out's audit
        // row comes from EndAsync, which has already deleted the row by then.
        public async Task RemoveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken)
        {
            if (SessionKeys.IsWellFormed(key))
                await DatabaseCalls.Guard(() => sessions.DeleteSession(key), logger);
        }

        // Sign-out: the session and its audit row go together. One that finds the session already gone, such as the second
        // of two at once, writes nothing: its delete waits for the first one's and then finds no row.
        public Task EndAsync(string key, ClaimsPrincipal user, HttpContext httpContext)
        {
            var signedIn = SignedInUser.From(user);
            var entry = Entry(httpContext, time.GetUtcNow(), signedIn?.Id, signedIn?.Name, RoleClaims.RoleOf(user).Word(), AuditWords.SignOut, AuditOutcome.Ok, null);

            return DatabaseCalls.Guard(() => sessions.RunInTransaction(async transaction =>
            {
                if (await sessions.DeleteSession(key, transaction))
                    await audit.AddAudit(entry, transaction);

                return true;
            }), logger);
        }

        // A browser that's already signed in and signs in again gets a new key. The cookie handler would otherwise keep
        // the old key and renew its session in place, so its session ends here, before the handler looks for it.
        public async Task ForgetCurrentAsync(HttpContext httpContext)
        {
            var key = httpContext.Request.Cookies[DashboardAuth.SessionCookie];

            if (SessionKeys.IsWellFormed(key))
                await DatabaseCalls.Guard(() => sessions.DeleteSession(key!), logger);
        }

        async Task EndForUnlistedAsync(DashboardSession session, HttpContext httpContext, DateTimeOffset now)
        {
            var refused = Entry(httpContext, now, session.user_id, session.user_name, AuditRole.Visitor, AuditWords.Refused, AuditOutcome.Refused,
                "Taken off the dashboard's lists, so their sessions were ended");

            // Audited only when this request ended something, so two requests at once don't log one ending twice
            var ended = await DatabaseCalls.Guard(() => sessions.RunInTransaction(async transaction =>
            {
                var deleted = await sessions.DeleteOldestSessions(session.user_id, 0, transaction);

                if (deleted > 0)
                    await audit.AddAudit(refused, transaction);

                return deleted;
            }), logger);

            if (ended > 0)
                logger.LogInformation("A session ended because its user is no longer on the dashboard's lists");
        }

        // The stored ticket is rebuilt from the three claims a session needs, so neither a role nor a token can reach the
        // table, whatever the sign-in's principal carried
        byte[] Protect(string key, SignedInUser user, DateTimeOffset issued)
        {
            var stored = new AuthenticationTicket(user.ToPrincipal(), new AuthenticationProperties
            {
                IssuedUtc = issued,
                ExpiresUtc = issued + Lifetime,
                AllowRefresh = false
            }, DashboardAuth.CookieScheme);

            return tickets.CreateProtector(key).Protect(TicketSerializer.Default.Serialize(stored));
        }

        AuthenticationTicket? Open(string key, byte[] blob)
        {
            try
            {
                return TicketSerializer.Default.Deserialize(tickets.CreateProtector(key).Unprotect(blob));
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        static AuditEntry Entry(HttpContext httpContext, DateTimeOffset now, ulong? actorId, string? actorName, string role, string action, string outcome, string? detail) => new()
        {
            at = now.UtcDateTime,
            actor_id = actorId,
            actor_name = actorName,
            actor_role = role,
            action = action,
            outcome = outcome,
            target_type = AuditWords.SessionTarget,
            detail = detail,
            ip = httpContext.Connection.RemoteIpAddress?.ToString(),
            request_id = httpContext.TraceIdentifier
        };

        static DateTimeOffset Utc(DateTime time) => new(DateTime.SpecifyKind(time, DateTimeKind.Utc));

        // The cookie handler only calls the overloads with an HttpContext
        public Task<string> StoreAsync(AuthenticationTicket ticket) => throw new NotSupportedException();
        public Task RenewAsync(string key, AuthenticationTicket ticket) => throw new NotSupportedException();
        public Task<AuthenticationTicket?> RetrieveAsync(string key) => throw new NotSupportedException();
        public Task RemoveAsync(string key) => throw new NotSupportedException();
    }
}
