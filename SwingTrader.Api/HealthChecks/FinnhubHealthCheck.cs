using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SwingTrader.Api.HealthChecks;

// Placeholder — becomes a real Finnhub connectivity check once
// SwingTrader.Infrastructure's FinnhubClient is ported in a later phase.
public class FinnhubHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(HealthCheckResult.Healthy("Finnhub client not yet implemented"));
}
