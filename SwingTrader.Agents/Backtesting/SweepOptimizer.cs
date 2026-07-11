using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Backtesting;

// The Optimizer-tab sweep: generate a capped set of candidate dial configs
// around the production baseline, evaluate them on a TRAIN window, pick the
// best by market-adjusted expectancy (subject to guardrails), then validate
// the winner on the HELD-OUT remainder it never saw. The validation split is
// a hard requirement, not decoration: with 8 free weights and a few hundred
// trades, an unvalidated sweep will always "find" a config that looks great
// on the data it was tuned on.

public sealed record SweepCandidateResult(
    string Label,
    HistoricBacktestWeights Weights,
    decimal BuyThreshold,
    bool ExcludeBreakout,
    bool AutopauseDuringBear,
    int Trades,
    decimal WinRate,
    decimal ExpectancyPct,               // raw avg return/trade on the train window
    decimal AdjustedExpectancyPct,       // avg (trade return - SPY return over same days)
    decimal ProfitFactor,
    decimal MaxDrawdownPct,
    decimal TotalReturnPct,
    bool MetConstraints,
    string? RejectionReason,             // why this candidate couldn't win (null if eligible)
    // The score every eligible candidate is actually RANKED on (across both
    // search pools and for the final winner): the worse of the two
    // train-window halves, each discounted to a lower confidence bound. It's
    // deliberately more pessimistic than AdjustedExpectancyPct so a big mean
    // from a small/one-regime sample can't win on luck; the headline
    // AdjustedExpectancyPct is kept alongside it for display.
    decimal RobustScorePct = 0m);

public sealed record SweepValidation(
    HistoricResult Train,                // winner's full result on the train window
    HistoricResult Holdout,              // winner's full result on the held-out window
    decimal TrainAdjustedExpectancyPct,
    decimal HoldoutAdjustedExpectancyPct,
    HistoricResult BaselineHoldout,      // production baseline on the same held-out window
    decimal BaselineHoldoutAdjustedExpectancyPct,
    bool HeldUp,                         // false = the edge collapsed out-of-sample
    string Verdict);                     // plain-English interpretation of the above

// Result of an on-demand out-of-sample validation of ONE configuration (the
// A/B tab's "Validate out-of-sample" button): the same train/holdout split
// and hold-up verdict the optimizer applies to its sweep winner, applied to
// dials the user tuned by hand - which are in-sample by construction until
// they pass this.
public sealed record ValidateResult(
    string Mode,                         // "validate" - discriminator for the UI
    SweepValidation Validation);

public sealed record SweepResult(
    string Mode,                         // "sweep" - discriminator for the UI
    SweepCandidateResult Baseline,
    SweepCandidateResult Winner,
    SweepValidation Validation,
    List<SweepCandidateResult> Candidates,
    string? Explanation,                 // Claude writeup (null if unavailable)
    // Head-to-head: the best eligible candidate found by each search method,
    // and which one actually produced the overall Winner above - so the UI
    // can show whether the deterministic/random sweep or the CMA-ES-guided
    // search found the better config, not just report the winner in isolation.
    SweepCandidateResult? BestTraditional = null,
    SweepCandidateResult? BestMlSearch = null,
    string? WinnerSource = null);

public static class SweepOptimizer
{
    // ~70% of the calendar to optimize on, the rest held out for validation.
    public const decimal TrainFraction = 0.70m;

    // Guardrails on what may win: a config that wins on a handful of trades is
    // noise, and one that wins by tolerating a much deeper drawdown isn't a
    // better strategy, just a riskier one.
    public const int MinTrainTrades = 40;
    public const decimal MaxDrawdownCeilingFactor = 1.25m;

    // The first optimizer run's winner sat EXACTLY on MinTrainTrades - 40
    // trades where the baseline took 195 - and collapsed out-of-sample. A
    // fixed floor is a boundary the search learns to ride: fewer trades
    // makes a lucky high average easier. So candidates must also trade at a
    // comparable rate to the baseline - a config trading 5x less isn't a
    // tuned version of the strategy, it's a different (nearly inactive) one.
    public const decimal MinTradeRateVsBaselineFactor = 0.5m;

