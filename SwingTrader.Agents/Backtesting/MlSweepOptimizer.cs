using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Backtesting;

// The optimizer's second, ML-guided search pool: the SAME dial space and
// eligibility guardrails as SweepOptimizer's deterministic/random sweep, but
// covered by MULTI-START CMA-ES instead of nudges + a uniform random fill.
//
// A single CMA-ES run concentrates its search distribution toward whatever
// region looks best early - efficient once it's in a good basin, but a poor
// substitute for coverage: it can converge into the first promising pocket
// it finds and never revisit the rest of the simplex. Restarting from many
// independent, widely-scattered starting points fixes that - each restart
// gets a short, cheap local search (a few generations is enough for the
// covariance adaptation to start pulling toward a better region), and NumSeeds
// restarts scattered across the space give broad coverage no single run could,
// while each restart still searches its neighbourhood far more efficiently
// than uniform random sampling would.
public static class MlSweepOptimizer
{
    // 30 restarts x 3 generations each x lambda(9) = 810 evaluations, tuned
    // to land around an hour end-to-end (~4.5s/backtest on the train window,
    // matching the deterministic sweep's ~400-in-20-40min pace). Restart 0 is
    // always the production baseline (guaranteed local coverage of the region
    // the traditional sweep already nudges around); the rest start from
    // independently-seeded random points spread across the full weight range.
    public const int NumSeeds = 30;
    public const int GenerationsPerSeed = 3;

    // Dimensions searched: the six live weights (RSI, MACD, Volume, Setup
    // quality, Relative strength, Price level) plus the Buy threshold.
    private const int Dimensions = 7;

    // Worse than any feasible AdjustedExpectancyPct could plausibly be -
    // candidates that fail the trade-count/drawdown guardrails are pushed to
    // the back of the search without needing a violation-magnitude gradient.
    private const double InfeasiblePenalty = 1000.0;

    // Unit coordinate -> domain scale. CMA-ES adapts per-dimension scale via
    // its covariance matrix regardless of the initial units, so these just
    // need to be roughly the right order of magnitude. A coordinate of ±3 is
    // enough to reach the domain clamps below from the baseline, which is the
    // range random-restart starting points are drawn from.
    private const decimal WeightUnitPerCoordinate = 0.10m;   // 1.0 coordinate ~ 10pp weight change
    private const decimal ThresholdUnitPerCoordinate = 1.0m; // 1.0 coordinate ~ 1.0 Buy-threshold point
    private const double RandomStartRange = 3.0;             // random restarts sample coordinates in [-range, +range]

    public static readonly int Lambda = CmaEs.ComputeLambda(Dimensions);
    public static readonly int BudgetPerSeed = GenerationsPerSeed * Lambda;
    public static readonly int ActualCandidateCount = NumSeeds * BudgetPerSeed;

    // runBacktest evaluates ONE candidate on the train window and returns its
    // HistoricResult - the caller (BacktestConsumerFunction) owns persisting
    // per-candidate progress inside that delegate, same pattern as the
    // deterministic sweep's foreach loop.
    public static async Task<List<SweepCandidateResult>> OptimizeAsync(
        HistoricBacktestCandidate baseline,
        Func<HistoricBacktestCandidate, CancellationToken, Task<HistoricResult>> runBacktest,
        DailyBar[] trainSpy, decimal baselineMaxDrawdownPct, CancellationToken ct)
    {
        var baseArr = SweepOptimizer.ToArray(baseline.Weights);
        var liveBudget = SweepOptimizer.LiveIndices.Sum(i => baseArr[i]);
        var results = new List<SweepCandidateResult>();
        var counter = 0;

        // Deterministic per-seed starting points: seed 0 is always the
        // unperturbed baseline; the rest are independently-seeded random
        // draws so repeated sweeps on the same data retrace the same set of
        // restarts, but the restarts themselves are genuinely scattered.
        var startRng = new Random(20260711);
        double[] StartingPoint(int seedIndex) =>
            seedIndex == 0
                ? new double[Dimensions]
                : Enumerable.Range(0, Dimensions)
                    .Select(_ => (startRng.NextDouble() * 2 - 1) * RandomStartRange)
                    .ToArray();

        HistoricBacktestCandidate MapToCandidate(double[] x, int seedIndex)
        {
            var arr = (decimal[])baseArr.Clone();
            for (var i = 0; i < SweepOptimizer.LiveIndices.Length; i++)
            {
                var idx = SweepOptimizer.LiveIndices[i];
                var nv = baseArr[idx] + (decimal)x[i] * WeightUnitPerCoordinate;
                arr[idx] = Math.Clamp(nv, 0.02m, 0.45m);
            }
            SweepOptimizer.RenormaliseLive(arr, liveBudget);
            var threshold = Math.Clamp(baseline.BuyThreshold + (decimal)x[^1] * ThresholdUnitPerCoordinate, 3.0m, 9.0m);

            return baseline with
            {
                Label = $"ML search {seedIndex + 1}.{++counter}",
                Weights = SweepOptimizer.FromArray(arr),
                BuyThreshold = threshold,
            };
        }

        async Task<double> Evaluate(double[] x, int seedIndex, CancellationToken token)
        {
            var candidate = MapToCandidate(x, seedIndex);
            var result = await runBacktest(candidate, token);
            var summary = SweepOptimizer.Summarise(candidate, result, trainSpy, baselineMaxDrawdownPct);
            results.Add(summary);
            return summary.MetConstraints ? -(double)summary.AdjustedExpectancyPct : InfeasiblePenalty;
        }

        for (var seedIndex = 0; seedIndex < NumSeeds; seedIndex++)
        {
            ct.ThrowIfCancellationRequested();
            counter = 0;
            await CmaEs.MinimizeAsync(
                Dimensions, StartingPoint(seedIndex), initialSigma: 1.0, BudgetPerSeed,
                (x, token) => Evaluate(x, seedIndex, token), ct,
                rngSeed: 20260711 + seedIndex + 1); // distinct noise sequence per restart
        }

        return results;
    }
}
