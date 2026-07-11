namespace SwingTrader.Agents.Monitor;

// The probation momentum-health ALGORITHM, extracted pure so the live service
// (MomentumHealthService, reading today's StockSignal) and the historic
// backtester (recomputing the same inputs from bars) run identical code -
// parity by construction, same pattern as the Phase 1 RS/price-level
// calculators. Null inputs score the neutral 0.5 ("never exit on missing
// data"), exactly the live behaviour.
public static class MomentumHealthCalculator
{
    public sealed record Outcome(
        decimal Score, string Verdict,
        decimal RsiDirectionScore, decimal VolumeScore, decimal PriceDirectionScore, decimal RelativeStrengthScore);

    public static Outcome Compute(
        decimal? rsiToday, decimal? rsiAtEntry,
        decimal? volumeRatio,
        decimal currentPrice, decimal entryPrice,
        decimal? relativeReturn,
        decimal threshold)
    {
        // ── Component 1: RSI direction (weight 0.30) ──────────────────────
        decimal rsiScore;
        if (rsiToday.HasValue)
        {
            var rsi = rsiToday.Value;
            var entryRsi = rsiAtEntry ?? rsi;
            var rising = rsi > entryRsi;
            if (rising && rsi >= 50) rsiScore = 1.00m;
            else if (rising) rsiScore = 0.50m;
            else rsiScore = 0.00m;
        }
        else rsiScore = 0.50m;

        // ── Component 2: Volume sustainability (weight 0.25) ──────────────
        decimal volumeScore;
        if (volumeRatio.HasValue)
        {
            var ratio = volumeRatio.Value;
            if (ratio >= 0.8m) volumeScore = 1.00m;
            else if (ratio >= 0.5m) volumeScore = 0.50m;
            else volumeScore = 0.00m;
        }
        else volumeScore = 0.50m;

        // ── Component 3: Price vs entry (weight 0.25) ─────────────────────
        decimal priceScore;
        if (currentPrice > 0 && entryPrice > 0)
        {
            var pctFromEntry = (currentPrice - entryPrice) / entryPrice * 100m;
            if (pctFromEntry >= 1.5m) priceScore = 1.00m;
            else if (pctFromEntry >= 0.0m) priceScore = 0.50m;
            else priceScore = 0.00m;
        }
        else priceScore = 0.50m;

        // ── Component 4: Relative to sector (weight 0.20) ─────────────────
        decimal relativeScore;
        if (relativeReturn.HasValue)
        {
            var rel = relativeReturn.Value;
            if (rel > 0.5m) relativeScore = 1.00m;
            else if (rel >= -0.5m) relativeScore = 0.50m;
            else relativeScore = 0.00m;
        }
        else relativeScore = 0.50m;

        var score = (rsiScore * 0.30m) + (volumeScore * 0.25m) + (priceScore * 0.25m) + (relativeScore * 0.20m);

        var verdict = score >= threshold + 0.25m ? "Confirmed"
            : score >= threshold ? "Borderline"
            : "Exit";

        return new Outcome(Math.Round(score, 3), verdict, rsiScore, volumeScore, priceScore, relativeScore);
    }
}
