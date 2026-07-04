using Microsoft.Extensions.Diagnostics.HealthChecks;
using SwingTrader.Data;

namespace SwingTrader.Api.HealthChecks;

public class DatabaseHealthCheck(SwingTraderDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var canConnect = await db.Database.CanConnectAsync(cancellationToken);
        return canConnect
            ? HealthCheckResult.Healthy("Database reachable")
            : HealthCheckResult.Unhealthy("Cannot reach database");
    }
}