    // Candidates are RANKED on a lower confidence bound - mean adjusted
    // expectancy minus this many standard errors - rather than the raw mean,
    // so a big average from a small, noisy sample is discounted by its own
    // uncertainty instead of winning on luck. ~1.5 SE is roughly a one-sided
    // 93% bound: harsh enough to kill 40-lucky-trades candidates, loose
    // enough not to bury genuinely better configs with normal variance.
    public const decimal LcbStdErrMultiplier = 1.5m;

    // A winner "holds up" if its held-out adjusted expectancy keeps at least
    // half its train value AND beats the baseline on the same held-out window.
    public const decimal HoldupRetentionFactor = 0.5m;

    // Weight-array indices, matching HistoricBacktestWeights field order.
    // "Live" components are the ones the historic engine reconstructs from
    // price/volume bars - since the engine gained relative strength (vs stored
    // sector-ETF bars) and price level (support/resistance), only Sentiment
    // and Fundamental momentum remain "dead" (fixed neutral 0.5).
    // Internal (not private) so MlSweepOptimizer's CMA-ES search shares the
    // exact same live/dead split and renormalisation as the deterministic sweep.
    internal static readonly int[] LiveIndices = [0, 1, 2, 4, 5, 6]; // RSI, MACD, Volume, Setup, RelStrength, PriceLevel
    private static readonly int[] DeadIndices = [3, 7];             // Sentiment, FundMomentum

    // Deterministic candidate generation: production baseline + variations
    // that redistribute weight ONLY among the live components, holding the
    // dead two at their production values. Shifting weight into/out of a
    // dead component just dilutes conviction scores toward the neutral
    // midpoint - a candidate could "win" purely because sentiment is dead in
    // the backtest, a conclusion that wouldn't transfer to production where
    // sentiment has real data. Same reasoning as the locked dials in the A/B
    // tab's historic mode. Also deliberately NOT seeded from own-data
    // optimizer output - own-data evaluation is censored (only trades
    // production weights took) and would pipe its overfit into this, the
    // honest pipeline.
    // Total candidates per sweep. Each engine run costs a few seconds, so
    // ~400 keeps the job in the 20-40 minute range while searching the live
    // simplex far more densely than the original ~25 (bumped from 200).
    public const int TargetCandidateCount = 400;

