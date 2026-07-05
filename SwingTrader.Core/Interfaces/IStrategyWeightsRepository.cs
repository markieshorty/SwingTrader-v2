using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IStrategyWeightsRepository
{
    // Seeds the default strategy weights row for a brand-new account.
    Task SeedDefaultAsync(int accountId, CancellationToken ct = default);

    Task<StrategyWeights?> GetActiveWeightsAsync(int accountId);

    // Returns the active row for the given regime if one exists, otherwise falls back
    // to the general (ApplicableRegime == null) active row.
    Task<StrategyWeights?> GetActiveWeightsAsync(int accountId, MarketRegime? regime);
    Task<StrategyWeights> AddAsync(StrategyWeights weights);
    Task SetActiveAsync(int accountId, int id);

    // Activates this row for its regime only — leaves the general row and other
    // regimes' active rows untouched.
    Task SetRegimeActiveAsync(int accountId, int id, MarketRegime regime);
}
