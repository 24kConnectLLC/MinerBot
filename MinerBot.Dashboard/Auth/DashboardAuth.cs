using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Services;
using MinerBot.Dashboard.Settings;

namespace MinerBot.Dashboard.Auth
{
    // Sign-in with Discord, the session cookie, the roles and the forms' antiforgery, all with ASP.NET Core's own handlers
    internal static class DashboardAuth
    {
        public const string CookieScheme = "Session";
        public const string DiscordScheme = "Discord";

        // A __Host- cookie must be Secure, with Path=/ and no Domain, so murderminershub.com, where WordPress runs, can
        // neither set nor replace it
        public const string SessionCookie = "__Host-mb";
        public const string AntiforgeryCookie = "__Host-mb-af";
        public const string CorrelationCookiePrefix = "__Host-mb-signin.";

        // A form's notice, which FormNotices keeps for the redirect's GET to draw
        public const string NoticeCookie = "__Host-mb-notice";

        // Whether the account had Discord two-factor on when it signed in
        public const string MfaClaim = "mb:mfa";

        public const string CallbackPath = "/signin-discord";

        public static IServiceCollection AddDashboardSignIn(this IServiceCollection services)
        {
            // Someone without a session is sent to /signin, never straight to Discord, so the return address is checked first
            services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieScheme;
                options.DefaultChallengeScheme = CookieScheme;
            })
            .AddCookie(CookieScheme, ConfigureCookie)
            .AddOAuth(DiscordScheme, "Discord", _ => { });

            services.AddOptions<OAuthOptions>(DiscordScheme).Configure<IOptions<DiscordSignInOptions>>((oauth, settings) => DiscordSignIn.Configure(oauth, settings.Value));
            services.AddOptions<CookieAuthenticationOptions>(CookieScheme).Configure<DbTicketStore>((cookie, store) => cookie.SessionStore = store);

            services.AddSingleton<DbTicketStore>();
            services.AddScoped<DiscordSignInEvents>();

            // A handler that follows no redirects, so a token never goes anywhere it wasn't sent, and no loggers, which would
            // log each request's headers
            services.AddHttpClient<DiscordUserApi>(DiscordUserApi.Configure)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
                .RemoveAllLoggers();

            // The roles come from the lists on every request, and only from there
            services.AddSingleton<RoleLists>();
            services.AddTransient<IClaimsTransformation, RoleClaims>();
            Policies.Add(services.AddAuthorizationBuilder());

            services.AddAntiforgery(options =>
            {
                options.Cookie.Name = AntiforgeryCookie;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.Path = "/";
                options.Cookie.HttpOnly = true;

                // Lax, not Strict: Strict would leave it off every arrival from another site, Discord's return from a
                // sign-in included, and the new one written then would expire every form already open in another tab.
                // Neither is sent with a cross-site POST, and OriginCheck and the token stop the rest either way.
                options.Cookie.SameSite = SameSiteMode.Lax;

                // Antiforgery would add X-Frame-Options: SAMEORIGIN to every page with a form, and SecurityHeaders sends DENY
                options.SuppressXFrameOptionsHeader = true;
            });

            return services;
        }

        static void ConfigureCookie(CookieAuthenticationOptions options)
        {
            options.Cookie.Name = SessionCookie;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.Path = "/";
            options.Cookie.IsEssential = true;

            // Lax, so the first page after Discord sends the browser back is signed in. Lax is no defence against
            // murderminershub.com, which browsers count as the same site, so OriginCheck and antiforgery are.
            options.Cookie.SameSite = SameSiteMode.Lax;

            // Never persistent, so the cookie has no Expires and ends with the browser. The session itself ends in the
            // database after 30 minutes unused or 12 hours in all, and its end never slides.
            options.ExpireTimeSpan = DbTicketStore.Lifetime;
            options.SlidingExpiration = false;

            options.LoginPath = "/signin";
            options.LogoutPath = "/signout";
            options.AccessDeniedPath = "/denied";
            options.ReturnUrlParameter = "returnUrl";

            // Without a session, a GET or a HEAD goes to /signin as before, which goes on to Discord: a page's with a 302, and
            // refresh.js's, which asks with X-Requested-With, with the default's 401 and the address, after which it stops
            // with its note. Any other method is a form sent after the session ended, whose redirect couldn't go on to
            // Discord, so SessionEnded ends it on this site's page that says so.
            var toSignIn = options.Events.OnRedirectToLogin;
            options.Events.OnRedirectToLogin = context =>
                HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) ? toSignIn(context) : SessionEnded.RedirectAsync(context);

            // The cookie holds only the session's random key, and the ticket stays in the database
            options.TicketDataFormat = new SessionKeyCookieFormat();
        }
    }
}
