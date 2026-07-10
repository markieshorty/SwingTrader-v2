using System.Globalization;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Agents.Research;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Core.Trading;
using SwingTrader.Infrastructure.Market;
using SwingTrader.Infrastructure.Services;

namespace SwingTrader.Backtest;

// Replays the production decision pipeline over local daily bars:
// weekly screener -> daily indicator scoring (the real ConvictionScorer +
// IndicatorService + EntryLevelCalculator) -> next-open entries -> gap-aware
// stop/target/trailing/time exits, with round-trip costs.
//
// Honest divergences from production (kept deliberately, all conservative):
// - No Claude: the watchlist proxy is "top 25 by screener rank" (production
//   sends 80 to Claude which picks ~25 with sector diversity), and sentiment /
//   fundamental components score a neutral 0.5 (the only two that can't be
//   reconstructed from bars). Relative strength (needs the sector ETF CSVs in
//   the data dir - neutral when absent) and price level ARE computed, via the
//   same shared calculators the live services and the in-app Lab run.
// - Survivorship bias: the universe is TODAY'S index membership, so results
//   are optimistic in absolute terms - use them to COMPARE configurations,
//   not to predict returns.
public static class BacktestEngine
{
    private sealed record Bar(DateTime Date, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

    private sealed class Position
    {
        public required string Symbol;
        public required DateTime EntryDate;
        // Bars are trading days, so (currentIndex - EntryBarIndex) is trading
        // days held - matches the live PositionMonitorService time-exit.
        public required int EntryBarIndex;
        public required decimal EntryPrice;
        public required decimal Quantity;
        public required decimal StopLoss;
        public required decimal Target;
        public required SetupType Setup;
        public required decimal Conviction;
        public decimal? TrailingStop;
    }

    private sealed record ClosedTrade(
        string Symbol, DateTime EntryDate, DateTime ExitDate, decimal EntryPrice, decimal ExitPrice,
        decimal Quantity, SetupType Setup, decimal Conviction, string ExitReason, decimal NetPnl, decimal ReturnPct);

    // ── Config (mirrors production defaults where they exist) ────────────────
    private const decimal StartingEquity = 10_000m;
    private const int MaxOrdersPerDay = 3;
    private const decimal CostPerSide = 0.0025m;        // 0.15% T212 FX + ~0.1% spread/slippage
    private const int WatchlistSize = 25;
    private const int MaxHoldDays = 10;                 // calendar days, as production
    private const decimal TrailingActivationPct = 0.05m;
    private const decimal TrailingDistancePct = 0.03m;
    // Shared with the in-app Lab engine so both replay identical windows.
    private const int WarmupBars = HistoricBacktester.WarmupBars;

    // Experiment knobs (CLI-settable) - defaults mirror production.
    public sealed record Options(
        decimal BuyThreshold = 6.0m,                    // StrategyWeights default
        bool RegimeFilter = false,                      // only enter when SPY > its 200d SMA
        HashSet<SetupType>? ExcludedSetups = null,
        decimal? BreakoutQualityOverride = null,        // penalize (production: 0.9) instead of excluding
        bool ConvictionSizing = false,                  // scale budget 0.5x-1.0x over conviction 6-9, as production
        StrategyWeights? Weights = null,                // override component weights (default: production)
        decimal PositionFraction = 0.10m,               // fraction of equity per position
        int MaxOpenPositions = 3,
        string Label = "baseline");

    public static async Task<int> RunAsync(string dataDir, Options opts, CancellationToken ct)
    {
        if (!Directory.Exists(dataDir))
        {
            Console.Error.WriteLine($"Data directory not found: {Path.GetFullPath(dataDir)} — run the download command first.");
            return 2;
        }

        Console.WriteLine("Loading bars…");
        var bars = LoadAllBars(dataDir);
        if (!bars.TryGetValue("SPY", out var spy))
        {
            Console.Error.WriteLine("SPY.csv missing — required as the trading calendar anchor.");
            return 2;
        }
        Console.WriteLine($"Loaded {bars.Count} symbols.");

        var indicators = new IndicatorService();
        var weights = opts.Weights ?? new StrategyWeights(); // production defaults unless overridden
        var calendar = spy.Select(b => b.Date).ToList();

        // Per-symbol date -> index lookups for O(1) bar access.
        var index = bars.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select((b, i) => (b.Date, i)).ToDictionary(x => x.Date, x => x.i),
            StringComparer.OrdinalIgnoreCase);

        var equity = StartingEquity;
        var cash = StartingEquity;
        var open = new List<Position>();
        var closed = new List<ClosedTrade>();
        var equityCurve = new List<decimal>();
        var watchlist = new List<string>();

        for (var d = WarmupBars; d < calendar.Count - 1; d++) // -1: entries need next-day open
        {
            if (ct.IsCancellationRequested) break;
            var today = calendar[d];

            // ── Weekly watchlist rebuild (first trading day of each week) ─────
            if (watchlist.Count == 0 || today.DayOfWeek == DayOfWeek.Monday)
                watchlist = BuildWatchlist(bars, index, today);

            // ── Manage open positions on today's bar (gap-aware) ──────────────
            foreach (var pos in open.ToList())
            {
                var bar = GetBar(bars, index, pos.Symbol, today);
                if (bar is null) continue;

                var (exitPrice, reason) = CheckExit(pos, bar, d);
                if (exitPrice.HasValue)
                {
                    var proceeds = exitPrice.Value * pos.Quantity * (1 - CostPerSide);
                    var cost = pos.EntryPrice * pos.Quantity * (1 + CostPerSide);
                    var pnl = proceeds - cost;
                    cash += proceeds;
                    closed.Add(new ClosedTrade(pos.Symbol, pos.EntryDate, today, pos.EntryPrice, exitPrice.Value,
                        pos.Quantity, pos.Setup, pos.Conviction, reason!, pnl, pnl / cost * 100m));
                    open.Remove(pos);
                }
                else
                {
                    // Trailing stop arm/ratchet on the close, as Monitor does on quotes.
                    if (bar.Close >= pos.EntryPrice * (1 + TrailingActivationPct))
                    {
                        var newTrail = bar.Close * (1 - TrailingDistancePct);
                        if (pos.TrailingStop is null || newTrail > pos.TrailingStop) pos.TrailingStop = newTrail;
                    }
                }
            }

            // ── Score today's watchlist, enter tomorrow at the open ───────────
            var regimeOk = !opts.RegimeFilter || SpyAboveSma200(spy, d);
            if (open.Count < opts.MaxOpenPositions && regimeOk)
            {
                var candidates = new List<(string Symbol, decimal Conviction, SetupType Setup)>();
                foreach (var symbol in watchlist)
                {
                    if (open.Any(p => p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))) continue;
                    var scored = await ScoreAsync(indicators, weights, bars, index, symbol, today, opts);
                    if (scored is { } s && s.Conviction >= opts.BuyThreshold && s.Rsi <= 75m
                        && opts.ExcludedSetups?.Contains(s.Setup) != true)
                        candidates.Add((symbol, s.Conviction, s.Setup));
                }

                foreach (var c in candidates.OrderByDescending(c => c.Conviction).Take(MaxOrdersPerDay))
                {
                    if (open.Count >= opts.MaxOpenPositions) break;
                    var entryBar = GetBar(bars, index, c.Symbol, calendar[d + 1]);
                    if (entryBar is null || entryBar.Open <= 0) continue;

                    var budget = Math.Min(equity * opts.PositionFraction, cash / (1 + CostPerSide));
                    if (opts.ConvictionSizing)
                    {
                        // Mirror PositionSizingService.ConvictionMultiplier.
                        var t = (Math.Clamp(c.Conviction, 6.0m, 9.0m) - 6.0m) / 3.0m;
                        budget *= 0.5m + t * 0.5m;
                    }
                    if (budget < 50m) continue; // dust guard

                    var qty = Math.Floor(budget / entryBar.Open * 1000m) / 1000m;
                    if (qty <= 0) continue;

                    var (stop, target) = EntryLevelCalculator.Calculate(c.Setup, c.Conviction, entryBar.Open);
                    cash -= entryBar.Open * qty * (1 + CostPerSide);
                    open.Add(new Position
                    {
                        Symbol = c.Symbol, EntryDate = calendar[d + 1], EntryBarIndex = d + 1, EntryPrice = entryBar.Open,
                        Quantity = qty, StopLoss = stop, Target = target, Setup = c.Setup, Conviction = c.Conviction,
                    });
                }
            }

            // ── Mark to market ────────────────────────────────────────────────
            equity = cash + open.Sum(p => (GetBar(bars, index, p.Symbol, today)?.Close ?? p.EntryPrice) * p.Quantity);
            equityCurve.Add(equity);
        }

