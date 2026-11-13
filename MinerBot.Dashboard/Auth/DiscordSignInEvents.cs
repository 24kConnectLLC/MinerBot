using System.Globalization;
using System.Security.Claims;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Audit.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Services;

namespace MinerBot.Dashboard.Auth
{
    // What happens between Discord's answer and a session: read the user, revoke the token, and decide. A refused or
    // failed sign-in ends on the denied page with a reason, and never gets a cookie or a session.
    internal sealed class DiscordSignInEvents(DiscordUserApi discord, RoleLists roles, DbTicketStore store, IAuditLogData audit,
        TimeProvider time, ILogger<DiscordSignInEvents> logger) : OAuthEvents
    {
        // In the request's items rather than the ticket's properties, which would be stored
        const string RefusalItem = "mb:refusal";
        const string UserItem = "mb:user";

        // A token endpoint failure that isn't one of OAuth's standard errors carries Discord's response headers and body,
        // cookies included, so that part is dropped
        const string HeadersMarker = ";Headers:";
        const int FailureTextLimit = 300;

        // How the OAuth handler starts the message of a standard error that Discord's token endpoint answered with
        const string TokenEndpointFailure = "OAuth token endpoint failure: ";

        // OAuth 2.0's error codes (RFC 6749, sections 4.1.2.1 and 5.2), the only error words worth logging. An error in
        // the callback's address can be written by anyone who started a sign-in, so its description and address never are.
        static readonly HashSet<string> StandardErrors = new(StringComparer.Ordinal)
        {
            "invalid_request", "unauthorized_client", "access_denied", "unsupported_response_type", "invalid_scope",
            "server_error", "temporarily_unavailable", "invalid_client", "invalid_grant", "unsupported_grant_type"
        };

        public override async Task CreatingTicket(OAuthCreatingTicketContext context)
        {
            var token = context.AccessToken!;
            var items = context.HttpContext.Items;

            // Every revoke goes ahead whether or not the visitor is still waiting, so going away mid-sign-in never leaves
            // the token working. A token with the wrong scope is used for nothing else.
            if (!DiscordSignIn.GrantedOnlyIdentify(context.TokenResponse))
            {
                await discord.RevokeAsync(token, CancellationToken.None);
                items[RefusalItem] = SignInRefusal.WrongScope;
                return;
            }

            DiscordUser user;

            try
            {
                user = await discord.GetCurrentUserAsync(token, context.HttpContext.RequestAborted);
            }
            catch (Exception e)
            {
                await discord.RevokeAsync(token, CancellationToken.None);

                if (e is DiscordSignInException)
                    throw;

                throw new DiscordSignInException($"Discord didn't say who signed in ({e.GetType().Name})");
            }

            // The token has done its one job, so Discord forgets it now rather than in a week. It's never used again.
            await discord.RevokeAsync(token, CancellationToken.None);

            var identity = context.Identity!;
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)));
            identity.AddClaim(new Claim(ClaimTypes.Name, user.DisplayName));
            identity.AddClaim(new Claim(DashboardAuth.MfaClaim, user.MfaEnabled ? "true" : "false"));
            items[UserItem] = user;

            // The lists first, so someone who isn't on them learns nothing about the two-factor rule
            if (roles.RoleOf(user.Id) == DashboardRole.None)
                items[RefusalItem] = SignInRefusal.Unlisted;
            else if (!user.MfaEnabled)
                items[RefusalItem] = SignInRefusal.NoTwoFactor;
        }

        public override async Task TicketReceived(TicketReceivedContext context)
        {
            var httpContext = context.HttpContext;

            if (httpContext.Items[RefusalItem] is SignInRefusal refusal and not SignInRefusal.None)
            {
                await AuditRefusalAsync(httpContext, refusal, httpContext.Items[UserItem] as DiscordUser);

                // Handled here, so the handler never signs in, and no cookie or session is made
                context.Response.Redirect(refusal.DeniedPath());
                context.HandleResponse();
                return;
            }

            // The state is protected, so this is the address /signin checked, but it's checked again on the way out
            context.ReturnUri = ReturnUrls.Pick(context.ReturnUri);
            context.Properties!.IsPersistent = false;

            await store.ForgetCurrentAsync(httpContext);
        }

        // The visitor pressed Cancel at Discord, after the state and the correlation cookie had passed
        public override Task AccessDenied(AccessDeniedContext context)
        {
            context.Response.Redirect(SignInRefusal.Cancelled.DeniedPath());
            context.HandleResponse();
            return Task.CompletedTask;
        }

        // A bad or missing state or correlation cookie, a failed code exchange or timeout, or Discord not saying who signed in
        public override Task RemoteFailure(RemoteFailureContext context)
        {
            logger.LogWarning("A Discord sign-in didn't finish: {Reason}", Describe(context.Failure));

            context.Response.Redirect(SignInRefusal.Failed.DeniedPath());
            context.HandleResponse();
            return Task.CompletedTask;
        }

        internal static string Describe(Exception? failure)
        {
            if (failure == null)
                return "no reason given";

            // An OAuth error, from Discord's token endpoint or from the callback's address: its code alone
            if (failure.Data["error"] is string error)
            {
                var code = StandardErrors.Contains(error) ? error : "an error OAuth doesn't define";

                return failure.Message.StartsWith(TokenEndpointFailure, StringComparison.Ordinal)
                    ? "Discord's token endpoint answered " + code
                    : "the callback's address carried " + code;
            }

            var text = failure.Message;
            var headers = text.IndexOf(HeadersMarker, StringComparison.Ordinal);

            if (headers >= 0)
                text = text[..headers];

            if (text.Length > FailureTextLimit)
                text = text[..FailureTextLimit];

            // On one line, so nothing in it can start a log line of its own
            return string.Create(text.Length, text, (chars, source) =>
            {
                for (var i = 0; i < source.Length; i++)
                    chars[i] = char.IsControl(source[i]) ? '?' : source[i];
            });
        }

        Task AuditRefusalAsync(HttpContext httpContext, SignInRefusal refusal, DiscordUser? user)
        {
            // The role the lists give someone without two-factor, and visitor for everyone else
            var role = refusal == SignInRefusal.NoTwoFactor && user != null ? roles.RoleOf(user.Id).Word() : AuditRole.Visitor;

            var entry = new AuditEntry
            {
                at = time.GetUtcNow().UtcDateTime,
                actor_id = user?.Id,
                actor_name = user?.DisplayName,
                actor_role = role,
                action = AuditWords.Refused,
                outcome = AuditOutcome.Refused,
                target_type = AuditWords.SessionTarget,
                detail = refusal.Detail(),
                ip = httpContext.Connection.RemoteIpAddress?.ToString(),
                request_id = httpContext.TraceIdentifier
            };

            return DatabaseCalls.Guard(() => audit.AddAudit(entry), logger);
        }
    }
}
