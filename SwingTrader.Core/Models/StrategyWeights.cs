using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

public class StrategyWeights : BaseEntity
{
    // The six GATE component weights — the backtestable technical blend that
    // decides Buy/Watch/Hold/Avoid. Normalised 0.0-1.0, must sum to 1.0 (see
    // Validate()). Sentiment and fundamental momentum are NOT here: they drive
    // the funnel's Forward score (sizing/veto), not entry selection.
    public decimal RsiWeight { get; set; } = 0.23m;
    public decimal MacdWeight { get; set; } = 0.12m;
    public decimal VolumeWeight { get; set; } = 0.28m;
    public decimal SetupQualityWeight { get; set; } = 0.16m;
    public decimal RelativeStrengthWeight { get; set; } = 0.14m;
    public decimal PriceLevelWeight { get; set; } = 0.07m;

    // Forward-score blend (funnel Phase F2/F3): how the forward-looking
    // components combine into the 0-10 Forward score that drives the per-regime
    // sizing tilt and veto. Configurable (was a hardcoded 0.60/0.40 in
    // ResearchConfig); must sum to 1.0. Deliberately NOT part of the gate blend
    // and NOT swept by the optimizer/A-B (the backtest doesn't exercise it).
    // Filing (FD2): the decayed 10-K/10-Q language-delta score. Most symbols
    // have no fresh scored filing on a given day - that contributes a neutral
    // 0.5 WITHOUT degrading the score (unlike sentiment/fundamental outages),
    // so the veto keeps working for the majority no-filing case.
    public decimal ForwardSentimentWeight { get; set; } = 0.45m;
    public decimal ForwardFundamentalWeight { get; set; } = 0.30m;
    public decimal ForwardFilingWeight { get; set; } = 0.25m;

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
        var gate = RsiWeight + MacdWeight + VolumeWeight +
                   SetupQualityWeight + RelativeStrengthWeight + PriceLevelWeight;
        if (Math.Abs(gate - 1.0m) > 0.001m)
            throw new InvalidOperationException(
                $"Gate weights must sum to 1.0 — got {gate:F4}. Adjust weights before saving.");

        var forward = ForwardSentimentWeight + ForwardFundamentalWeight + ForwardFilingWeight;
        if (Math.Abs(forward - 1.0m) > 0.001m)
            throw new InvalidOperationException(
                $"Forward weights must sum to 1.0 — got {forward:F4}. Adjust weights before saving.");
    }
}
