using SwingTrader.Infrastructure.HttpClients.Dtos;

namespace SwingTrader.Agents.Monitor;

public interface IPortfolioCircuitBreakerService
{
    // Takes an already-fetched summary (rather than an ITrading212Client)
    // so MonitorService can fetch account/summary once per cycle and share
    // it with UpdateSnapshotAsync, instead of each hitting T212 separately -
    // T212's rate limit is tight enough that doubling up on this endpoint
    // every 5-minute cycle was a meaningful contributor to 429s.
    Task<bool> ShouldTriggerAsync(int accountId, T212AccountSummary? summary, CancellationToken ct = default);
}
