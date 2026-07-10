using SwingTrader.Api.Contracts;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Api.Services;

// "Own data" strategy simulation: replays the account's closed trades under a
// candidate dial configuration. Each trade's signal persisted its 8 component
// scores at research time, so conviction under ANY weight mix is exact - the
// simulation asks "which of the trades I actually took would these dials have
// taken, and how did that subset actually perform?"
//
// Honest scope limit (surfaced in the UI): it can only evaluate trades that
// WERE taken. Dials that would have taken different/extra trades can't be
// scored from own data - that's what the historic-market mode (backtester) is
// for. Own-data suggestions are therefore filters ("drop these kinds of
// trades"), never expansions.
public class StrategyLabService(
    ITradeRepository tradeRepo,
    ISignalRepository signalRepo,
    IAccountRepository accountRepo)
{
    private sealed record ReplayTrade(
        string Symbol, DateTime OpenedAt, SetupType Setup, decimal ReturnPct,
        decimal Rsi, decimal Macd, decimal Volume, decimal Sentiment,
        decimal SetupQuality, decimal RelativeStrength, decimal PriceLevel, decimal Fundamental);

    public async Task<StrategyLabResponse?> RunOwnDataAsync(int accountId, StrategyLabRequest req, CancellationToken ct)
    {
        var account = await accountRepo.GetAsync(accountId, ct);
        if (account is null) return null;

        var replayable = await LoadReplayableTradesAsync(accountId, account.TradingMode, ct);

        var result = Evaluate(replayable, req.Weights, req.BuyThreshold, req.ExcludeBreakout);
        var suggestions = Search(replayable, req, result);
        var trades = replayable
            .Select(t => new LabTradeOutcome(
                t.Symbol, t.OpenedAt,
                Conviction(t, req.Weights), t.Setup.ToString(), Math.Round(t.ReturnPct, 2),
                WouldTake(t, req.Weights, req.BuyThreshold, req.ExcludeBreakout)))
            .OrderByDescending(t => t.OpenedAt)
            .ToList();

        string? warning = replayable.Count switch
        {
            0 => "No closed trades with scored signals yet — the simulator needs live trade history to replay. Let the system trade first.",
            < 30 => $"Only {replayable.Count} replayable trades — results are indicative, not statistically meaningful. Treat suggestions with caution until 30+.",
            _ => null,
        };

        return new StrategyLabResponse(result, suggestions, trades, warning);
    }

    private async Task<List<ReplayTrade>> LoadReplayableTradesAsync(int accountId, TradingMode mode, CancellationToken ct)
    {
        var history = await tradeRepo.GetTradeHistoryAsync(accountId, mode, DateTime.UtcNow.AddYears(-2), DateTime.UtcNow);
        var replayable = new List<ReplayTrade>();

        foreach (var trade in history.Where(t =>
                     t.Status is not (TradeStatus.Open or TradeStatus.Pending or TradeStatus.Cancelled)
                     && t.RealizedPnl.HasValue && t.SignalId.HasValue && t.EntryPrice > 0 && t.Quantity > 0))
        {
            var signal = await signalRepo.GetByIdAsync(accountId, trade.SignalId!.Value);
            if (signal?.RsiScore is null) continue; // pre-component-score era signal

            var cost = trade.EntryPrice * trade.Quantity;
            replayable.Add(new ReplayTrade(
                trade.Symbol, trade.OpenedAt, signal.SetupType,
                trade.RealizedPnl!.Value / cost * 100m,
                signal.RsiScore ?? 0.5m, signal.MacdScore ?? 0.5m, signal.VolumeScore ?? 0.5m,
                signal.SentimentComponentScore ?? 0.5m, signal.SetupQualityScore ?? 0.5m,
                signal.RelativeStrengthScore ?? 0.5m, signal.PriceLevelScore ?? 0.5m,
                signal.FundamentalMomentumScore ?? 0.5m));
        }

        return replayable;
    }

    private static decimal Conviction(ReplayTrade t, LabWeights w)
    {
        var raw =
            w.Rsi * t.Rsi + w.Macd * t.Macd + w.Volume * t.Volume + w.Sentiment * t.Sentiment +
            w.SetupQuality * t.SetupQuality + w.RelativeStrength * t.RelativeStrength +
            w.PriceLevel * t.PriceLevel + w.FundamentalMomentum * t.Fundamental;
        return Math.Round(Math.Clamp(raw * 10m, 0m, 10m), 1);
    }

    private static bool WouldTake(ReplayTrade t, LabWeights w, decimal buyThreshold, bool excludeBreakout) =>
        (!excludeBreakout || t.Setup != SetupType.Breakout) && Conviction(t, w) >= buyThreshold;

    private static LabResult Evaluate(List<ReplayTrade> trades, LabWeights w, decimal threshold, bool excludeBreakout)
    {
        var kept = trades.Where(t => WouldTake(t, w, threshold, excludeBreakout)).ToList();
        var dropped = trades.Count - kept.Count;
        var droppedTrades = trades.Where(t => !WouldTake(t, w, threshold, excludeBreakout)).ToList();

        decimal AvgReturn(List<ReplayTrade> set) => set.Count > 0 ? Math.Round(set.Average(t => t.ReturnPct), 2) : 0m;
        decimal WinRate(List<ReplayTrade> set) => set.Count > 0 ? Math.Round((decimal)set.Count(t => t.ReturnPct > 0) / set.Count, 4) : 0m;

        var actualAvg = AvgReturn(trades);
        var simAvg = AvgReturn(kept);

        var summary = trades.Count == 0
            ? "Nothing to simulate yet."
            : $"Of your {trades.Count} closed trades, these dials would have taken {kept.Count} and skipped {dropped}. " +
              $"The skipped set contained {droppedTrades.Count(t => t.ReturnPct > 0)} winner(s) and {droppedTrades.Count(t => t.ReturnPct <= 0)} loser(s). " +
              (kept.Count == 0
                  ? "These dials would have filtered out every trade you took."
                  : simAvg > actualAvg
                      ? $"Average return per trade improves from {actualAvg:F2}% to {simAvg:F2}% — the dials filter more losers than winners."
                      : simAvg < actualAvg
                          ? $"Average return per trade drops from {actualAvg:F2}% to {simAvg:F2}% — the dials filter out more winners than losers."
                          : "Average return per trade is unchanged.");

        return new LabResult(
            trades.Count, kept.Count, dropped,
            droppedTrades.Count(t => t.ReturnPct > 0), droppedTrades.Count(t => t.ReturnPct <= 0),
            actualAvg, simAvg, WinRate(trades), WinRate(kept),
            Math.Round(trades.Sum(t => t.ReturnPct), 2), Math.Round(kept.Sum(t => t.ReturnPct), 2),
            summary);
    }

    // Bounded neighbourhood search around the user's dials: threshold steps,
    // single-component weight nudges (renormalised so the mix still sums to
    // 1.0), and the breakout toggle. Suggestions must keep a floor of the
    // sample (avoid "perfect" configs that cherry-pick 3 trades) and beat the
    // user's run on average return.
    private static List<LabSuggestion> Search(List<ReplayTrade> trades, StrategyLabRequest req, LabResult baseline)
    {
        if (trades.Count == 0) return [];

        var minKept = Math.Max(5, (int)(trades.Count * 0.4));
        var candidates = new List<LabSuggestion>();

        void Try(string description, LabWeights w, decimal threshold, bool excludeBreakout)
        {
            var r = Evaluate(trades, w, threshold, excludeBreakout);
            if (r.TradesKept < minKept) return;
            var improvement = r.SimAvgReturnPct - baseline.SimAvgReturnPct;
            if (improvement <= 0.05m) return; // must be a real improvement
            candidates.Add(new LabSuggestion(description, w, threshold, excludeBreakout,
                r.SimAvgReturnPct, r.SimWinRate, r.TradesKept, Math.Round(improvement, 2)));
        }

        // Threshold sweep
        foreach (var t in new[] { -0.5m, -0.25m, 0.25m, 0.5m, 1.0m })
        {
            var nt = Math.Clamp(req.BuyThreshold + t, 3.0m, 9.0m);
            if (nt != req.BuyThreshold)
                Try($"{(t > 0 ? "Raise" : "Lower")} Buy threshold {req.BuyThreshold:0.0} → {nt:0.0}", req.Weights, nt, req.ExcludeBreakout);
        }

        // Breakout toggle
        Try(req.ExcludeBreakout ? "Allow Breakout setups again" : "Exclude Breakout setups",
            req.Weights, req.BuyThreshold, !req.ExcludeBreakout);

        // Single-weight nudges (±0.05), renormalised
        var names = new[] { "RSI", "MACD", "Volume", "Sentiment", "Setup quality", "Relative strength", "Price level", "Fundamental momentum" };
        for (var i = 0; i < 8; i++)
        {
            foreach (var delta in new[] { 0.05m, -0.05m })
            {
                var arr = ToArray(req.Weights);
                var nv = arr[i] + delta;
                if (nv < 0.02m || nv > 0.45m) continue;
                arr[i] = nv;
                var sum = arr.Sum();
                for (var k = 0; k < 8; k++) arr[k] = Math.Round(arr[k] / sum, 4);
                Try($"{(delta > 0 ? "Increase" : "Decrease")} {names[i]} weight to {arr[i]:P0} (others rebalanced)",
                    FromArray(arr), req.BuyThreshold, req.ExcludeBreakout);
            }
        }

        return candidates
            .OrderByDescending(c => c.ImprovementPct)
            .DistinctBy(c => c.Description)
            .Take(3)
            .ToList();
    }

    private static decimal[] ToArray(LabWeights w) =>
        [w.Rsi, w.Macd, w.Volume, w.Sentiment, w.SetupQuality, w.RelativeStrength, w.PriceLevel, w.FundamentalMomentum];

    private static LabWeights FromArray(decimal[] a) =>
        new(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7]);
}
