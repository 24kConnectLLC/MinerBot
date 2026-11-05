using DataAccessLibrary;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace MinerBot.Dashboard.Health
{
    // /health/live says the process answers, for the container's health check. /health/ready also asks both databases,
    // for the deploy. Each answers Healthy or Unhealthy and nothing more, and only on Host localhost, so they're never
    // public even with the proxy key.
    internal static class HealthEndpoints
    {
        public const string LivePath = "/health/live";
        public const string ReadyPath = "/health/ready";

        const string ReadyTag = "ready";

        // Each check's own limit, a little over the database probe's, so one hung database can't hold the answer
        static readonly TimeSpan CheckLimit = TimeSpan.FromSeconds(5);

        public static IServiceCollection AddDashboardHealthChecks(this IServiceCollection services)
        {
            services.AddHealthChecks()
                .AddTypeActivatedCheck<DatabaseHealthCheck>("website database", null, [ReadyTag], CheckLimit, DBConnectionName.Armor)
                .AddTypeActivatedCheck<DatabaseHealthCheck>("bot database", null, [ReadyTag], CheckLimit, DBConnectionName.Bot);

            return services;
        }

        // The default writer, so the body is exactly Healthy or Unhealthy, as text, with 200 or 503
        public static void MapDashboardHealthChecks(this IEndpointRouteBuilder endpoints)
        {
            Map(endpoints, LivePath, new HealthCheckOptions { Predicate = _ => false });
            Map(endpoints, ReadyPath, new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) });
        }

        // Reads only, so they take GET and HEAD alone, and every endpoint that takes anything else is a Razor page with
        // the antiforgery check
        static void Map(IEndpointRouteBuilder endpoints, string path, HealthCheckOptions options) =>
            endpoints.MapHealthChecks(path, options)
                .RequireHost("localhost")
                .AllowAnonymous()
                .WithMetadata(new HttpMethodMetadata([HttpMethods.Get, HttpMethods.Head]));
    }
}
