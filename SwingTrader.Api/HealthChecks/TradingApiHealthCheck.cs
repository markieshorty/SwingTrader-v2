using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SwingTrader.Api.HealthChecks;

// Placeholder — becomes a real Trading212 connectivity check once
// SwingTrader.Infrastructure's Trading212Client is ported in a later phase.
public class TradingApiHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(HealthCheckResult.Healthy("Trading212 client not yet implemented"));
}
