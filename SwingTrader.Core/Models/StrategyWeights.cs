using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

public class StrategyWeights : BaseEntity
{
    // Normalised 0.0-1.0 weights — must sum to 1.0 (see Validate()).
    public decimal RsiWeight { get; set; } = 0.17m;
    public decimal MacdWeight { get; set; } = 0.09m;
    public decimal VolumeWeight { get; set; } = 0.21m;
    public decimal SentimentWeight { get; set; } = 0.16m;
    public decimal SetupQualityWeight { get; set; } = 0.12m;
    public decimal RelativeStrengthWeight { get; set; } = 0.10m;
    public decimal PriceLevelWeight { get; set; } = 0.05m;
    public decimal FundamentalMomentumWeight { get; set; } = 0.10m;
    public decimal BuyThreshold { get; set; } = 6.0m;
    public decimal WatchThreshold { get; set; } = 5.0m;
    public decimal StopLossPctDefault { get; set; } = 0.05m;
    public bool IsActive { get; set; }
    public string Source { get; set; } = "Default";
    public string? Notes { get; set; }

    // null = general weights, applies to any regime without a specific active row
    public MarketRegime? ApplicableRegime { get; set; }

    public void Validate()
    {
        var total = RsiWeight + MacdWeight + VolumeWeight + SentimentWeight +
                    SetupQualityWeight + RelativeStrengthWeight + PriceLevelWeight + FundamentalMomentumWeight;
        if (Math.Abs(total - 1.0m) > 0.001m)
            throw new InvalidOperationException(
                $"StrategyWeights must sum to 1.0 — got {total:F4}. Adjust weights before saving.");
    }
}
