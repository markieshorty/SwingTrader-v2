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
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var heartbeats = await db.WorkerHeartbeats.ToListAsync(cancellationToken);
        if (heartbeats.Count == 0)
            return HealthCheckResult.Healthy("No workers registered yet");

        // Cadence-aware (see WorkerCadence): the previous flat 25h threshold
        // flagged the weekly workers (CandleSync, Watchlist) stale for most
        // of every week, keeping this check permanently Degraded.
        // Heartbeats are per-account rows; a worker counts as alive if ANY
        // account has a fresh row (legacy system-account rows go stale by
        // design once per-account rows take over).
        var now = DateTime.UtcNow;
        var stale = heartbeats
            .GroupBy(h => h.WorkerName)
            .Where(g => Services.WorkerCadence.IsStale(g.Key, g.Max(h => h.LastHeartbeatAt), now))
            .Select(g => g.Key)
            .ToList();

        return stale.Count == 0
            ? HealthCheckResult.Healthy("All workers reporting")
            : HealthCheckResult.Degraded($"Stale heartbeat: {string.Join(", ", stale)}");
    }
}
