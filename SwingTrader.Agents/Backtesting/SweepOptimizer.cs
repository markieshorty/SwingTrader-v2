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
    int Trades,
    decimal WinRate,
    decimal ExpectancyPct,               // raw avg return/trade on the train window
    decimal AdjustedExpectancyPct,       // avg (trade return - SPY return over same days)
    decimal ProfitFactor,
    decimal MaxDrawdownPct,
    decimal TotalReturnPct,
    bool MetConstraints,
    string? RejectionReason);            // why this candidate couldn't win (null if eligible)

public sealed record SweepValidation(
    HistoricResult Train,                // winner's full result on the train window
    HistoricResult Holdout,              // winner's full result on the held-out window
    decimal TrainAdjustedExpectancyPct,
    decimal HoldoutAdjustedExpectancyPct,
    HistoricResult BaselineHoldout,      // production baseline on the same held-out window
    decimal BaselineHoldoutAdjustedExpectancyPct,
    bool HeldUp,                         // false = the edge collapsed out-of-sample
    string Verdict);                     // plain-English interpretation of the above

public sealed record SweepResult(
    string Mode,                         // "sweep" - discriminator for the UI
    SweepCandidateResult Baseline,
    SweepCandidateResult Winner,
    SweepValidation Validation,
    List<SweepCandidateResult> Candidates,
    string? Explanation);                // Claude writeup (null if unavailable)

public static class SweepOptimizer
{
    // ~70% of the calendar to optimize on, the rest held out for validation.
    public const decimal TrainFraction = 0.70m;

    // Guardrails on what may win: a config that wins on a handful of trades is
    // noise, and one that wins by tolerating a much deeper drawdown isn't a
    // better strategy, just a riskier one.
    public const int MinTrainTrades = 40;
    public const decimal MaxDrawdownCeilingFactor = 1.25m;

    // A winner "holds up" if its held-out adjusted expectancy keeps at least
    // half its train value AND beats the baseline on the same held-out window.
    public const decimal HoldupRetentionFactor = 0.5m;

    // Weight-array indices, matching HistoricBacktestWeights field order.
    // "Live" components are the ones the historic engine reconstructs from
    // price/volume bars; the "dead" four (Sentiment, Relative strength, Price
    // level, Fundamental momentum) score a fixed neutral 0.5 in a backtest.
    private static readonly int[] LiveIndices = [0, 1, 2, 4];   // RSI, MACD, Volume, Setup quality
    private static readonly int[] DeadIndices = [3, 5, 6, 7];   // Sentiment, RelStrength, PriceLevel, FundMomentum

