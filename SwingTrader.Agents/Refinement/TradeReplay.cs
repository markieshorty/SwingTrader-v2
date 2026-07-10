using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Refinement;

// The shared data spine for every engine that reasons about the account's
// trade history (RefinementService's correlation analysis, the Strategy Lab's
// replay, and the refinement replay-check). One loader, one outcome metric,
// one set of filters - so the Refinement page and the Lab can never show
// conflicting numbers for the same history.
//
// Outcome metric: MARKET-ADJUSTED return % (raw return minus SPY's move over
// the same holding period, where captured) - a +3% trade during a +5% SPY
// week is a worse decision than a +2% trade in a flat one.
public record ReplayableTrade(Trade Trade, StockSignal Signal, decimal ReturnPct);

public interface ITradeReplayService
{
    /// <summary>
    /// Closed trades (excluding Pending/Cancelled intents and dead-heat
    /// zero-P&amp;L rows) joined to their signals' persisted component scores,
    /// with market-adjusted return. Only trades whose signal carries component
    /// scores are replayable.
    /// </summary>
    Task<List<ReplayableTrade>> LoadAsync(int accountId, TradingMode mode, DateTime from, DateTime to, CancellationToken ct = default);
}

public class TradeReplayService(ITradeRepository tradeRepo, ISignalRepository signalRepo) : ITradeReplayService
{
    public async Task<List<ReplayableTrade>> LoadAsync(int accountId, TradingMode mode, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var history = await tradeRepo.GetTradeHistoryAsync(accountId, mode, from, to);
        var result = new List<ReplayableTrade>();

        foreach (var trade in history.Where(t =>
                     t.Status is not (TradeStatus.Open or TradeStatus.Pending or TradeStatus.Cancelled)
                     && t.ClosedAt.HasValue
                     && t.RealizedPnl is not null and not 0m
                     && t.SignalId.HasValue && t.EntryPrice > 0 && t.Quantity > 0))
        {
            var signal = await signalRepo.GetByIdAsync(accountId, trade.SignalId!.Value);
            if (signal?.RsiScore is null) continue; // pre-component-score era

            var cost = trade.EntryPrice * trade.Quantity;
            var rawPct = trade.RealizedPnl!.Value / cost * 100m;
            var returnPct = trade.SpyReturnDuringTrade.HasValue
                ? rawPct - trade.SpyReturnDuringTrade.Value
                : rawPct;

            result.Add(new ReplayableTrade(trade, signal, returnPct));
        }

        return result;
    }
}

// Pure config evaluator shared by the Strategy Lab and the refinement
// replay-check: given a set of replayable trades and a candidate dial
// configuration, which trades would have been taken and how did that subset
// actually perform?
public static class ReplayEvaluator
{
    public record Outcome(
        int Total, int Kept, int DroppedWinners, int DroppedLosers,
        decimal ActualAvgReturnPct, decimal KeptAvgReturnPct,
        decimal ActualWinRate, decimal KeptWinRate,
        decimal ActualTotalReturnPct, decimal KeptTotalReturnPct);

    public static decimal Conviction(StockSignal s, StrategyWeights w)
    {
        var raw =
            w.RsiWeight * (s.RsiScore ?? 0.5m) +
            w.MacdWeight * (s.MacdScore ?? 0.5m) +
            w.VolumeWeight * (s.VolumeScore ?? 0.5m) +
            w.SentimentWeight * (s.SentimentComponentScore ?? 0.5m) +
            w.SetupQualityWeight * (s.SetupQualityScore ?? 0.5m) +
            w.RelativeStrengthWeight * (s.RelativeStrengthScore ?? 0.5m) +
            w.PriceLevelWeight * (s.PriceLevelScore ?? 0.5m) +
            w.FundamentalMomentumWeight * (s.FundamentalMomentumScore ?? 0.5m);
        return Math.Round(Math.Clamp(raw * 10m, 0m, 10m), 1);
    }

    public static bool WouldTake(ReplayableTrade t, StrategyWeights w, decimal buyThreshold, bool excludeBreakout) =>
        (!excludeBreakout || t.Signal.SetupType != SetupType.Breakout)
        && Conviction(t.Signal, w) >= buyThreshold;

    public static Outcome Evaluate(IReadOnlyList<ReplayableTrade> trades, StrategyWeights w, decimal buyThreshold, bool excludeBreakout)
    {
        var kept = trades.Where(t => WouldTake(t, w, buyThreshold, excludeBreakout)).ToList();
        var dropped = trades.Except(kept).ToList();

        static decimal Avg(List<ReplayableTrade> set) => set.Count > 0 ? Math.Round(set.Average(t => t.ReturnPct), 2) : 0m;
        static decimal WinRate(List<ReplayableTrade> set) => set.Count > 0 ? Math.Round((decimal)set.Count(t => t.ReturnPct > 0) / set.Count, 4) : 0m;

        return new Outcome(
            trades.Count, kept.Count,
            dropped.Count(t => t.ReturnPct > 0), dropped.Count(t => t.ReturnPct <= 0),
            Avg(trades.ToList()), Avg(kept),
            WinRate(trades.ToList()), WinRate(kept),
            Math.Round(trades.Sum(t => t.ReturnPct), 2), Math.Round(kept.Sum(t => t.ReturnPct), 2));
    }
}