    public static List<HistoricBacktestCandidate> GenerateCandidates(HistoricBacktestCandidate baseline)
    {
        var candidates = new List<HistoricBacktestCandidate> { baseline with { Label = "Production baseline" } };
        var names = new[] { "RSI", "MACD", "Volume", "Sentiment", "Setup quality", "Relative strength", "Price level", "Fundamental momentum" };
        var baseArr = ToArray(baseline.Weights);
        var liveBudget = LiveIndices.Sum(i => baseArr[i]); // what the live dials must always sum to

        // Single-dial nudges: ±5pp and ±10pp on each LIVE weight, other live
        // weights renormalised to keep the live total (and therefore the
        // overall sum) unchanged.
        foreach (var i in LiveIndices)
        {
            foreach (var delta in new[] { 0.05m, -0.05m, 0.10m, -0.10m })
            {
                var arr = (decimal[])baseArr.Clone();
                var nv = arr[i] + delta;
                if (nv < 0.02m || nv > 0.45m) continue;
                arr[i] = nv;
                RenormaliseLive(arr, liveBudget);
                candidates.Add(baseline with
                {
                    Label = $"{names[i]} {(delta > 0 ? "+" : "−")}{Math.Abs(delta) * 100:0}pp",
                    Weights = FromArray(arr),
                });
            }
        }

        // Buy-threshold variants on the baseline mix.
        foreach (var t in new[] { -1.5m, -1.0m, -0.5m, -0.25m, 0.25m, 0.5m, 1.0m, 1.5m })
        {
            var nt = Math.Clamp(baseline.BuyThreshold + t, 3.0m, 9.0m);
            if (nt != baseline.BuyThreshold && candidates.All(c => c.BuyThreshold != nt || c.Weights != baseline.Weights))
                candidates.Add(baseline with { Label = $"Buy threshold {nt:0.0}", BuyThreshold = nt });
        }

        // Bear-autopause toggle: unlike the dead components, the regime filter
        // IS reconstructable from bars, so testing it flipped is a legitimate,
        // transferable hypothesis.
        candidates.Add(baseline with
        {
            Label = baseline.AutopauseDuringBear ? "Bear autopause OFF" : "Bear autopause ON",
            AutopauseDuringBear = !baseline.AutopauseDuringBear,
        });

        // Structural variants: small nudges rarely separate from noise on a
        // finite trade sample, so also try genuinely different LIVE mixes -
        // each spreads/concentrates the same live budget differently, dead
        // dials untouched. Each also gets an autopause-flipped twin so the
        // sweep can spot interactions between the mix and the regime filter.
        // Proportions follow LiveIndices order: RSI, MACD, Volume, Setup
        // quality, Relative strength, Price level.
        var structural = new (string Label, decimal[] Proportions)[]
        {
            ("Equal live weights", [1m / 6, 1m / 6, 1m / 6, 1m / 6, 1m / 6, 1m / 6]),
            ("Momentum tilt (MACD/Volume heavy)", [0.10m, 0.28m, 0.28m, 0.10m, 0.14m, 0.10m]),
            ("Pattern tilt (RSI/Setup heavy)", [0.26m, 0.08m, 0.10m, 0.30m, 0.08m, 0.18m]),
            ("Structure tilt (RelStrength/PriceLevel heavy)", [0.12m, 0.10m, 0.12m, 0.16m, 0.25m, 0.25m]),
        };
        foreach (var (label, proportions) in structural)
        {
            var mix = MakeLiveMix(baseline, baseArr, liveBudget, label, proportions);
            candidates.Add(mix);
            candidates.Add(mix with
            {
                Label = $"{label} + autopause {(baseline.AutopauseDuringBear ? "OFF" : "ON")}",
                AutopauseDuringBear = !baseline.AutopauseDuringBear,
            });
        }

        // Pair trades among live components: shift 4pp and 8pp from each live
        // dial to each other - every "this signal matters more than that one"
        // hypothesis, both directions, two magnitudes.
        foreach (var from in LiveIndices)
        {
            foreach (var to in LiveIndices)
            {
                if (from == to) continue;
                foreach (var magnitude in new[] { 0.04m, 0.08m })
                {
                    var arr = (decimal[])baseArr.Clone();
                    var shift = Math.Min(magnitude, arr[from] - 0.02m);
                    if (shift <= 0m || arr[to] + shift > 0.45m) continue;
                    arr[from] -= shift;
                    arr[to] += shift;
                    candidates.Add(baseline with
                    {
                        Label = $"Shift {magnitude * 100:0}pp: {names[from]} → {names[to]}",
                        Weights = FromArray(arr),
                    });
                }
            }
        }

        // Fill the remainder to TargetCandidateCount with deterministic
        // pseudo-random LIVE mixes (fixed seed - repeated sweeps on the same
        // data give the same answer). Half also jitter the Buy threshold so
        // the search covers the joint weights-threshold space, not just the
        // simplex at one threshold.
        var rng = new Random(20260710);
        var k = 0;
        while (candidates.Count < TargetCandidateCount)
        {
            k++;
            var arr = (decimal[])baseArr.Clone();
            foreach (var i in LiveIndices)
                arr[i] = Math.Max(0.02m, baseArr[i] + (decimal)(rng.NextDouble() - 0.5) * 0.30m);
            RenormaliseLive(arr, liveBudget);

            var jitterThreshold = k % 2 == 0;
            var nt = jitterThreshold
                ? Math.Clamp(Math.Round(baseline.BuyThreshold + (decimal)(rng.NextDouble() - 0.5) * 2.0m, 1), 3.0m, 9.0m)
                : baseline.BuyThreshold;

            candidates.Add(baseline with
            {
                Label = jitterThreshold ? $"Random mix {k} (threshold {nt:0.0})" : $"Random mix {k}",
                Weights = FromArray(arr),
                BuyThreshold = nt,
            });
        }

        return candidates;
    }

