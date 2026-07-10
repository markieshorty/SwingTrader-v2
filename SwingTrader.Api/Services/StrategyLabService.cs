using SwingTrader.Agents.Refinement;
using SwingTrader.Api.Contracts;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Api.Services;

// "Own data" strategy simulation: replays the account's closed trades under a
// candidate dial configuration via the shared TradeReplay spine (the same
// loader, filters and market-adjusted outcome metric RefinementService uses,
// so the Lab and the Refinement page always agree about the history).
//
// Honest scope limit (surfaced in the UI): it can only evaluate trades that
// WERE taken. Dials that would have taken different/extra trades can't be
// scored from own data - that's what the historic-market mode (backtester) is
// for. Own-data suggestions are therefore filters ("drop these kinds of
// trades"), never expansions.
public class StrategyLabService(
    ITradeReplayService tradeReplay,
    IAccountRepository accountRepo)
{
    public async Task<StrategyLabResponse?> RunOwnDataAsync(int accountId, StrategyLabRequest req, CancellationToken ct)
    {
        var account = await accountRepo.GetAsync(accountId, ct);
        if (account is null) return null;

        var replayable = await tradeReplay.LoadAsync(accountId, account.TradingMode, DateTime.UtcNow.AddYears(-2), DateTime.UtcNow, ct);

        var weights = ToWeights(req.Weights);
        var result = Evaluate(replayable, weights, req.BuyThreshold, req.ExcludeBreakout);
        var suggestions = Search(replayable, req, result);
        var trades = replayable
            .Select(t => new LabTradeOutcome(
                t.Trade.Symbol, t.Trade.OpenedAt,
                ReplayEvaluator.Conviction(t.Signal, weights), t.Signal.SetupType.ToString(), Math.Round(t.ReturnPct, 2),
                ReplayEvaluator.WouldTake(t, weights, req.BuyThreshold, req.ExcludeBreakout)))
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

    private static StrategyWeights ToWeights(LabWeights w) => new()
    {
        RsiWeight = w.Rsi, MacdWeight = w.Macd, VolumeWeight = w.Volume, SentimentWeight = w.Sentiment,
        SetupQualityWeight = w.SetupQuality, RelativeStrengthWeight = w.RelativeStrength,
        PriceLevelWeight = w.PriceLevel, FundamentalMomentumWeight = w.FundamentalMomentum,
    };

    private static LabResult Evaluate(List<ReplayableTrade> trades, StrategyWeights w, decimal threshold, bool excludeBreakout)
    {
        var o = ReplayEvaluator.Evaluate(trades, w, threshold, excludeBreakout);

        var summary = trades.Count == 0
            ? "Nothing to simulate yet."
            : $"Of your {o.Total} closed trades, these dials would have taken {o.Kept} and skipped {o.Total - o.Kept}. " +
              $"The skipped set contained {o.DroppedWinners} winner(s) and {o.DroppedLosers} loser(s). " +
              (o.Kept == 0
                  ? "These dials would have filtered out every trade you took."
                  : o.KeptAvgReturnPct > o.ActualAvgReturnPct
                      ? $"Average market-adjusted return per trade improves from {o.ActualAvgReturnPct:F2}% to {o.KeptAvgReturnPct:F2}% — the dials filter more losers than winners."
                      : o.KeptAvgReturnPct < o.ActualAvgReturnPct
                          ? $"Average market-adjusted return per trade drops from {o.ActualAvgReturnPct:F2}% to {o.KeptAvgReturnPct:F2}% — the dials filter out more winners than losers."
                          : "Average market-adjusted return per trade is unchanged.");

        return new LabResult(
            o.Total, o.Kept, o.Total - o.Kept,
            o.DroppedWinners, o.DroppedLosers,
            o.ActualAvgReturnPct, o.KeptAvgReturnPct, o.ActualWinRate, o.KeptWinRate,
            o.ActualTotalReturnPct, o.KeptTotalReturnPct,
            summary);
    }

    // Bounded neighbourhood search around the user's dials: threshold steps,
    // single-component weight nudges (renormalised so the mix still sums to
    // 1.0), and the breakout toggle. Suggestions must keep a floor of the
    // sample (avoid "perfect" configs that cherry-pick 3 trades) and beat the
    // user's run on average return.
    private static List<LabSuggestion> Search(List<ReplayableTrade> trades, StrategyLabRequest req, LabResult baseline)
    {
        if (trades.Count == 0) return [];

        var minKept = Math.Max(5, (int)(trades.Count * 0.4));
        var candidates = new List<LabSuggestion>();

        void Try(string description, LabWeights w, decimal threshold, bool excludeBreakout)
        {
            var r = Evaluate(trades, ToWeights(w), threshold, excludeBreakout);
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
