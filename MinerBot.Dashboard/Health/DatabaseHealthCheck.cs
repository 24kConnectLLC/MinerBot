using DataAccessLibrary;
using DataAccessLibrary.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MinerBot.Dashboard.Health
{
    // Whether one database answers within a short limit. Unhealthy carries no description or exception, so nothing of
    // the database can reach the endpoint's answer.
    internal sealed class DatabaseHealthCheck(IDatabaseHealthData databases, DBConnectionName database) : IHealthCheck
    {
        internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await databases.Answers(database, Limit).WaitAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
    }
}
