using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SwingTrader.Api.HealthChecks;

// Reports Unhealthy on the "ready" probe until startup migrations have applied
// cleanly. Reads the cached MigrationState flag (no per-probe DB query), so it
// costs nothing on the Basic-tier DB. Keeps a revision with failed/partial
// migrations from receiving traffic.
public sealed class MigrationHealthCheck(MigrationState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.Applied
            ? HealthCheckResult.Healthy("Migrations applied")
            : HealthCheckResult.Unhealthy($"Startup migrations have not applied: {state.Error ?? "in progress or failed"}"));
}
