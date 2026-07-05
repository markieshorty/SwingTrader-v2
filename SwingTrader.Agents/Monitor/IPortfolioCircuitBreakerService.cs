using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Agents.Monitor;

public interface IPortfolioCircuitBreakerService
{
    Task<bool> ShouldTriggerAsync(int accountId, ITrading212Client t212, CancellationToken ct = default);
}
