using SwingTrader.Agents.Research;
using SwingTrader.Core.Constants;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Market;
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
// (watchlist proxy = top-N by screener rank; sentiment and fundamental
// momentum score neutral 0.5 - the only two components not reconstructable
// from bars), and the universe is today's membership (survivorship bias) -
// results are for RELATIVE comparisons, not absolute predictions.
// Relative strength (vs sector ETF bars), price level (support/resistance)
// and the probation momentum-health check ARE computed, via the same shared
// calculators the live services run.

public sealed record DailyBar(DateTime Date, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

// Per-setup entry/exit tactics for the backtester, mirroring the live
// SetupTactics row (docs/setup-tactics-plan). Plain values (not the EF entity)
// so the engine stays free of persistence concerns; the caller reads the
// account's SetupTactics once and hands the map in.
public sealed record HistoricSetupTactics(
    decimal StopLossPct, decimal TargetPct, int GuideHoldDays,
    decimal TrailingActivationPct, decimal TrailingDistancePct);

public sealed record HistoricConfig(
    StrategyWeights Weights,
    decimal BuyThreshold = 6.0m,
    // Legacy single-toggle breakout exclusion. Live no longer excludes any
    // setup (Phase 1, breakouts trade again) and the Lab always passes an
    // explicit value via ToConfig, so this default only affects direct callers
    // (the console tool / tests). Superseded by ExcludedSetups below when set.
    bool ExcludeBreakout = true,
    // Approximates production's bear-market autopause (on by default there):
    // entries are skipped while SPY is below its 200-day average. Coarser than
    // the live classifier (which also wants a falling MA / deep breach / death
    // cross), but far closer to live behaviour than trading straight through a
    // bear. Wired from the account's Bear regime book AutopauseTrading toggle.
    bool RegimeFilter = false,
    decimal? BreakoutQualityOverride = null,
    bool ConvictionSizing = false,
    decimal PositionFraction = 0.10m,
    int MaxOpenPositions = 3,
    decimal MinDollarVolume = 10_000_000m,
    // Trading-day hold cap, mirrored from the account risk profile so the Lab
    // tests the strategy the account actually runs (was a hardcoded 10).
    int MaxHoldDays = 10,
    // Setups whose signals are never entered. Null = derive from
    // ExcludeBreakout (the original single-toggle behaviour); the Lab's
    // trading-rules panel can exclude any set.
    IReadOnlyCollection<SetupType>? ExcludedSetups = null,
    // Fallback trailing stop shape, used for any setup without an entry in
    // SetupTactics below. Mirrors the risk profile (or a Lab rules override).
    decimal TrailingActivationPct = 0.05m,
    decimal TrailingDistancePct = 0.03m,
    // Fallback flat stop/target (fractions, e.g. 0.06 = 6%), used for any setup
    // without an entry in SetupTactics below. Defaults match
    // CapitalRules.DefaultStopLossPct/TargetPct.
    decimal StopLossPct = 0.05m,
    decimal TargetPct = 0.08m,
    // Per-setup entry/exit tactics (docs/setup-tactics-plan). When a signal's
    // SetupType has an entry here, its stop/target/guide-hold/trailing come
    // from it - exactly as live does via SetupTacticsRepository, frozen at
    // entry. Setups not in the map fall back to the flat StopLossPct/TargetPct/
    // MaxHoldDays/Trailing* above. Null = the whole map is absent (every setup
    // uses the flat fallback - the pre-Phase-4 behaviour).
    IReadOnlyDictionary<SetupType, HistoricSetupTactics>? SetupTactics = null,
    // Probation (momentum-health) simulation - mirrors the live two-phase
    // lifecycle: one check at MinHoldDays trading days, one grace day if
    // Borderline, forced exit if the thesis isn't playing out. On by default
    // because production always runs it.
    bool SimulateProbation = true,
    int MinHoldDays = 3,
    decimal MomentumHealthThreshold = 0.5m,
    // Simulator-only "capital pool" sizing (no live equivalent - live uses
    // flat or funnel-tilted per-position sizing). Null = the flat
    // PositionFraction sizing. When set, positions are budgeted against a
    // capped active-capital pool: pool = equity x ActiveCapitalPct, per-
    // position cap = pool x MaxPositionPctOfActive, total deployment never
    // exceeds the pool, and a CashBufferPct stays uninvested. NOTE flat sizing
    // deploys up to MaxOpenPositions x PositionFraction of equity, so a small
    // pool caps total exposure far tighter - the two aren't directly comparable.
    decimal? ActiveCapitalPct = null,
    decimal MaxPositionPctOfActive = 0.33m,
    decimal CashBufferPct = 0.02m,
    // Per-regime "Mixed" mode: when present, the exposure ENVELOPE (autopause,
    // max open positions, flat position size) is resolved from the market
    // regime detected at each simulated day, mirroring how live switches risk
    // books as the market moves. Null = single fixed envelope (the flat scalars
    // above), which is Force-a-single-book and the legacy behaviour. Per-setup
    // exit tactics stay frozen-at-entry and regime-independent either way.
    IReadOnlyDictionary<MarketRegime, RegimeEnvelope>? RegimeBooks = null,
    // Forced-regime runs (the comparison's "Force <regime>" columns) KNOW the
    // regime, so a book with autopause on means pause entries the WHOLE period -
    // not the SPY-vs-200 proxy RegimeFilter uses when the regime is unknown.
    // True => no new entries at all. (Mixed uses per-regime envelopes instead.)
    bool ForceAutopause = false,
    // Protected reserve never traded: total deployment can't exceed the un-locked
    // share of equity (mirrors live's Locked Capital). Per-regime in Mixed via
    // the envelope below. 0 = the whole account is tradeable (legacy behaviour).
    decimal LockedCapitalPct = 0m);

// The slice of a regime risk book the backtest switches on per simulated day.
// Exit tactics are per-setup and frozen at entry, so only the entry-time
// exposure envelope varies by regime here: autopause + position cap + flat size
// + locked-capital reserve (probation and pool-sizing stay from the base config).
public readonly record struct RegimeEnvelope(
    bool Autopause, int MaxOpenPositions, decimal PositionFraction, decimal LockedCapitalPct);

public sealed record HistoricTrade(
    string Symbol, DateTime EntryDate, DateTime ExitDate, decimal EntryPrice, decimal ExitPrice,
    SetupType Setup, decimal Conviction, string ExitReason, decimal ReturnPct,
    // TRADING days held (bar-index difference), matching how guide-hold and the
    // time cap are counted - not calendar days, which run ~1.4x longer.
    // Defaulted so pre-existing stored result JSON stays deserializable.
    int TradingDaysHeld = 0);

// AvgReturnPct IS the bucket's expectancy (mean return over all trades, wins
// and losses). AvgHoldDays is the mean TRADING days held (matching how the
// guide-hold is counted) - a per-setup expectancy surface reads Count /
// WinRate / expectancy / hold together to see which setups earn their capital
// and how long they tie it up. AvgHoldDays defaults to 0 so pre-existing
// stored result JSON stays deserializable.
public sealed record BucketStat(string Key, int Count, decimal WinRate, decimal AvgReturnPct, decimal AvgHoldDays = 0m);

public sealed record HistoricResult(
    DateTime From, DateTime To,
    int Trades, decimal WinRate, decimal AvgWinPct, decimal AvgLossPct,
    decimal ExpectancyPct, decimal ProfitFactor,
    decimal TotalReturnPct, decimal MaxDrawdownPct, decimal SpyReturnPct,
    List<BucketStat> BySetup, List<BucketStat> ByConviction, List<BucketStat> ByExitReason,
    List<HistoricTrade> TradeLog,
    // Calmar ratio: annualised return / max drawdown - return per unit of
    // worst-case pain. >1 is respectable, >2 strong. 0 when drawdown is 0
    // (no meaningful denominator). Default keeps pre-existing stored result
    // JSON deserializable.
    decimal CalmarRatio = 0m);

public static class HistoricBacktester
{
    private const decimal StartingEquity = 10_000m;
    private const decimal CostPerSide = 0.0025m;
    private const int WatchlistSize = 25;
    private const int MaxOrdersPerDay = 3;
    // 85 (was 60): the price-level component mirrors the live service's
    // 120-calendar-day lookback, which is ~82-84 trading bars. Raising the
    // warmup shifted every backtest window's start ~25 bars later, so results
    // from before this change are not comparable with results after it.
    public const int WarmupBars = 85;

    // Sentiment can't be reconstructed from bars - the live pipeline blends a
    // neutral 0.5 when it's unavailable, and the backtest mirrors that.
    private const decimal NeutralScore = 0.5m;

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
        // Exit tactics frozen at entry from the setup's tactics (or the flat
        // fallback), mirroring live's thesis-as-contract: the guide-hold and
        // trailing shape a position exits under never change mid-trade even if
        // the account's tactics are edited afterwards.
        public required int MaxHoldDays;
        public required decimal TrailingActivationPct;
        public required decimal TrailingDistancePct;
        public decimal? TrailingStop;
        // Probation state: RSI at scoring time (the live entry signal's
        // Rsi14), the last verdict, and whether the check cycle is finished
        // (Confirmed, or the grace day has been used).
        public decimal? EntryRsi;
        public string? ProbationVerdict;
        public bool ProbationDone;
    }

    public static async Task<HistoricResult> RunAsync(
        IReadOnlyDictionary<string, DailyBar[]> bars, HistoricConfig cfg,
        // Symbol -> sector-ETF benchmark for the RS component (built from the
        // universe's GICS sectors by the caller so backtest and live use the
        // SAME mapping). Null / missing symbols use the legacy override-or-SPY
        // fallback, matching live's degraded path.
        IReadOnlyDictionary<string, string>? sectorEtfBySymbol = null,
        CancellationToken ct = default)
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

                var (exitPrice, reason) = CheckExit(pos, bar, d);

                // Momentum check: mirrors the live two-phase + runner lifecycle.
                // Live runs the check during morning research and the exit fills
                // at market during the day - approximated here as the day's
                // close. Ordinary stop/target/trailing exits (above) take
                // priority, exactly as they would intraday.
                //  - Probation window: at MinHoldDays (+ one grace day).
                //  - Runner window: every day PAST the guide-hold, a still-
                //    healthy position keeps running, a stalled one exits
                //    (docs/setup-tactics-plan Phase 3).
                if (exitPrice is null && cfg.SimulateProbation)
                {
                    var daysHeld = d - pos.EntryBarIndex;
                    var isGraceRecheck = !pos.ProbationDone
                        && daysHeld == cfg.MinHoldDays + 1 && pos.ProbationVerdict == "Borderline";
                    var inProbation = !pos.ProbationDone && (daysHeld == cfg.MinHoldDays || isGraceRecheck);
                    var inRunnerWindow = pos.ProbationDone && daysHeld > pos.MaxHoldDays;
                    if (inProbation || inRunnerWindow)
                    {
                        var verdict = await ProbationVerdictAsync(
                            indicators, bars, index, pos, today, cfg.MomentumHealthThreshold, sectorEtfBySymbol);
                        // One grace day only - a still-Borderline recheck is an Exit.
                        if (isGraceRecheck && verdict == "Borderline") verdict = "Exit";
                        if (inProbation)
                        {
                            pos.ProbationVerdict = verdict;
                            pos.ProbationDone = verdict != "Borderline";
                        }
                        if (verdict == "Exit")
                            (exitPrice, reason) = (bar.Close, inRunnerWindow ? "RunnerStalled" : "Probation");
                    }
                }

                if (exitPrice.HasValue)
                {
                    var proceeds = exitPrice.Value * pos.Quantity * (1 - CostPerSide);
                    var cost = pos.EntryPrice * pos.Quantity * (1 + CostPerSide);
                    cash += proceeds;
                    closed.Add(new HistoricTrade(pos.Symbol, pos.EntryDate, today, pos.EntryPrice, exitPrice.Value,
                        pos.Setup, pos.Conviction, reason!, Math.Round((proceeds - cost) / cost * 100m, 2),
                        TradingDaysHeld: d - pos.EntryBarIndex));
                    open.Remove(pos);
                }
                else if (bar.Close >= pos.EntryPrice * (1 + pos.TrailingActivationPct))
                {
                    var newTrail = bar.Close * (1 - pos.TrailingDistancePct);
                    if (pos.TrailingStop is null || newTrail > pos.TrailingStop) pos.TrailingStop = newTrail;
                }
            }

            // Enter new positions tomorrow at the open. The exposure envelope
            // (autopause, position cap, size) is resolved per-day: in Mixed mode
            // from the regime detected at this bar, else from the fixed config.
            var env = ResolveEnvelope(cfg, spy, d);
            if (open.Count < env.MaxOpenPositions && !env.Autopause)
            {
                // Explicit exclusion set wins; otherwise the original
                // single-toggle behaviour (ExcludeBreakout) applies.
                var excludedSetups = cfg.ExcludedSetups
                    ?? (cfg.ExcludeBreakout ? [SetupType.Breakout] : Array.Empty<SetupType>());
                var candidates = new List<(string Symbol, decimal Conviction, SetupType Setup, decimal Rsi)>();
                foreach (var symbol in watchlist)
                {
                    if (open.Any(p => p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))) continue;
                    var scored = await ScoreAsync(indicators, cfg, bars, index, symbol, today, sectorEtfBySymbol);
                    if (scored is { } s && s.Conviction >= cfg.BuyThreshold && s.Rsi <= 75m
                        && !excludedSetups.Contains(s.Setup))
                        candidates.Add((symbol, s.Conviction, s.Setup, s.Rsi));
                }

                foreach (var c in candidates.OrderByDescending(c => c.Conviction).Take(MaxOrdersPerDay))
                {
                    if (open.Count >= env.MaxOpenPositions) break;
                    var entryBar = GetBar(bars, index, c.Symbol, calendar[d + 1]);
                    if (entryBar is null || entryBar.Open <= 0) continue;

                    var deployedValue = open.Sum(p =>
                        (GetBar(bars, index, p.Symbol, today)?.Close ?? p.EntryPrice) * p.Quantity);

                    decimal budget;
                    if (cfg.ActiveCapitalPct is { } activePct)
                    {
                        // Lab-only pool sizing (no live equivalent): the pool bounds
                        // both the per-position budget and TOTAL deployment, and a
                        // cash buffer stays parked. The pool IS the usable-capital
                        // definition here, so Locked Capital doesn't also apply.
                        var activePool = equity * activePct;
                        var remainingPool = activePool - deployedValue;
                        if (remainingPool <= 0) break; // pool fully deployed
                        var spendableCash = cash / (1 + CostPerSide) - equity * cfg.CashBufferPct;
                        budget = Math.Min(Math.Min(activePool * cfg.MaxPositionPctOfActive, spendableCash), remainingPool);
                    }
                    else
                    {
                        // Flat sizing mirrors live: PositionFraction of equity per
                        // trade, with TOTAL deployment capped at the un-locked share
                        // (Locked Capital is a protected reserve, never traded).
                        // Per-regime in Mixed; 0 = whole account tradeable (legacy).
                        var usableCapital = equity * (1m - env.LockedCapitalPct);
                        var remainingUsable = usableCapital - deployedValue;
                        if (remainingUsable <= 0m) break; // reserve fully committed
                        budget = Math.Min(Math.Min(equity * env.PositionFraction, cash / (1 + CostPerSide)), remainingUsable);
                    }
                    if (cfg.ConvictionSizing)
                    {
                        var t = (Math.Clamp(c.Conviction, 6.0m, 9.0m) - 6.0m) / 3.0m;
                        budget *= 0.5m + t * 0.5m;
                    }
                    if (budget < 50m) continue;

                    var qty = Math.Floor(budget / entryBar.Open * 1000m) / 1000m;
                    if (qty <= 0) continue;

                    // Resolve the setup's tactics (stop/target/guide-hold/
                    // trailing) - live does this via SetupTacticsRepository.
                    // Any setup absent from the map falls back to the flat cfg
                    // values, so a run without tactics behaves as pre-Phase-4.
                    var tac = cfg.SetupTactics is not null && cfg.SetupTactics.TryGetValue(c.Setup, out var found) ? found : null;
                    var stopPct = tac?.StopLossPct ?? cfg.StopLossPct;
                    var targetPct = tac?.TargetPct ?? cfg.TargetPct;
                    var guideHold = tac?.GuideHoldDays ?? cfg.MaxHoldDays;
                    var trailAct = tac?.TrailingActivationPct ?? cfg.TrailingActivationPct;
                    var trailDist = tac?.TrailingDistancePct ?? cfg.TrailingDistancePct;

                    var (stop, target) = Core.Trading.EntryLevelCalculator.Calculate(entryBar.Open, stopPct, targetPct);
                    cash -= entryBar.Open * qty * (1 + CostPerSide);
                    open.Add(new Position
                    {
                        Symbol = c.Symbol, EntryDate = calendar[d + 1], EntryBarIndex = d + 1, EntryPrice = entryBar.Open,
                        Quantity = qty, StopLoss = stop, Target = target, Setup = c.Setup, Conviction = c.Conviction,
                        MaxHoldDays = guideHold, TrailingActivationPct = trailAct, TrailingDistancePct = trailDist,
                        EntryRsi = c.Rsi,
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

        // Calmar = annualised (CAGR) return / max drawdown.
        var totalReturnFrac = equityCurve.Count > 0 ? equityCurve[^1] / StartingEquity - 1m : 0m;
        var years = Math.Max(0.25, (calendar[^1] - calendar[WarmupBars]).TotalDays / 365.25);
        var cagr = (decimal)(Math.Pow((double)(1m + totalReturnFrac), 1.0 / years) - 1.0);
        var calmar = maxDd > 0 ? Math.Round(cagr / maxDd, 2) : 0m;

        List<BucketStat> Bucket<TKey>(Func<HistoricTrade, TKey> keySelector) => closed
            .GroupBy(keySelector)
            .Select(g => new BucketStat(g.Key!.ToString()!, g.Count(),
                Math.Round((decimal)g.Count(t => t.ReturnPct > 0) / g.Count(), 4),
                Math.Round(g.Average(t => t.ReturnPct), 2),
                Math.Round((decimal)g.Average(t => t.TradingDaysHeld), 1)))
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
            closed,
            calmar);
    }

    private static (decimal? ExitPrice, string? Reason) CheckExit(Position pos, DailyBar bar, int currentBarIndex)
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
        // Hard time cap = guide-hold x HoldCeilingMultiple (Phase 3): the
        // guide-hold is soft, so the absolute backstop mirrors live
        // PositionMonitorService. Trading days held = bar-index difference.
        var hardCeiling = (int)Math.Ceiling(pos.MaxHoldDays * CapitalRules.HoldCeilingMultiple);
        if (currentBarIndex - pos.EntryBarIndex > hardCeiling) return (bar.Close, "TimeExit");
        return (null, null);
    }

    internal static async Task<(decimal Conviction, SetupType Setup, decimal Rsi)?> ScoreAsync(
        IndicatorService indicators, HistoricConfig cfg,
        IReadOnlyDictionary<string, DailyBar[]> bars, Dictionary<string, Dictionary<DateTime, int>> index,
        string symbol, DateTime today,
        IReadOnlyDictionary<string, string>? sectorEtfBySymbol = null)
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

        // Relative strength: this symbol's 5d return vs its sector ETF's, via
        // the SAME shared calculator + SectorEtfMap the live scorer uses. Null
        // (ETF bars missing for this window) blends neutral, mirroring live.
        var rsScore = ComputeRelativeStrengthScore(bars, index, symbol, history, today, sectorEtfBySymbol);

        // Price level: support/resistance from this symbol's own warmup bars
        // (<= today only - no lookahead), same shared calculator as live.
        // InsufficientData scores 0.5, exactly the live blend behaviour.
        var priceBars = history.Select(b => new PriceBar(b.High, b.Low, b.Close, b.Volume)).ToList();
        var priceLevel = PriceLevelCalculator.Compute(priceBars, history[^1].Close, PriceLevelDefaults);

        var conviction = ConvictionScorer.Calculate(
            cfg.Weights,
            ConvictionScorer.ScoreRsi(ind.Rsi14),
            ConvictionScorer.ScoreMacd(ind.MacdHistogram, prev.Histogram),
            ConvictionScorer.ScoreVolume(ind.VolumeRatio),
            setupScore,
            relativeStrengthScore: rsScore ?? NeutralScore,
            priceLevelScore: priceLevel.Score);

        return (conviction, setup, ind.Rsi14.Value);
    }

    // Production defaults (PriceLevelConfig class defaults) - the backtest
    // deliberately does not read appsettings overrides so results are
    // reproducible across environments.
    private static readonly Infrastructure.Configuration.PriceLevelConfig PriceLevelDefaults = new();

    internal static decimal? ComputeRelativeStrengthScore(
        IReadOnlyDictionary<string, DailyBar[]> bars, Dictionary<string, Dictionary<DateTime, int>> index,
        string symbol, DailyBar[] history, DateTime today,
        IReadOnlyDictionary<string, string>? sectorEtfBySymbol = null) =>
        ComputeRelativeStrengthOutcome(bars, index, symbol, history, today, sectorEtfBySymbol)?.Score;

    internal static RelativeStrengthOutcome? ComputeRelativeStrengthOutcome(
        IReadOnlyDictionary<string, DailyBar[]> bars, Dictionary<string, Dictionary<DateTime, int>> index,
        string symbol, DailyBar[] history, DateTime today,
        IReadOnlyDictionary<string, string>? sectorEtfBySymbol = null)
    {
        var etf = sectorEtfBySymbol is not null && sectorEtfBySymbol.TryGetValue(symbol, out var mapped)
            ? mapped
            : SectorEtfMap.GetEtf(symbol);
        if (!bars.TryGetValue(etf, out var etfBars) ||
            !index.TryGetValue(etf, out var etfDates) ||
            !etfDates.TryGetValue(today, out var etfIdx) ||
            etfIdx < RelativeStrengthCalculator.WindowDays - 1)
            return null;

        var window = RelativeStrengthCalculator.WindowDays;
        var stockCloses = history[^window..].Select(b => b.Close).ToList();
        var etfCloses = etfBars[(etfIdx - window + 1)..(etfIdx + 1)].Select(b => b.Close).ToList();

        return RelativeStrengthCalculator.Compute(stockCloses, etfCloses);
    }

    // The probation verdict from bar-reconstructable inputs: today's RSI and
    // volume ratio (same IndicatorService the scorer uses), price vs entry,
    // and the RS relative return - fed through the SAME shared
    // MomentumHealthCalculator the live monitor runs. Missing data (not
    // enough history at this index) scores neutral, mirroring live's
    // "never exit on missing data".
    private static async Task<string> ProbationVerdictAsync(
        IndicatorService indicators,
        IReadOnlyDictionary<string, DailyBar[]> bars, Dictionary<string, Dictionary<DateTime, int>> index,
        Position pos, DateTime today, decimal threshold,
        IReadOnlyDictionary<string, string>? sectorEtfBySymbol)
    {
        decimal? rsi = null, volumeRatio = null, relativeReturn = null;
        var currentPrice = pos.EntryPrice; // neutral price component if no bar

        if (index.TryGetValue(pos.Symbol, out var dates) && dates.TryGetValue(today, out var i) && i >= WarmupBars)
        {
            var history = bars[pos.Symbol][(i - WarmupBars + 1)..(i + 1)];
            currentPrice = history[^1].Close;
            var candles = history.Select(b => new StockCandle
            {
                Symbol = pos.Symbol, Timestamp = b.Date, Open = b.Open, High = b.High, Low = b.Low, Close = b.Close,
                Volume = (long)b.Volume,
            }).ToList();
            var ind = await indicators.CalculateAllAsync(candles);
            rsi = ind.Rsi14;
            volumeRatio = ind.VolumeRatio;
            relativeReturn = ComputeRelativeStrengthOutcome(
                bars, index, pos.Symbol, history, today, sectorEtfBySymbol)?.RelativeReturn;
        }

        return Monitor.MomentumHealthCalculator.Compute(
            rsi, pos.EntryRsi, volumeRatio, currentPrice, pos.EntryPrice, relativeReturn, threshold).Verdict;
    }

    // Mirror of ResearchPipeline.DetectSetup (private there) - keep in sync.
    private static SetupType DetectSetup(IndicatorResult ind, List<StockCandle> candles)
    {
        var price = candles[^1].Close;

        // Recovery confirmation (17 Jul 2026, in lockstep with the live
        // pipeline): oversold alone isn't the setup - the price must also be
        // higher than 4 bars ago, i.e. the bounce has begun. Results from
        // before this change measured plain "oversold" and are not comparable.
        if (ind.Rsi14 < 35 && ind.BollingerLower.HasValue && price > ind.BollingerLower.Value
            && candles.Count >= 4 && price > candles[^4].Close)
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
            // SPY and the sector ETFs are in the bar set only as regime/RS
            // benchmarks - never as trade candidates.
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

    // The exposure envelope in force on simulated day d. Mixed mode (RegimeBooks
    // present) resolves it from the regime detected at this bar; otherwise the
    // fixed config scalars, with the legacy SPY-vs-200 autopause proxy preserved
    // exactly (skip entries only when RegimeFilter is on AND SPY is sub-200).
    private static RegimeEnvelope ResolveEnvelope(HistoricConfig cfg, DailyBar[] spy, int d)
    {
        if (cfg.RegimeBooks is null)
        {
            // ForceAutopause pauses unconditionally (a forced autopausing book);
            // otherwise the legacy SPY-vs-200 proxy for the live bear autopause.
            var autopause = cfg.ForceAutopause || (cfg.RegimeFilter && !SpyAboveSma200(spy, d));
            return new RegimeEnvelope(autopause, cfg.MaxOpenPositions, cfg.PositionFraction, cfg.LockedCapitalPct);
        }
        var regime = RegimeAt(spy, d);
        if (cfg.RegimeBooks.TryGetValue(regime, out var book)) return book;
        // Regime with no supplied book (e.g. Crisis without a Crisis entry) falls
        // back to Neutral, then to the flat scalars.
        if (cfg.RegimeBooks.TryGetValue(MarketRegime.Neutral, out var neutral)) return neutral;
        return new RegimeEnvelope(false, cfg.MaxOpenPositions, cfg.PositionFraction, cfg.LockedCapitalPct);
    }

    // Historical regime at bar d from SPY closes (price-structure only - no VIX
    // history, so Crisis never triggers here; Mixed treats it as Bear/Neutral by
    // structure). Only the last ~220 closes matter for the 50/200-day MAs.
    private static MarketRegime RegimeAt(DailyBar[] spy, int d)
    {
        var start = Math.Max(0, d - 219);
        var window = new List<decimal>(d - start + 1);
        for (var i = start; i <= d && i < spy.Length; i++) window.Add(spy[i].Close);
        return MarketRegimeService.ClassifyFromCloses(window) ?? MarketRegime.Neutral;
    }

    private static DailyBar? GetBar(
        IReadOnlyDictionary<string, DailyBar[]> bars, Dictionary<string, Dictionary<DateTime, int>> index,
        string symbol, DateTime date) =>
        index.TryGetValue(symbol, out var dates) && dates.TryGetValue(date, out var i) ? bars[symbol][i] : null;
}
