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

        var profile = await riskProfileRepo.GetAsync(accountId, ct);

        // Shared algorithm (MomentumHealthCalculator) - the exact same code
        // the historic backtester's probation simulation runs, so live and
        // backtest verdicts can never drift apart.
        var outcome = MomentumHealthCalculator.Compute(
            rsiToday: signal.Rsi14,
            rsiAtEntry: entrySignal?.Rsi14,
            volumeRatio: signal.VolumeRatio,
            currentPrice: signal.CurrentPrice,
            entryPrice: trade.EntryPrice,
            relativeReturn: signal.RelativeReturn,
            threshold: profile.MomentumHealthThreshold);

        var (score, verdict, rsiScore, volumeScore, priceScore, relativeScore) =
            (outcome.Score, outcome.Verdict, outcome.RsiDirectionScore, outcome.VolumeScore,
             outcome.PriceDirectionScore, outcome.RelativeStrengthScore);

        decimal? pctFromEntry = signal.CurrentPrice > 0 && trade.EntryPrice > 0
            ? (signal.CurrentPrice - trade.EntryPrice) / trade.EntryPrice * 100m
            : null;

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
