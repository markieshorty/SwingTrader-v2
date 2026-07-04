using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SwingTrader.Api.HealthChecks;

// Placeholder — becomes a real Claude API connectivity check once
// SwingTrader.Infrastructure's ClaudeClient is ported in a later phase.
public class ClaudeHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(HealthCheckResult.Healthy("Claude client not yet implemented"));
}
