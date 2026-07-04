using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SwingTrader.Data;

namespace SwingTrader.Api.HealthChecks;

// Reports Degraded once any registered worker's heartbeat is stale, rather than
// Unhealthy — a slow/skipped worker shouldn't take the whole API off the
// load balancer's rotation. No heartbeat rows yet (brand-new deployment) is
// reported Healthy, since no workers means nothing to be behind on.
public class WorkerHealthCheck(SwingTraderDbContext db) : IHealthCheck
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(25);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var heartbeats = await db.WorkerHeartbeats.ToListAsync(cancellationToken);
        if (heartbeats.Count == 0)
            return HealthCheckResult.Healthy("No workers registered yet");

        var stale = heartbeats
            .Where(h => DateTime.UtcNow - h.LastHeartbeatAt > StaleAfter)
            .Select(h => h.WorkerName)
            .ToList();

        return stale.Count == 0
            ? HealthCheckResult.Healthy("All workers reporting")
            : HealthCheckResult.Degraded($"Stale heartbeat: {string.Join(", ", stale)}");
    }
}
