using DataAccessLibrary;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Health;
using MinerBot.Dashboard.Hosting;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Settings;

// The deploy's migration step and the container's health check need none of the web settings, and must never try to
// take the port the running dashboard holds
if (DashboardCommands.Find(args) is { } command)
    return await DashboardCommands.RunAsync(command, args);

// The pages anyone may open, kept as one visible list. Every other page is under /Admin, and a test fails when a page
// is neither on this list nor under a policy.
string[] publicPages = ["/Index", "/SignIn", "/SignOut", "/Denied", "/Error"];

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDashboardSettings();
builder.Logging.AddDashboardFilters();
builder.Services.AddDashboardHosting();

builder.Services.AddDataAccessServices(builder.Configuration)
                .AddAuditLogAccess()
                .AddDashboardSessionAccess()
                .AddDatabaseHealthAccess();

builder.Services.AddDashboardSignIn();

// No fallback policy: it would also apply to addresses that match no page and turn every 404 into a sign-in. Deny by
// default is the endpoint test's job instead.
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Admin", Policies.Admin);

    foreach (var page in publicPages)
        options.Conventions.AllowAnonymousToPage(page);
});

builder.Services.Configure<RouteOptions>(options => options.LowercaseUrls = true);
builder.Services.AddDashboardHealthChecks();

var app = builder.Build();

// The host checks the settings as it starts, but MapRazorPages below already reads them, through Data Protection's keys
// folder, so the same check comes first. A setting that's missing or wrong stops the dashboard here, before anything
// listens, with a plain list and exit code 1 instead of a stack trace.
if (DashboardHosting.StoppedBySettings(app.Services, Console.Error))
    return 1;

// The pipeline, in the order that matters.
// 0. ASP.NET Core's own host filtering runs before everything here. It admits only the public host name and localhost,
//    because Discord's redirect address is built from the Host header. Any other host gets an empty 400, without the
//    headers and before the key below, which only something on the server itself can see, since nginx sends the public name.

// 1. Security headers go on every response, including the bare 404s below. They're set as the response starts, so nothing
//    later can drop them.
app.UseMiddleware<SecurityHeaders>();

// 2. Only nginx knows the proxy key, so anything else on the server that calls Kestrel gets a bare 404. The container's
//    health check, on Host localhost, needs no key.
app.UseMiddleware<ProxyKeyMiddleware>();

// 3. The visitor's address and https are trusted only after the key has shown that nginx sent them: one hop, from
//    loopback only, and never X-Forwarded-Host.
app.UseForwardedHeaders();

// 4. A generic page with a request ID, written directly instead of running the pipeline again, so a database outage can't
//    break the error page too. The middleware's own log line holds the exception's message, which for the database
//    driver names its login and host, so it's suppressed, and ErrorResponses logs without the message.
app.UseExceptionHandler(new ExceptionHandlerOptions { ExceptionHandler = ErrorResponses.WriteAsync, SuppressDiagnosticsCallback = _ => true });

// 5. Limits by the visitor's address. Before sign-in, because Discord's callback is answered inside UseAuthentication,
//    and every sign-in costs Discord calls from the address the bot also uses.
app.UseRateLimiter();

// 6. Plain pages for 404, 403, 400 and 405. After the limits, so showing one never counts twice.
app.UseStatusCodePagesWithReExecute("/error");

// 7. A change must come from the dashboard's own pages. Browsers treat murderminershub.com as the same site, so SameSite
//    cookies alone can't stop it.
app.UseMiddleware<OriginCheck>();

// 8. The stylesheet comes before sign-in, so it costs no session lookup and still loads while the database is down.
app.UseStaticFiles(StaticFiles.Options);

// 9. Which page or endpoint the request is for.
app.UseRouting();

// 10. The session comes from the database, which ends it there if it's unused, too old or off the lists. Discord's
//     callback at /signin-discord is also handled here.
app.UseAuthentication();

// 11. Each page's policy, with the role worked out from the lists on this request.
app.UseAuthorization();

// 12. The pages, and the health endpoints, which answer only on Host localhost.
app.MapRazorPages();
app.MapDashboardHealthChecks();

// Left out on purpose:
// - UseHttpsRedirection and UseHsts: nginx and Cloudflare handle https, and UseHsts skips localhost, so SecurityHeaders
//   sends HSTS itself.
// - CORS: no other site may call the dashboard.
// - Response compression: pages hold secrets next to text visitors control.
// - HTTP logging: it would log cookies and Discord's codes.
// - MapStaticAssets: UseStaticFiles serves wwwroot the same way in the tests and in the container, with no build manifest.
// - A fallback policy: see AddRazorPages above.

// 13. Runs the app until the host stops it.
await app.RunAsync();
return 0;
