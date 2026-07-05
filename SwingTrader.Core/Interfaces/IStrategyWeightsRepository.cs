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

    // In-place edit of every tunable field on the active general row - a manual
    // testing/tuning knob (e.g. temporarily lowering BuyThreshold to exercise
    // the Execution path), not a Refinement-style versioned change. The 8
    // component weights must still sum to 1.0 (StrategyWeights.Validate()).
    Task UpdateWeightsAsync(int accountId, StrategyWeightsUpdate update);
}

public record StrategyWeightsUpdate(
    decimal RsiWeight,
    decimal MacdWeight,
    decimal VolumeWeight,
    decimal SentimentWeight,
    decimal SetupQualityWeight,
    decimal RelativeStrengthWeight,
    decimal PriceLevelWeight,
    decimal FundamentalMomentumWeight,
    decimal BuyThreshold,
    decimal WatchThreshold,
    decimal StopLossPctDefault);
