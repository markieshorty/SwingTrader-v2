namespace SwingTrader.Agents.Backtesting;

// Monte Carlo robustness check for a backtested configuration: bootstrap-
// resamples the run's OWN trade log (same number of trades, drawn with
// replacement) thousands of times and rebuilds the equity path for each
// ordering. The single historical path is one draw from the strategy's
// return distribution - if the config's result depends on the lucky ORDER
// of its trades (a couple of early winners compounding), the percentile
// spread exposes it. This deliberately does NOT create new information
// about the strategy's edge (the trades are the same trades); it measures
// sequence risk and the realistic drawdown range around that edge.
public sealed record MonteCarloResult(
    string Mode,                          // "montecarlo" - discriminator for the UI
    int Resamples,
    int Trades,
    decimal PositionFraction,             // equity slice per trade used to compound
    decimal ActualTotalReturnPct,         // the real historical ordering
    decimal ActualMaxDrawdownPct,
    decimal ActualCalmarRatio,
    decimal SpyReturnPct,
    decimal MedianTotalReturnPct,
    decimal P5TotalReturnPct,             // pessimistic ordering (5th percentile)
    decimal P95TotalReturnPct,            // optimistic ordering
    decimal MedianMaxDrawdownPct,
    decimal P95MaxDrawdownPct,            // realistic worst-case drawdown
    decimal ProbabilityOfLossPct,         // % of orderings that end below breakeven
    decimal ProbabilityBeatingSpyPct,     // % of orderings that beat SPY buy-and-hold
    string Verdict);

public static class MonteCarloSimulator
{
    public const int DefaultResamples = 2_000;

    // Fixed seed: repeated runs on the same trade log give the same answer -
    // a Monte Carlo whose verdict changes on refresh teaches distrust.
    private const int Seed = 20260711;

    public static MonteCarloResult Run(
        HistoricResult result, decimal positionFraction, int resamples = DefaultResamples)
    {
        var returns = result.TradeLog.Select(t => t.ReturnPct).ToArray();
        if (returns.Length < 10)
        {
            return new MonteCarloResult("montecarlo", 0, returns.Length, positionFraction,
                result.TotalReturnPct, result.MaxDrawdownPct, result.CalmarRatio, result.SpyReturnPct,
                0, 0, 0, 0, 0, 0, 0,
                $"Not enough trades ({returns.Length}) for a meaningful resampling — need at least 10.");
        }

        var rng = new Random(Seed);
        var totals = new decimal[resamples];
        var drawdowns = new decimal[resamples];

        for (var i = 0; i < resamples; i++)
        {
            decimal equity = 1m, peak = 1m, maxDd = 0m;
            for (var k = 0; k < returns.Length; k++)
            {
                var ret = returns[rng.Next(returns.Length)];
                // Each trade moves the account by its return on the equity
                // slice it occupied - the same approximation the flat-sizing
                // model makes (pool-mode callers pass their effective
                // per-position fraction).
                equity *= 1m + ret / 100m * positionFraction;
                if (equity > peak) peak = equity;
                else if (peak > 0) maxDd = Math.Max(maxDd, (peak - equity) / peak);
            }
            totals[i] = Math.Round((equity - 1m) * 100m, 1);
            drawdowns[i] = Math.Round(maxDd * 100m, 1);
        }

        Array.Sort(totals);
        var sortedDds = (decimal[])drawdowns.Clone();
        Array.Sort(sortedDds);

        decimal Pct(decimal[] sorted, double p) => sorted[(int)Math.Clamp(p * (sorted.Length - 1), 0, sorted.Length - 1)];

        var median = Pct(totals, 0.50);
        var p5 = Pct(totals, 0.05);
        var p95 = Pct(totals, 0.95);
        var medianDd = Pct(sortedDds, 0.50);
        var p95Dd = Pct(sortedDds, 0.95);
        var probLoss = Math.Round(totals.Count(t => t < 0m) * 100m / resamples, 1);
        var probBeatSpy = Math.Round(totals.Count(t => t > result.SpyReturnPct) * 100m / resamples, 1);

        var verdict = p5 > 0m
            ? $"Robust to trade ordering: even the pessimistic 5th-percentile shuffle of these {returns.Length} trades " +
              $"finishes at +{p5:F1}%, and only {probLoss:F1}% of {resamples:N0} orderings lose money. " +
              $"Budget emotionally for a ~{p95Dd:F1}% drawdown (95th percentile) rather than the single " +
              $"historical path's {result.MaxDrawdownPct:F1}% — the order of trades was partly luck."
            : $"Sequence-fragile: {probLoss:F1}% of {resamples:N0} reshuffled orderings of these {returns.Length} trades " +
              $"lose money (5th percentile: {p5:F1}%). The historical result leans on the lucky ORDER of its " +
              "trades, not just their quality — treat the headline number with caution.";

        return new MonteCarloResult(
            "montecarlo", resamples, returns.Length, positionFraction,
            result.TotalReturnPct, result.MaxDrawdownPct, result.CalmarRatio, result.SpyReturnPct,
            median, p5, p95, medianDd, p95Dd, probLoss, probBeatSpy, verdict);
    }
}