    // A candidate whose live dials follow the given proportions of the live
    // budget, dead dials kept at baseline.
    private static HistoricBacktestCandidate MakeLiveMix(
        HistoricBacktestCandidate baseline, decimal[] baseArr, decimal liveBudget, string label, decimal[] liveProportions)
    {
        var arr = (decimal[])baseArr.Clone();
        for (var k = 0; k < LiveIndices.Length; k++)
            arr[LiveIndices[k]] = liveProportions[k] * liveBudget;
        RenormaliseLive(arr, liveBudget); // absorb rounding so the sum is exact
        return baseline with { Label = label, Weights = FromArray(arr) };
    }

    // Split each symbol's bars by the train-cutoff date derived from the SPY
    // calendar. The holdout slice keeps enough leading history for indicator
    // warmup, but the warmup bars overlap the train window by design - only
    // the trades generated after warmup land in the held-out evaluation.
    public static (Dictionary<string, DailyBar[]> Train, Dictionary<string, DailyBar[]> Holdout) SplitBars(
        IReadOnlyDictionary<string, DailyBar[]> bars, int warmupBars)
    {
        var spy = bars["SPY"];
        var cutoffIndex = (int)(spy.Length * TrainFraction);
        var cutoffDate = spy[cutoffIndex].Date;
        var holdoutStartDate = spy[Math.Max(0, cutoffIndex - warmupBars)].Date;

        var train = new Dictionary<string, DailyBar[]>(StringComparer.OrdinalIgnoreCase);
        var holdout = new Dictionary<string, DailyBar[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (symbol, series) in bars)
        {
            var t = series.Where(b => b.Date < cutoffDate).ToArray();
            var h = series.Where(b => b.Date >= holdoutStartDate).ToArray();
            if (t.Length > 0) train[symbol] = t;
            if (h.Length > 0) holdout[symbol] = h;
        }
        return (train, holdout);
    }

    // Market-adjusted expectancy: for each closed trade, subtract SPY's return
    // over the same holding period, then average. This stops the objective
    // rewarding configs merely for being long during a rising market.
    public static decimal AdjustedExpectancy(HistoricResult result, DailyBar[] spy) =>
        AdjustedExpectancy(result.TradeLog, spy);

    // Split-half consistency check: the same adjusted expectancy computed
    // separately over the earlier and later halves of the result's own
    // window (trades split by entry date at the calendar midpoint). Costs
    // nothing - it re-reads the trade log the backtest already produced.
    // A candidate that made all its money in one stretch of the tuning
    // window scores well overall but poorly on its weak half, and that
    // lucky-in-one-regime profile is exactly what tends to collapse
    // out-of-sample - so the ML search ranks on the WORSE half (see
    // MlSweepOptimizer) while the holdout stays untouched for the final
    // verdict.
    public static (decimal Early, decimal Late) SplitHalfAdjustedExpectancy(HistoricResult result, DailyBar[] spy)
    {
        var midpoint = result.From + (result.To - result.From) / 2;
        return (
            AdjustedExpectancy(result.TradeLog.Where(t => t.EntryDate < midpoint), spy),
            AdjustedExpectancy(result.TradeLog.Where(t => t.EntryDate >= midpoint), spy));
    }