    // Deterministic candidate generation: production baseline + variations
    // that redistribute weight ONLY among the live components, holding the
    // dead four at their production values. Shifting weight into/out of a
    // dead component just dilutes conviction scores toward the neutral
    // midpoint - a candidate could "win" purely because sentiment is dead in
    // the backtest, a conclusion that wouldn't transfer to production where
    // sentiment has real data. Same reasoning as the locked dials in the A/B
    // tab's historic mode. Also deliberately NOT seeded from own-data
    // optimizer output - own-data evaluation is censored (only trades
    // production weights took) and would pipe its overfit into this, the
    // honest pipeline.
    public static List<HistoricBacktestCandidate> GenerateCandidates(HistoricBacktestCandidate baseline)
    {
        var candidates = new List<HistoricBacktestCandidate> { baseline with { Label = "Production baseline" } };
        var names = new[] { "RSI", "MACD", "Volume", "Sentiment", "Setup quality", "Relative strength", "Price level", "Fundamental momentum" };
        var baseArr = ToArray(baseline.Weights);
        var liveBudget = LiveIndices.Sum(i => baseArr[i]); // what the live dials must always sum to

        // Single-dial nudges: ±0.05 on each LIVE weight, other live weights
        // renormalised to keep the live total (and therefore the overall sum)
        // unchanged.
        foreach (var i in LiveIndices)
        {
            foreach (var delta in new[] { 0.05m, -0.05m })
            {
                var arr = (decimal[])baseArr.Clone();
                var nv = arr[i] + delta;
                if (nv < 0.02m || nv > 0.45m) continue;
                arr[i] = nv;
                RenormaliseLive(arr, liveBudget);
                candidates.Add(baseline with
                {
                    Label = $"{names[i]} {(delta > 0 ? "+" : "−")}5pp",
                    Weights = FromArray(arr),
                });
            }
        }

        // Buy-threshold variants on the baseline mix.
        foreach (var t in new[] { -1.0m, -0.5m, 0.5m, 1.0m })
        {
            var nt = Math.Clamp(baseline.BuyThreshold + t, 3.0m, 9.0m);
            if (nt != baseline.BuyThreshold && candidates.All(c => c.BuyThreshold != nt || c.Weights != baseline.Weights))
                candidates.Add(baseline with { Label = $"Buy threshold {nt:0.0}", BuyThreshold = nt });
        }

        // Structural variants: small nudges rarely separate from noise on a
        // finite trade sample, so also try genuinely different LIVE mixes -
        // each spreads/concentrates the same live budget differently, dead
        // dials untouched.
        candidates.Add(MakeLiveMix(baseline, baseArr, liveBudget, "Equal live weights", [0.25m, 0.25m, 0.25m, 0.25m]));
        candidates.Add(MakeLiveMix(baseline, baseArr, liveBudget, "Momentum tilt (MACD/Volume heavy)", [0.15m, 0.35m, 0.35m, 0.15m]));
        candidates.Add(MakeLiveMix(baseline, baseArr, liveBudget, "Pattern tilt (RSI/Setup heavy)", [0.35m, 0.10m, 0.15m, 0.40m]));

        // Pair trades among live components: shift a meaningful chunk (8pp)
        // from one live dial to another - each tests a distinct "this signal
        // matters more than that one" hypothesis.
        var pairs = new (int From, int To, string Desc)[]
        {
            (0, 2, "RSI → Volume"),
            (2, 4, "Volume → Setup quality"),
            (1, 0, "MACD → RSI"),
            (4, 1, "Setup quality → MACD"),
        };
        foreach (var (from, to, desc) in pairs)
        {
            var arr = (decimal[])baseArr.Clone();
            var shift = Math.Min(0.08m, arr[from] - 0.02m);
            if (shift <= 0m || arr[to] + shift > 0.45m) continue;
            arr[from] -= shift;
            arr[to] += shift;
            candidates.Add(baseline with { Label = $"Shift 8pp: {desc}", Weights = FromArray(arr) });
        }

        // A few deterministic pseudo-random LIVE mixes for diversity (fixed
        // seed so repeated sweeps on the same data give the same answer).
        var rng = new Random(20260710);
        for (var k = 0; k < 5; k++)
        {
            var arr = (decimal[])baseArr.Clone();
            foreach (var i in LiveIndices)
                arr[i] = Math.Max(0.02m, baseArr[i] + (decimal)(rng.NextDouble() - 0.5) * 0.16m);
            RenormaliseLive(arr, liveBudget);
            candidates.Add(baseline with { Label = $"Random live mix {k + 1}", Weights = FromArray(arr) });
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
    public static decimal AdjustedExpectancy(HistoricResult result, DailyBar[] spy)
    {
        if (result.TradeLog.Count == 0) return 0m;
        var spyByDate = spy.ToDictionary(b => b.Date, b => b.Close);

        decimal total = 0m;
        var counted = 0;
        foreach (var t in result.TradeLog)
        {
            if (!spyByDate.TryGetValue(t.EntryDate, out var spyEntry) ||
                !spyByDate.TryGetValue(t.ExitDate, out var spyExit) || spyEntry <= 0)
            {
                total += t.ReturnPct; // no SPY bar to adjust against - use raw
                counted++;
                continue;
            }
            total += t.ReturnPct - (spyExit / spyEntry - 1m) * 100m;
            counted++;
        }
        return counted > 0 ? Math.Round(total / counted, 2) : 0m;
    }

    public static SweepCandidateResult Summarise(
        HistoricBacktestCandidate candidate, HistoricResult result, DailyBar[] spy, decimal baselineMaxDrawdownPct)
    {
        var adjusted = AdjustedExpectancy(result, spy);
        string? rejection = null;
        if (result.Trades < MinTrainTrades)
            rejection = $"only {result.Trades} trades on the train window (minimum {MinTrainTrades}) — too few to trust";
        else if (baselineMaxDrawdownPct > 0 && result.MaxDrawdownPct > baselineMaxDrawdownPct * MaxDrawdownCeilingFactor)
            rejection = $"max drawdown {result.MaxDrawdownPct:F1}% exceeds the ceiling ({baselineMaxDrawdownPct * MaxDrawdownCeilingFactor:F1}%, 1.25× baseline)";

        return new SweepCandidateResult(
            candidate.Label, candidate.Weights, candidate.BuyThreshold, candidate.ExcludeBreakout,
            result.Trades, result.WinRate, result.ExpectancyPct, adjusted,
            result.ProfitFactor, result.MaxDrawdownPct, result.TotalReturnPct,
            rejection is null, rejection);
    }

    public static SweepValidation BuildValidation(
        HistoricResult winnerTrain, HistoricResult winnerHoldout, HistoricResult baselineHoldout,
        DailyBar[] trainSpy, DailyBar[] holdoutSpy)
    {
        var trainAdj = AdjustedExpectancy(winnerTrain, trainSpy);
        var holdoutAdj = AdjustedExpectancy(winnerHoldout, holdoutSpy);
        var baselineAdj = AdjustedExpectancy(baselineHoldout, holdoutSpy);

        var heldUp = holdoutAdj >= trainAdj * HoldupRetentionFactor && holdoutAdj >= baselineAdj;

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

    private static decimal[] ToArray(HistoricBacktestWeights w) =>
        [w.Rsi, w.Macd, w.Volume, w.Sentiment, w.SetupQuality, w.RelativeStrength, w.PriceLevel, w.FundamentalMomentum];

    private static HistoricBacktestWeights FromArray(decimal[] a) =>
        new(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7]);

    // Scales only the live components so they sum to the fixed live budget
    // (total minus the untouched dead weights) - the overall sum stays exactly
    // 1.0 without moving any dead dial.
    private static void RenormaliseLive(decimal[] arr, decimal liveBudget)
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