        Report(closed, equityCurve, spy, calendar, dataDir, opts);
        return 0;
    }

    private static bool SpyAboveSma200(Bar[] spy, int i)
    {
        if (i < 200) return true; // not enough history - don't block
        var sma = 0m;
        for (var k = i - 199; k <= i; k++) sma += spy[k].Close;
        return spy[i].Close > sma / 200m;
    }

    private static (decimal? ExitPrice, string? Reason) CheckExit(Position pos, Bar bar, int currentBarIndex)
    {
        // Priority mirrors PositionMonitorService: stop, target, trailing, time.
        // Gap-aware: an open through the level fills at the open, not the level.
        if (bar.Open <= pos.StopLoss) return (bar.Open, "StopLoss(gap)");
        if (bar.Low <= pos.StopLoss) return (pos.StopLoss, "StopLoss");
        if (bar.Open >= pos.Target) return (bar.Open, "Target(gap)");
        if (bar.High >= pos.Target) return (pos.Target, "Target");
        if (pos.TrailingStop is { } trail)
        {
            if (bar.Open <= trail) return (bar.Open, "Trailing(gap)");
            if (bar.Low <= trail) return (trail, "Trailing");
        }
        // Trading days held = bar-index difference (bars are trading days).
        if (currentBarIndex - pos.EntryBarIndex > MaxHoldDays) return (bar.Close, "TimeExit");
        return (null, null);
    }

    private static async Task<(decimal Conviction, SetupType Setup, decimal Rsi)?> ScoreAsync(
        IndicatorService indicators, StrategyWeights weights,
        Dictionary<string, Bar[]> bars, Dictionary<string, Dictionary<DateTime, int>> index,
        string symbol, DateTime today, Options opts)
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

        var setupScore = setup == SetupType.Breakout && opts.BreakoutQualityOverride is { } q
            ? q
            : ConvictionScorer.ScoreSetupQuality(setup);

        // Same shared RS + price-level calculators as live/Lab. RS is neutral
        // when the sector ETF CSV isn't in the data dir.
        var rsScore = ComputeRelativeStrengthScore(bars, index, symbol, history, today);
        var priceBars = history.Select(b => new PriceBar(b.High, b.Low, b.Close, b.Volume)).ToList();
        var priceLevel = PriceLevelCalculator.Compute(priceBars, history[^1].Close, PriceLevelDefaults);

        var conviction = ConvictionScorer.Calculate(
            weights,
            ConvictionScorer.ScoreRsi(ind.Rsi14),
            ConvictionScorer.ScoreMacd(ind.MacdHistogram, prev.Histogram),
            ConvictionScorer.ScoreVolume(ind.VolumeRatio),
            sentimentScore: 0.5m, // not reconstructible historically
            setupScore,
            relativeStrengthScore: rsScore ?? 0.5m,
            priceLevelScore: priceLevel.Score);

        return (conviction, setup, ind.Rsi14.Value);
    }

    // Production defaults (PriceLevelConfig class defaults) - deliberately not
    // read from appsettings so results are reproducible across environments.
    private static readonly Infrastructure.Configuration.PriceLevelConfig PriceLevelDefaults = new();

    private static decimal? ComputeRelativeStrengthScore(
        Dictionary<string, Bar[]> bars, Dictionary<string, Dictionary<DateTime, int>> index,
        string symbol, Bar[] history, DateTime today)
    {
        var etf = SectorEtfMap.GetEtf(symbol);
        if (!bars.TryGetValue(etf, out var etfBars) ||
            !index.TryGetValue(etf, out var etfDates) ||
            !etfDates.TryGetValue(today, out var etfIdx) ||
            etfIdx < RelativeStrengthCalculator.WindowDays - 1)
            return null;

        var window = RelativeStrengthCalculator.WindowDays;
        var stockCloses = history[^window..].Select(b => b.Close).ToList();
        var etfCloses = etfBars[(etfIdx - window + 1)..(etfIdx + 1)].Select(b => b.Close).ToList();

        return RelativeStrengthCalculator.Compute(stockCloses, etfCloses)?.Score;
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

    // The production screener, replayed: price band + move band + dollar-volume
    // floor, ranked by |move|, top-25 as the Claude proxy.
    private static List<string> BuildWatchlist(
        Dictionary<string, Bar[]> bars, Dictionary<string, Dictionary<DateTime, int>> index, DateTime asOf)
    {
        var ranked = new List<(string Symbol, decimal AbsChange)>();
        foreach (var (symbol, series) in bars)
        {
            // SPY and the sector ETFs are benchmarks, never trade candidates.
            if (symbol.Equals("SPY", StringComparison.OrdinalIgnoreCase) ||
                SectorEtfMap.AllEtfs().Contains(symbol, StringComparer.OrdinalIgnoreCase)) continue;
            if (!index[symbol].TryGetValue(asOf, out var i) || i < 21) continue;

            var bar = series[i];
            var prevBar = series[i - 1];
            if (prevBar.Close <= 0) continue;

            var change = (bar.Close - prevBar.Close) / prevBar.Close * 100m;
            var absChange = Math.Abs(change);
            if (bar.Close < 15m || bar.Close > 500m) continue;
            if (absChange < 1.0m || absChange > 15.0m) continue;

            var avgVol = series[(i - 19)..(i + 1)].Average(b => b.Volume);
            if (avgVol * bar.Close < 10_000_000m) continue;

            ranked.Add((symbol, absChange));
        }

        return ranked.OrderByDescending(r => r.AbsChange).Take(WatchlistSize).Select(r => r.Symbol).ToList();
    }

    private static Bar? GetBar(
        Dictionary<string, Bar[]> bars, Dictionary<string, Dictionary<DateTime, int>> index,
        string symbol, DateTime date) =>
        index.TryGetValue(symbol, out var dates) && dates.TryGetValue(date, out var i) ? bars[symbol][i] : null;

    private static Dictionary<string, Bar[]> LoadAllBars(string dataDir)
    {
        var result = new Dictionary<string, Bar[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dataDir, "*.csv"))
        {
            var symbol = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
            if (symbol.StartsWith('_')) continue; // result/metadata files, not price data
            var lines = File.ReadAllLines(file);
            if (lines.Length < 2) continue;

            var list = new List<Bar>(lines.Length - 1);
            foreach (var line in lines.Skip(1))
            {
                var p = line.Split(',');
                if (p.Length < 11) continue;
                // Adjusted OHLCV (columns 6-10) - split/dividend-consistent history.
                list.Add(new Bar(
                    DateTime.ParseExact(p[0], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    decimal.Parse(p[6], CultureInfo.InvariantCulture),
                    decimal.Parse(p[7], CultureInfo.InvariantCulture),
                    decimal.Parse(p[8], CultureInfo.InvariantCulture),
                    decimal.Parse(p[9], CultureInfo.InvariantCulture),
                    decimal.Parse(p[10], CultureInfo.InvariantCulture)));
            }
            if (list.Count > 0) result[symbol] = list.ToArray();
        }
        return result;
    }

    private static void Report(List<ClosedTrade> closed, List<decimal> equityCurve, Bar[] spy, List<DateTime> calendar, string dataDir, Options opts)
    {
        Console.WriteLine($"\n════════ BACKTEST RESULTS [{opts.Label}] ════════");
        Console.WriteLine($"Config: threshold={opts.BuyThreshold} regimeFilter={opts.RegimeFilter} excluded=[{string.Join(",", opts.ExcludedSetups ?? [])}]");
        if (closed.Count == 0)
        {
            Console.WriteLine("No trades taken.");
            return;
        }

        var wins = closed.Where(t => t.NetPnl > 0).ToList();
        var losses = closed.Where(t => t.NetPnl <= 0).ToList();
        var grossWin = wins.Sum(t => t.NetPnl);
        var grossLoss = Math.Abs(losses.Sum(t => t.NetPnl));
        var finalEquity = equityCurve[^1];
        var totalReturn = (finalEquity / StartingEquity - 1) * 100m;
        var spyStart = spy[WarmupBars].Close;
        var spyReturn = (spy[^1].Close / spyStart - 1) * 100m;

        var peak = 0m; var maxDd = 0m;
        foreach (var e in equityCurve)
        {
            peak = Math.Max(peak, e);
            maxDd = Math.Max(maxDd, (peak - e) / peak);
        }

        Console.WriteLine($"Period:        {calendar[WarmupBars]:yyyy-MM-dd} → {calendar[^1]:yyyy-MM-dd}");
        Console.WriteLine($"Trades:        {closed.Count}  (win rate {(decimal)wins.Count / closed.Count:P1})");
        Console.WriteLine($"Avg win:       {(wins.Count > 0 ? wins.Average(t => t.ReturnPct) : 0):F2}%   Avg loss: {(losses.Count > 0 ? losses.Average(t => t.ReturnPct) : 0):F2}%");
        Console.WriteLine($"Expectancy:    {closed.Average(t => t.ReturnPct):F2}% per trade");
        Console.WriteLine($"Profit factor: {(grossLoss > 0 ? grossWin / grossLoss : 999):F2}");
        Console.WriteLine($"Total return:  {totalReturn:F1}%   (SPY buy&hold over period: {spyReturn:F1}%)");
        Console.WriteLine($"Max drawdown:  {maxDd:P1}");

        Console.WriteLine("\nBy setup:");
        foreach (var g in closed.GroupBy(t => t.Setup).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-22} n={g.Count(),4}  win={(decimal)g.Count(t => t.NetPnl > 0) / g.Count():P0}  avg={g.Average(t => t.ReturnPct):F2}%");

        Console.WriteLine("\nBy conviction:");
        foreach (var g in closed.GroupBy(t => Math.Floor(t.Conviction)).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key}-{g.Key + 1,-4} n={g.Count(),4}  win={(decimal)g.Count(t => t.NetPnl > 0) / g.Count():P0}  avg={g.Average(t => t.ReturnPct):F2}%");

        Console.WriteLine("\nBy exit reason:");
        foreach (var g in closed.GroupBy(t => t.ExitReason.Replace("(gap)", "")).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-12} n={g.Count(),4}  avg={g.Average(t => t.ReturnPct):F2}%");

        var tradesCsv = Path.Combine(dataDir, $"_trades_{opts.Label}.csv");
        File.WriteAllLines(tradesCsv, new[] { "Symbol,EntryDate,ExitDate,EntryPrice,ExitPrice,Quantity,Setup,Conviction,ExitReason,NetPnl,ReturnPct" }
            .Concat(closed.Select(t => string.Join(',', t.Symbol, t.EntryDate.ToString("yyyy-MM-dd"), t.ExitDate.ToString("yyyy-MM-dd"),
                t.EntryPrice, t.ExitPrice, t.Quantity, t.Setup, t.Conviction, t.ExitReason, Math.Round(t.NetPnl, 2), Math.Round(t.ReturnPct, 2)))));
        Console.WriteLine($"\nTrade log: {Path.GetFullPath(tradesCsv)}");
        Console.WriteLine("\nCaveats: survivorship-biased universe (today's members); sentiment/fundamental neutral; no Claude selection. Use for RELATIVE comparisons.");
    }
}
