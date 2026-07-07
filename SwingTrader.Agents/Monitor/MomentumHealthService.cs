using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Monitor;

public class MomentumHealthService(
    ISignalRepository signalRepo,
    IAccountRiskProfileRepository riskProfileRepo) : IMomentumHealthService
{
    public async Task<MomentumHealthResult> CalculateAsync(
        int accountId,
        Trade trade,
        CancellationToken ct = default)
    {
        var signal = (await signalRepo.GetBySymbolAsync(accountId, trade.Symbol)).FirstOrDefault();

        if (signal is null)
        {
            // Research hasn't run yet today, or the symbol left every watchlist.
            // Neutral score — never exit a position on missing data.
            return new MomentumHealthResult(
                Score: 0.50m,
                Verdict: "Borderline",
                Reasoning: "Insufficient data for momentum assessment — holding position",
                RsiDirectionScore: 0.50m,
                VolumeScore: 0.50m,
                PriceDirectionScore: 0.50m,
                RelativeStrengthScore: 0.50m);
        }

        var entrySignal = trade.SignalId.HasValue
            ? await signalRepo.GetByIdAsync(accountId, trade.SignalId.Value)
            : null;

        // ── Component 1: RSI Direction (weight 0.30) ──────────────────────
        decimal rsiScore;
        if (signal.Rsi14.HasValue)
        {
            var rsi = signal.Rsi14.Value;
            var entryRsi = entrySignal?.Rsi14 ?? rsi;
            var rising = rsi > entryRsi;

            if (rising && rsi >= 50) rsiScore = 1.00m;
            else if (rising) rsiScore = 0.50m;
            else rsiScore = 0.00m;
        }
        else
        {
            rsiScore = 0.50m;
        }

        // ── Component 2: Volume Sustainability (weight 0.25) ──────────────
        decimal volumeScore;
        if (signal.VolumeRatio.HasValue)
        {
            var ratio = signal.VolumeRatio.Value;
            if (ratio >= 0.8m) volumeScore = 1.00m;
            else if (ratio >= 0.5m) volumeScore = 0.50m;
            else volumeScore = 0.00m;
        }
        else
        {
            volumeScore = 0.50m;
        }

        // ── Component 3: Price vs Entry (weight 0.25) ─────────────────────
        decimal priceScore;
        decimal? pctFromEntry = null;
        if (signal.CurrentPrice > 0 && trade.EntryPrice > 0)
        {
            pctFromEntry = (signal.CurrentPrice - trade.EntryPrice) / trade.EntryPrice * 100m;
            if (pctFromEntry >= 1.5m) priceScore = 1.00m;
            else if (pctFromEntry >= 0.0m) priceScore = 0.50m;
            else priceScore = 0.00m;
        }
        else
        {
            priceScore = 0.50m;
        }

        // ── Component 4: Relative to Sector (weight 0.20) ─────────────────
        decimal relativeScore;
        if (signal.RelativeReturn.HasValue)
        {
            var rel = signal.RelativeReturn.Value;
            if (rel > 0.5m) relativeScore = 1.00m;
            else if (rel >= -0.5m) relativeScore = 0.50m;
            else relativeScore = 0.00m;
        }
        else
        {
            relativeScore = 0.50m;
        }

        var score = (rsiScore * 0.30m) + (volumeScore * 0.25m) + (priceScore * 0.25m) + (relativeScore * 0.20m);

        var profile = await riskProfileRepo.GetAsync(accountId, ct);
        var threshold = profile.MomentumHealthThreshold;

        string verdict;
        if (score >= threshold + 0.25m) verdict = "Confirmed";
        else if (score >= threshold) verdict = "Borderline";
        else verdict = "Exit";

        var reasoning = BuildReasoning(verdict, rsiScore, volumeScore, priceScore, relativeScore, pctFromEntry);

        return new MomentumHealthResult(Math.Round(score, 3), verdict, reasoning, rsiScore, volumeScore, priceScore, relativeScore);
    }

    private static string BuildReasoning(
        string verdict, decimal rsiScore, decimal volumeScore, decimal priceScore, decimal relativeScore, decimal? pctFromEntry)
    {
        var parts = new List<string>();

        if (rsiScore >= 0.75m) parts.Add("RSI rising above 50");
        else if (rsiScore == 0) parts.Add("RSI falling");
        else parts.Add("RSI flat");

        if (volumeScore >= 0.75m) parts.Add("volume sustaining");
        else if (volumeScore == 0) parts.Add("volume faded");

        if (priceScore >= 0.75m && pctFromEntry.HasValue) parts.Add($"+{Math.Round(pctFromEntry.Value, 1)}% from entry");
        else if (priceScore == 0) parts.Add("price below entry");

        if (relativeScore >= 0.75m) parts.Add("outperforming sector");
        else if (relativeScore == 0) parts.Add("underperforming sector");

        return verdict switch
        {
            "Confirmed" => $"Thesis confirmed — {string.Join(", ", parts)}.",
            "Borderline" => $"Mixed signals — {string.Join(", ", parts)}. One more day to prove direction.",
            _ => $"Thesis not playing out — {string.Join(", ", parts)}.",
        };
    }
}
