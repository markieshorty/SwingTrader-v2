using SwingTrader.Agents.Research;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Services;

namespace SwingTrader.Agents.Backtesting;

// The single historic-market backtest engine, shared by the local console tool
// (CSV bars) and the in-app Strategy Lab historic mode (DB bars). Replays the
// production decision pipeline over daily bars: weekly screener -> daily
// indicator scoring (the real IndicatorService + ConvictionScorer +
// EntryLevelCalculator) -> next-open entries -> gap-aware stop/target/
// trailing/time exits, with round-trip costs.
//
// Honest divergences from production (deliberate, conservative): no Claude
// (watchlist proxy = top-N by screener rank; sentiment and the other
// unreconstructable components score neutral 0.5), and the universe is
// today's membership (survivorship bias) - results are for RELATIVE
// comparisons, not absolute predictions.

public sealed record DailyBar(DateTime Date, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

public sealed record HistoricConfig(
    StrategyWeights Weights,
    decimal BuyThreshold = 6.0m,
    bool ExcludeBreakout = true,           // production policy since 2026-07-10
    // Approximates production's bear-market autopause (on by default there):
    // entries are skipped while SPY is below its 200-day average. Coarser than
    // the live classifier (which also wants a falling MA / deep breach / death
    // cross), but far closer to live behaviour than trading straight through a
    // bear. Wired from the account's AutopauseDuringBear setting.
    bool RegimeFilter = false,
    decimal? BreakoutQualityOverride = null,
    bool ConvictionSizing = false,
    decimal PositionFraction = 0.10m,
    int MaxOpenPositions = 3,
    decimal MinDollarVolume = 10_000_000m,
    // Trading-day hold cap, mirrored from the account risk profile so the Lab
    // tests the strategy the account actually runs (was a hardcoded 10).
    int MaxHoldDays = 10);

public sealed record HistoricTrade(
    string Symbol, DateTime EntryDate, DateTime ExitDate, decimal EntryPrice, decimal ExitPrice,
    SetupType Setup, decimal Conviction, string ExitReason, decimal ReturnPct);

public sealed record BucketStat(string Key, int Count, decimal WinRate, decimal AvgReturnPct);

public sealed record HistoricResult(
    DateTime From, DateTime To,
    int Trades, decimal WinRate, decimal AvgWinPct, decimal AvgLossPct,
    decimal ExpectancyPct, decimal ProfitFactor,
    decimal TotalReturnPct, decimal MaxDrawdownPct, decimal SpyReturnPct,
    List<BucketStat> BySetup, List<BucketStat> ByConviction, List<BucketStat> ByExitReason,
    List<HistoricTrade> TradeLog);

public static class HistoricBacktester
{
    private const decimal StartingEquity = 10_000m;
    private const decimal CostPerSide = 0.0025m;
    private const int WatchlistSize = 25;
    private const int MaxOrdersPerDay = 3;
    private const decimal TrailingActivationPct = 0.05m;
    private const decimal TrailingDistancePct = 0.03m;
    private const int WarmupBars = 60;

    private sealed class Position
    {
        public required string Symbol;
        public required DateTime EntryDate;
        // Calendar index of the entry bar. Bars ARE trading days, so
        // (currentIndex - EntryBarIndex) is the trading days held - matching
        // the live time-exit accounting (PositionMonitorService), which counts
        // trading days, not calendar days.
        public required int EntryBarIndex;
        public required decimal EntryPrice;
        public required decimal Quantity;
        public required decimal StopLoss;
        public required decimal Target;
        public required SetupType Setup;
        public required decimal Conviction;
        public decimal? TrailingStop;
    }

    public static async Task<HistoricResult> RunAsync(
        IReadOnlyDictionary<string, DailyBar[]> bars, HistoricConfig cfg, CancellationToken ct = default)
    {
        if (!bars.TryGetValue("SPY", out var spy) || spy.Length <= WarmupBars)
            throw new InvalidOperationException("SPY bars are required as the trading-calendar anchor (with warmup history).");

        var indicators = new IndicatorService();
        var calendar = spy.Select(b => b.Date).ToList();
        var index = bars.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select((b, i) => (b.Date, i)).ToDictionary(x => x.Date, x => x.i),
            StringComparer.OrdinalIgnoreCase);

        var equity = StartingEquity;
        var cash = StartingEquity;
        var open = new List<Position>();
        var closed = new List<HistoricTrade>();
        var equityCurve = new List<decimal>();
        var watchlist = new List<string>();

        for (var d = WarmupBars; d < calendar.Count - 1; d++)
        {
            ct.ThrowIfCancellationRequested();
            var today = calendar[d];

            if (watchlist.Count == 0 || today.DayOfWeek == DayOfWeek.Monday)
                watchlist = BuildWatchlist(bars, index, today, cfg.MinDollarVolume);

            // Manage open positions (gap-aware)
            foreach (var pos in open.ToList())
            {
                var bar = GetBar(bars, index, pos.Symbol, today);
                if (bar is null) continue;

                var (exitPrice, reason) = CheckExit(pos, bar, d, cfg.MaxHoldDays);
                if (exitPrice.HasValue)
                {
                    var proceeds = exitPrice.Value * pos.Quantity * (1 - CostPerSide);
                    var cost = pos.EntryPrice * pos.Quantity * (1 + CostPerSide);
                    cash += proceeds;
                    closed.Add(new HistoricTrade(pos.Symbol, pos.EntryDate, today, pos.EntryPrice, exitPrice.Value,
                        pos.Setup, pos.Conviction, reason!, Math.Round((proceeds - cost) / cost * 100m, 2)));
                    open.Remove(pos);
                }
                else if (bar.Close >= pos.EntryPrice * (1 + TrailingActivationPct))
                {
                    var newTrail = bar.Close * (1 - TrailingDistancePct);
                    if (pos.TrailingStop is null || newTrail > pos.TrailingStop) pos.TrailingStop = newTrail;
                }
            }

            // Enter new positions tomorrow at the open
            var regimeOk = !cfg.RegimeFilter || SpyAboveSma200(spy, d);
            if (open.Count < cfg.MaxOpenPositions && regimeOk)
            {
                var candidates = new List<(string Symbol, decimal Conviction, SetupType Setup)>();
                foreach (var symbol in watchlist)
                {
                    if (open.Any(p => p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))) continue;
                    var scored = await ScoreAsync(indicators, cfg, bars, index, symbol, today);
                    if (scored is { } s && s.Conviction >= cfg.BuyThreshold && s.Rsi <= 75m
                        && !(cfg.ExcludeBreakout && s.Setup == SetupType.Breakout))
                        candidates.Add((symbol, s.Conviction, s.Setup));
                }

                foreach (var c in candidates.OrderByDescending(c => c.Conviction).Take(MaxOrdersPerDay))
                {
                    if (open.Count >= cfg.MaxOpenPositions) break;
                    var entryBar = GetBar(bars, index, c.Symbol, calendar[d + 1]);
                    if (entryBar is null || entryBar.Open <= 0) continue;

                    var budget = Math.Min(equity * cfg.PositionFraction, cash / (1 + CostPerSide));
                    if (cfg.ConvictionSizing)
                    {
                        var t = (Math.Clamp(c.Conviction, 6.0m, 9.0m) - 6.0m) / 3.0m;
                        budget *= 0.5m + t * 0.5m;
                    }
                    if (budget < 50m) continue;

                    var qty = Math.Floor(budget / entryBar.Open * 1000m) / 1000m;
                    if (qty <= 0) continue;

                    var (stop, target) = Core.Trading.EntryLevelCalculator.Calculate(c.Setup, c.Conviction, entryBar.Open);
                    cash -= entryBar.Open * qty * (1 + CostPerSide);
                    open.Add(new Position
                    {
                        Symbol = c.Symbol, EntryDate = calendar[d + 1], EntryBarIndex = d + 1, EntryPrice = entryBar.Open,
                        Quantity = qty, StopLoss = stop, Target = target, Setup = c.Setup, Conviction = c.Conviction,
                    });
                }
            }

            equity = cash + open.Sum(p => (GetBar(bars, index, p.Symbol, today)?.Close ?? p.EntryPrice) * p.Quantity);
            equityCurve.Add(equity);
        }

        return BuildResult(closed, equityCurve, spy, calendar);
    }

    private static HistoricResult BuildResult(List<HistoricTrade> closed, List<decimal> equityCurve, DailyBar[] spy, List<DateTime> calendar)
    {
        var wins = closed.Where(t => t.ReturnPct > 0).ToList();
        var losses = closed.Where(t => t.ReturnPct <= 0).ToList();
        var grossWin = wins.Sum(t => t.ReturnPct);
        var grossLoss = Math.Abs(losses.Sum(t => t.ReturnPct));

        var peak = 0m; var maxDd = 0m;
        foreach (var e in equityCurve)
        {
            peak = Math.Max(peak, e);
            if (peak > 0) maxDd = Math.Max(maxDd, (peak - e) / peak);
        }

        List<BucketStat> Bucket<TKey>(Func<HistoricTrade, TKey> keySelector) => closed
            .GroupBy(keySelector)
            .Select(g => new BucketStat(g.Key!.ToString()!, g.Count(),
                Math.Round((decimal)g.Count(t => t.ReturnPct > 0) / g.Count(), 4),
                Math.Round(g.Average(t => t.ReturnPct), 2)))
            .OrderByDescending(b => b.Count)
            .ToList();

        return new HistoricResult(
            calendar[WarmupBars], calendar[^1],
            closed.Count,
            closed.Count > 0 ? Math.Round((decimal)wins.Count / closed.Count, 4) : 0m,
            wins.Count > 0 ? Math.Round(wins.Average(t => t.ReturnPct), 2) : 0m,
            losses.Count > 0 ? Math.Round(losses.Average(t => t.ReturnPct), 2) : 0m,
            closed.Count > 0 ? Math.Round(closed.Average(t => t.ReturnPct), 2) : 0m,
            grossLoss > 0 ? Math.Round(grossWin / grossLoss, 2) : 0m,
            equityCurve.Count > 0 ? Math.Round((equityCurve[^1] / StartingEquity - 1) * 100m, 1) : 0m,
            Math.Round(maxDd * 100m, 1),
            spy[WarmupBars].Close > 0 ? Math.Round((spy[^1].Close / spy[WarmupBars].Close - 1) * 100m, 1) : 0m,
            Bucket(t => t.Setup),
            Bucket(t => Math.Floor(t.Conviction)),
            Bucket(t => t.ExitReason.Replace("(gap)", "")),
            closed);
    }

    private static (decimal? ExitPrice, string? Reason) CheckExit(Position pos, DailyBar bar, int currentBarIndex, int maxHoldDays)
    {
        if (bar.Open <= pos.StopLoss) return (bar.Open, "StopLoss(gap)");
        if (bar.Low <= pos.StopLoss) return (pos.StopLoss, "StopLoss");
        if (bar.Open >= pos.Target) return (bar.Open, "Target(gap)");
        if (bar.High >= pos.Target) return (pos.Target, "Target");
        if (pos.TrailingStop is { } trail)
        {
            if (bar.Open <= trail) return (bar.Open, "Trailing(gap)");
            if (bar.Low <= trail) return (trail, "Trailing");
        }
        // Trading days held = bar-index difference (bars are trading days),
        // matching the live PositionMonitorService time-exit accounting.
        if (currentBarIndex - pos.EntryBarIndex > maxHoldDays) return (bar.Close, "TimeExit");
        return (null, null);
    }

    private static async Task<(decimal Conviction, SetupType Setup, decimal Rsi)?> ScoreAsync(
        IndicatorService indicators, HistoricConfig cfg,
        IReadOnlyDictionary<string, DailyBar[]> bars, Dictionary<string, Dictionary<DateTime, int>> index,
        string symbol, DateTime today)
    {
        if (!index.TryGetValue(symbol, out var dates) || !dates.TryGetValue(today, out var i) || i < WarmupBars)
            return null;

        var history = bars[symbol][(i - WarmupBars + 1)..(i + 1)];
        var candles = history.Select(b => new StockCandle
        {
            Symbol = symbol, Timestamp = b.Date, Open = b.Open, High = b.High, Low = b.Low, Close = b.Close,
            Volume = (long)b.Volume,
        }).ToList();

        var ind = await indicators.CalculateAllAsync(candles);
        if (ind.Rsi14 is null) return null;
        var prev = await indicators.GetMacdAsync(candles.Take(candles.Count - 1));

        var setup = DetectSetup(ind, candles);
        var setupScore = setup == SetupType.Breakout && cfg.BreakoutQualityOverride is { } q
            ? q
            : ConvictionScorer.ScoreSetupQuality(setup);

        var conviction = ConvictionScorer.Calculate(
            cfg.Weights,
            ConvictionScorer.ScoreRsi(ind.Rsi14),
            ConvictionScorer.ScoreMacd(ind.MacdHistogram, prev.Histogram),
            ConvictionScorer.ScoreVolume(ind.VolumeRatio),
            sentimentScore: 0.5m, // not reconstructible historically
            setupScore);

        return (conviction, setup, ind.Rsi14.Value);
    }

    // Mirror of ResearchPipeline.DetectSetup (private there) - keep in sync.
    private static SetupType DetectSetup(IndicatorResult ind, List<StockCandle> candles)
    {
        var price = candles[^1].Close;

        if (ind.Rsi14 < 35 && ind.BollingerLower.HasValue && price > ind.BollingerLower.Value)
            return SetupType.OversoldRecovery;

        if (ind.BollingerUpper.HasValue && price > ind.BollingerUpper.Value
            && ind.VolumeRatio > 1.5m && ind.MacdHistogram > 0)
            return SetupType.Breakout;

        if (ind.Rsi14 >= 50 && ind.Rsi14 <= 65
            && ind.Ema9.HasValue && ind.Ema21.HasValue && ind.Ema9 > ind.Ema21
            && ind.MacdHistogram > 0 && ind.VolumeRatio > 1.0m)
            return SetupType.MomentumContinuation;

        if (ind.VolumeRatio > 2.0m && candles.Count >= 2)
        {
            var prevClose = candles[^2].Close;
            if (prevClose > 0 && (price - prevClose) / prevClose * 100 > 1.5m)
                return SetupType.VolumeSpike;
        }

        if (ind.Ema9.HasValue && ind.Ema21.HasValue && ind.Ema9 > ind.Ema21
            && ind.Rsi14 > 50
            && ind.BollingerMid.HasValue && price > ind.BollingerMid.Value)
            return SetupType.TrendFollowing;

        return SetupType.Unknown;
    }

    private static List<string> BuildWatchlist(
        IReadOnlyDictionary<string, DailyBar[]> bars, Dictionary<string, Dictionary<DateTime, int>> index,
        DateTime asOf, decimal minDollarVolume)
    {
        var ranked = new List<(string Symbol, decimal AbsChange)>();
        foreach (var (symbol, series) in bars)
        {
            if (symbol.Equals("SPY", StringComparison.OrdinalIgnoreCase)) continue;
            if (!index[symbol].TryGetValue(asOf, out var i) || i < 21) continue;

            var bar = series[i];
            var prevBar = series[i - 1];
            if (prevBar.Close <= 0) continue;

            var change = (bar.Close - prevBar.Close) / prevBar.Close * 100m;
            var absChange = Math.Abs(change);
            if (bar.Close < 15m || bar.Close > 500m) continue;
            if (absChange < 1.0m || absChange > 15.0m) continue;

            var avgVol = series[(i - 19)..(i + 1)].Average(b => b.Volume);
            if (avgVol * bar.Close < minDollarVolume) continue;

            ranked.Add((symbol, absChange));
        }

        return ranked.OrderByDescending(r => r.AbsChange).Take(WatchlistSize).Select(r => r.Symbol).ToList();
    }

    private static bool SpyAboveSma200(DailyBar[] spy, int i)
    {
        if (i < 200) return true;
        var sma = 0m;
        for (var k = i - 199; k <= i; k++) sma += spy[k].Close;
        return spy[i].Close > sma / 200m;
    }

    private static DailyBar? GetBar(
        IReadOnlyDictionary<string, DailyBar[]> bars, Dictionary<string, Dictionary<DateTime, int>> index,
        string symbol, DateTime date) =>
        index.TryGetValue(symbol, out var dates) && dates.TryGetValue(date, out var i) ? bars[symbol][i] : null;
}
