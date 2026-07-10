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

    // Deterministic candidate generation: production baseline + single-dial
    // nudges (renormalised) + threshold variants + a few seeded random mixes
    // for diversity. Deliberately NOT seeded from own-data optimizer output -
    // own-data evaluation is censored (only trades production weights took)
    // and would pipe its overfit into this, the honest pipeline.
    public static List<HistoricBacktestCandidate> GenerateCandidates(HistoricBacktestCandidate baseline)
    {
        var candidates = new List<HistoricBacktestCandidate> { baseline with { Label = "Production baseline" } };
        var names = new[] { "RSI", "MACD", "Volume", "Sentiment", "Setup quality", "Relative strength", "Price level", "Fundamental momentum" };
        var baseArr = ToArray(baseline.Weights);

        // Single-dial nudges: ±0.05 on each weight, others renormalised.
        for (var i = 0; i < 8; i++)
        {
            foreach (var delta in new[] { 0.05m, -0.05m })
            {
                var arr = (decimal[])baseArr.Clone();
                var nv = arr[i] + delta;
                if (nv < 0.02m || nv > 0.45m) continue;
                arr[i] = nv;
                Renormalise(arr);
                candidates.Add(baseline with
                {
                    Label = $"{names[i]} {(delta > 0 ? "+" : "−")}5pp",
                    Weights = FromArray(arr),
                });
            }
        }

        // Buy-threshold variants on the baseline mix.
        foreach (var t in new[] { -0.5m, 0.5m })
        {
            var nt = Math.Clamp(baseline.BuyThreshold + t, 3.0m, 9.0m);
            if (nt != baseline.BuyThreshold)
                candidates.Add(baseline with { Label = $"Buy threshold {nt:0.0}", BuyThreshold = nt });
        }

        // A few deterministic pseudo-random mixes for diversity (fixed seed so
        // repeated sweeps on the same data give the same answer).
        var rng = new Random(20260710);
        for (var k = 0; k < 5; k++)
        {
            var arr = new decimal[8];
            for (var i = 0; i < 8; i++)
                arr[i] = Math.Max(0.02m, baseArr[i] + (decimal)(rng.NextDouble() - 0.5) * 0.16m);
            Renormalise(arr);
            candidates.Add(baseline with { Label = $"Random mix {k + 1}", Weights = FromArray(arr) });
        }

        return candidates;
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

    private static void Renormalise(decimal[] arr)
    {
        var sum = arr.Sum();
        for (var i = 0; i < arr.Length; i++) arr[i] = Math.Round(arr[i] / sum, 4);
        // Rounding drift lands on the largest component so the sum is exactly 1.
        var drift = 1.0m - arr.Sum();
        var maxIdx = Array.IndexOf(arr, arr.Max());
        arr[maxIdx] += drift;
    }
}