    // The split-half check with the LCB discount applied to each half - the
    // ML search's actual fitness combines both suspicions in one number:
    // "your worse stretch of the window, discounted for how few trades that
    // judgement rests on".
    public static (decimal Early, decimal Late) SplitHalfLowerBoundExpectancy(HistoricResult result, DailyBar[] spy)
    {
        var midpoint = result.From + (result.To - result.From) / 2;
        return (
            LowerBoundExpectancy(AdjustedTradeReturns(result.TradeLog.Where(t => t.EntryDate < midpoint), spy)),
            LowerBoundExpectancy(AdjustedTradeReturns(result.TradeLog.Where(t => t.EntryDate >= midpoint), spy)));
    }

    // Mean minus LcbStdErrMultiplier standard errors: the same average, but
    // discounted by its own sampling uncertainty. A 1.6% mean over 40 trades
    // (huge SE) can rank below a 0.8% mean over 190 trades (tiny SE), which
    // is exactly the ordering a raw mean gets wrong.
    public static decimal LowerBoundExpectancy(IReadOnlyList<decimal> returns)
    {
        if (returns.Count == 0) return 0m;
        var mean = returns.Average();
        if (returns.Count < 2) return Math.Round(mean, 2); // no variance estimate - the trade floors handle tiny samples
        var variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
        var standardError = (decimal)Math.Sqrt((double)variance / returns.Count);
        return Math.Round(mean - LcbStdErrMultiplier * standardError, 2);
    }

    private static decimal AdjustedExpectancy(IEnumerable<HistoricTrade> trades, DailyBar[] spy)
    {
        var returns = AdjustedTradeReturns(trades, spy);
        return returns.Count > 0 ? Math.Round(returns.Average(), 2) : 0m;
    }

    private static List<decimal> AdjustedTradeReturns(IEnumerable<HistoricTrade> trades, DailyBar[] spy)
    {
        var spyByDate = spy.ToDictionary(b => b.Date, b => b.Close);

        var returns = new List<decimal>();
        foreach (var t in trades)
        {
            if (!spyByDate.TryGetValue(t.EntryDate, out var spyEntry) ||
                !spyByDate.TryGetValue(t.ExitDate, out var spyExit) || spyEntry <= 0)
            {
                returns.Add(t.ReturnPct); // no SPY bar to adjust against - use raw
                continue;
            }
            returns.Add(t.ReturnPct - (spyExit / spyEntry - 1m) * 100m);
        }
        return returns;
    }

    // baselineTrades enables the trade-rate floor: a candidate trading far
    // less than the baseline is a different (nearly inactive) strategy, not a
    // tuned version of it, and its high average is easy to hit by luck.
    // Passed as 0 (floor disabled) for the baseline candidate itself and
    // anywhere the reference count isn't known yet.
    // The effective train-trade floor: the fixed minimum, raised to a share
    // of the baseline's own trade count when that's known. Shared so the ML
    // search's infeasibility penalty slope points at the same threshold
    // Summarise rejects on.
    public static int MinTradesFor(int baselineTrades) =>
        baselineTrades > 0
            ? Math.Max(MinTrainTrades, (int)Math.Ceiling(baselineTrades * MinTradeRateVsBaselineFactor))
            : MinTrainTrades;

    public static SweepCandidateResult Summarise(
        HistoricBacktestCandidate candidate, HistoricResult result, DailyBar[] spy, decimal baselineMaxDrawdownPct,
        int baselineTrades = 0)
    {
        var adjusted = AdjustedExpectancy(result, spy);
        var minTrades = MinTradesFor(baselineTrades);

        string? rejection = null;
        if (result.Trades < minTrades)
            rejection = baselineTrades > 0 && minTrades > MinTrainTrades
                ? $"only {result.Trades} trades on the train window (minimum {minTrades}, ≥{MinTradeRateVsBaselineFactor:P0} of the baseline's {baselineTrades}) — too inactive to be a tuned version of the strategy"
                : $"only {result.Trades} trades on the train window (minimum {minTrades}) — too few to trust";
        else if (baselineMaxDrawdownPct > 0 && result.MaxDrawdownPct > baselineMaxDrawdownPct * MaxDrawdownCeilingFactor)
            rejection = $"max drawdown {result.MaxDrawdownPct:F1}% exceeds the ceiling ({baselineMaxDrawdownPct * MaxDrawdownCeilingFactor:F1}%, 1.25× baseline)";

        var (earlyLcb, lateLcb) = SplitHalfLowerBoundExpectancy(result, spy);

        return new SweepCandidateResult(
            candidate.Label, candidate.Weights, candidate.BuyThreshold, candidate.ExcludeBreakout, candidate.AutopauseDuringBear,
            result.Trades, result.WinRate, result.ExpectancyPct, adjusted,
            result.ProfitFactor, result.MaxDrawdownPct, result.TotalReturnPct,
            rejection is null, rejection,
            RobustScorePct: Math.Min(earlyLcb, lateLcb));
    }

    public static SweepValidation BuildValidation(
        HistoricResult winnerTrain, HistoricResult winnerHoldout, HistoricResult baselineHoldout,
        DailyBar[] trainSpy, DailyBar[] holdoutSpy)
    {
        var trainAdj = AdjustedExpectancy(winnerTrain, trainSpy);
        var holdoutAdj = AdjustedExpectancy(winnerHoldout, holdoutSpy);
        var baselineAdj = AdjustedExpectancy(baselineHoldout, holdoutSpy);

        // Three bars to clear: keep at least half the tuning-window edge on
        // held-out data, beat the baseline on that same held-out data, and be
        // POSITIVE outright - "less bad than production" is not a config worth
        // recommending, however well it retains its (negative) train number.
        var heldUp = holdoutAdj > 0m
            && holdoutAdj >= trainAdj * HoldupRetentionFactor
            && holdoutAdj >= baselineAdj;

        var verdict = heldUp
            ? $"Held up out-of-sample: the winning dials kept a market-adjusted expectancy of {holdoutAdj:F2}%/trade " +
              $"on data they were never tuned on (vs {trainAdj:F2}% on the tuning window, and vs the current production " +
              $"dials' {baselineAdj:F2}% on the same held-out period). That's the strongest evidence a backtest can give — " +
              "but it is still one historical period, not a guarantee."
            : $"Did NOT hold up out-of-sample: on data the dials were never tuned on, market-adjusted expectancy was " +
              $"{holdoutAdj:F2}%/trade (vs {trainAdj:F2}% on the tuning window; current production dials scored {baselineAdj:F2}% " +
              "on the same held-out period). A config that only wins on the data it was tuned on is most likely noise — " +
              "applying it is not recommended.";

        return new SweepValidation(
            winnerTrain with { TradeLog = [] },
            winnerHoldout with { TradeLog = [] },
            trainAdj, holdoutAdj,
            baselineHoldout with { TradeLog = [] },
            baselineAdj, heldUp, verdict);
    }

    internal static decimal[] ToArray(HistoricBacktestWeights w) =>
        [w.Rsi, w.Macd, w.Volume, w.Sentiment, w.SetupQuality, w.RelativeStrength, w.PriceLevel, w.FundamentalMomentum];

    internal static HistoricBacktestWeights FromArray(decimal[] a) =>
        new(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7]);

    // Scales only the live components so they sum to the fixed live budget
    // (total minus the untouched dead weights) - the overall sum stays exactly
    // 1.0 without moving any dead dial.
    internal static void RenormaliseLive(decimal[] arr, decimal liveBudget)
    {
        var liveSum = LiveIndices.Sum(i => arr[i]);
        if (liveSum <= 0m) return;
        foreach (var i in LiveIndices) arr[i] = Math.Round(arr[i] / liveSum * liveBudget, 4);
        // Rounding drift lands on the largest live component so the sum is exact.
        var drift = liveBudget - LiveIndices.Sum(i => arr[i]);
        var maxIdx = LiveIndices.OrderByDescending(i => arr[i]).First();
        arr[maxIdx] += drift;
    }
}
