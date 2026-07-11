using System.Collections.Concurrent;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Backtesting;

// One evaluated ML-search candidate: the summary the sweep ranks on plus the
// full train-window result (the consumer needs the latter to validate the
// winner out-of-sample without re-running it).
public sealed record MlEvaluation(SweepCandidateResult Summary, HistoricResult Result);

// The optimizer's second, ML-guided search pool: the SAME dial space and
// eligibility guardrails as SweepOptimizer's deterministic/random sweep, but
// covered by SUCCESSIVE-HALVING multi-start CMA-ES instead of nudges + a
// uniform random fill.
//
// Why this shape: a single CMA-ES run concentrates on the first promising
// basin it finds; pure multi-start with a fixed shallow budget per restart
// (the previous design: 30 starts x 3 generations) covers broadly but never
// runs any restart long enough for CMA-ES's covariance adaptation - the
// thing that makes it better than random sampling - to actually kick in.
// Successive halving spends the same total budget adaptively: every start
// gets a cheap screening generation, then only the starts showing promise
// get progressively deeper searches, with the final survivors getting enough
// generations for real adaptation. Broad where nothing is known, deep where
// early evidence says it's worth it.
public static class MlSweepOptimizer
{
    // The halving schedule: (searches still running, generations they get in
    // that stage). Stage 1 screens every start with one generation; the best
    // half continue; the final few go deep. Totals 30x1 + 15x2 + 5x6 = 90
    // generations x lambda(9) = 810 evaluations - same budget as the flat
    // 30x3 design this replaced, tuned to land around an hour end-to-end at
    // ~4.5s/backtest (less with parallel evaluation).
    private static readonly (int Searches, int Generations)[] Stages =
    [
        (30, 1),
        (15, 2),
        (5, 6),
    ];

    public static int InitialSeeds => Stages[0].Searches;

    // Dimensions searched: the six live weights (RSI, MACD, Volume, Setup
    // quality, Relative strength, Price level) plus the Buy threshold.
    private const int Dimensions = 7;

    // Base fitness for candidates failing the trade-count/drawdown
    // guardrails - worse than any feasible AdjustedExpectancyPct could
    // plausibly be. GRADED by how badly they fail (see Fitness below): a
    // 39-trade candidate must rank above a 3-trade one, otherwise a fully
    // infeasible population gives the rank-based update pure noise and the
    // search can't find its way back to feasible territory.
    private const double InfeasiblePenalty = 1000.0;

    private const decimal ThresholdUnitPerCoordinate = 1.0m; // 1.0 coordinate ~ 1.0 Buy-threshold point
    private const double RandomStartRange = 3.0;             // random restarts sample coordinates in [-range, +range]

    public static readonly int Lambda = CmaEs.ComputeLambda(Dimensions);
    public static readonly int ActualCandidateCount = Stages.Sum(s => s.Searches * s.Generations) * Lambda;

    // runBacktest evaluates ONE candidate on the train window and returns its
    // HistoricResult - the caller (BacktestConsumerFunction) owns per-candidate
    // progress persistence inside that delegate. With maxParallelism > 1, up
    // to that many runBacktest calls run concurrently (offspring within a
    // CMA-ES generation are independent), so the delegate must be safe to
    // call concurrently - the engine itself is stateless over read-only bars,
    // so in practice that means serializing any DB progress write.
    //
    // Deterministic for fixed inputs regardless of maxParallelism: offspring
    // are sampled before evaluation and fitness lands by index, and the
    // returned list is reconstructed in (seed, evaluation) order rather than
    // completion order.
    public static async Task<List<MlEvaluation>> OptimizeAsync(
        HistoricBacktestCandidate baseline,
        Func<HistoricBacktestCandidate, CancellationToken, Task<HistoricResult>> runBacktest,
        DailyBar[] trainSpy, decimal baselineMaxDrawdownPct, CancellationToken ct,
        int maxParallelism = 1)
    {
        var baseArr = SweepOptimizer.ToArray(baseline.Weights);
        var liveBudget = SweepOptimizer.LiveIndices.Sum(i => baseArr[i]);

        // Keyed by the exact x-array instance CmaEs hands to evaluate (and
        // stores in its History), so results can be reassembled in
        // deterministic search order after parallel evaluation.
        var evaluated = new ConcurrentDictionary<double[], MlEvaluation>(ReferenceEqualityComparer.Instance);

        // Softmax over the live components, centred so x = 0 reproduces the
        // baseline EXACTLY: w_i = liveBudget * base_i*e^{x_i} / sum_j base_j*e^{x_j}.
        // Unlike the previous clamp-then-renormalise mapping, this is smooth
        // and injective - no flat regions where many distinct search points
        // collapse onto one clamped candidate and feed the rank-based update
        // zero signal - and every point of the simplex is reachable without
        // ever producing an invalid mix. RenormaliseLive only absorbs the
        // 4dp rounding drift, it never has to rescale.
        HistoricBacktestCandidate MapToCandidate(double[] x)
        {
            var arr = (decimal[])baseArr.Clone();
            var lives = SweepOptimizer.LiveIndices;
            var numerators = new double[lives.Length];
            double denominator = 0;
            for (var i = 0; i < lives.Length; i++)
            {
                numerators[i] = (double)baseArr[lives[i]] * Math.Exp(x[i]);
                denominator += numerators[i];
            }
            for (var i = 0; i < lives.Length; i++)
                arr[lives[i]] = Math.Round((decimal)(numerators[i] / denominator) * liveBudget, 4);
            SweepOptimizer.RenormaliseLive(arr, liveBudget);

            var threshold = Math.Clamp(
                baseline.BuyThreshold + (decimal)x[^1] * ThresholdUnitPerCoordinate, 3.0m, 9.0m);

            return baseline with
            {
                Label = "ML search", // placeholder - real label assigned in search order below
                Weights = SweepOptimizer.FromArray(arr),
                BuyThreshold = threshold,
            };
        }

        // Feasible candidates compete on the WORSE of their two train-window
        // halves (split-half adjusted expectancy), not the overall number.
        // The failure mode the final out-of-sample validation keeps catching
        // is a candidate that won the train window by being lucky in one
        // stretch of it - that profile scores well overall but poorly on its
        // weak half, so ranking on the weak half cuts it at the screening
        // stage instead of spending deep-search budget on it. Deliberately
        // NOT the holdout: the moment survivor selection touched holdout
        // data, the final "held up out-of-sample" verdict would be
        // optimized-on and meaningless. Note the reported summaries still
        // carry the full-window AdjustedExpectancyPct - this consistency
        // score only steers the search and the halving.
        //
        // The infeasible are pushed behind ALL feasible candidates but still
        // ordered by how close they came, so a generation with no feasible
        // member still gives the search a slope back toward feasibility.
        double Fitness(SweepCandidateResult summary, HistoricResult result)
        {
            if (summary.MetConstraints)
            {
                var (early, late) = SweepOptimizer.SplitHalfAdjustedExpectancy(result, trainSpy);
                return -(double)Math.Min(early, late);
            }
            if (result.Trades < SweepOptimizer.MinTrainTrades)
                return InfeasiblePenalty + (SweepOptimizer.MinTrainTrades - result.Trades);
            var ceiling = baselineMaxDrawdownPct * SweepOptimizer.MaxDrawdownCeilingFactor;
            return InfeasiblePenalty + (double)Math.Max(0m, result.MaxDrawdownPct - ceiling);
        }

        async Task<double> Evaluate(double[] x, CancellationToken token)
        {
            var candidate = MapToCandidate(x);
            var result = await runBacktest(candidate, token);
            var summary = SweepOptimizer.Summarise(candidate, result, trainSpy, baselineMaxDrawdownPct);
            evaluated[x] = new MlEvaluation(summary, result);
            return Fitness(summary, result);
        }

        // Deterministic starting points: seed 0 is always the unperturbed
        // baseline (guaranteed local coverage of the region the traditional
        // sweep nudges around); the rest are independently-seeded random
        // draws scattered across the full logit range, so repeated sweeps on
        // the same data retrace the same set of restarts.
        var startRng = new Random(20260711);
        double[] StartingPoint(int seedIndex) =>
            seedIndex == 0
                ? new double[Dimensions]
                : Enumerable.Range(0, Dimensions)
                    .Select(_ => (startRng.NextDouble() * 2 - 1) * RandomStartRange)
                    .ToArray();

        var searches = Enumerable.Range(0, InitialSeeds)
            .Select(i => (SeedIndex: i, Search: new CmaEsSearch(
                Dimensions, StartingPoint(i), initialSigma: 1.0, rngSeed: 20260711 + i + 1)))
            .ToList();

        var active = searches;
        foreach (var (stageSearches, stageGenerations) in Stages)
        {
            ct.ThrowIfCancellationRequested();

            // Survivor selection: best fitness achieved so far, seed index as
            // the deterministic tiebreak. Stage 1 takes everyone (nothing has
            // run yet, all BestFitness values are +infinity).
            active = active
                .OrderBy(s => s.Search.BestFitness)
                .ThenBy(s => s.SeedIndex)
                .Take(stageSearches)
                .ToList();

            foreach (var (_, search) in active)
                await search.RunGenerationsAsync(stageGenerations, Evaluate, ct, maxParallelism);
        }

        // Reassemble in (seed, evaluation) order - deterministic whatever the
        // parallel completion order was - and assign the human-readable
        // labels the sweep result surfaces.
        var results = new List<MlEvaluation>(ActualCandidateCount);
        foreach (var (seedIndex, search) in searches)
        {
            var j = 0;
            foreach (var evaluation in search.History)
            {
                var entry = evaluated[evaluation.X];
                results.Add(entry with { Summary = entry.Summary with { Label = $"ML search {seedIndex + 1}.{++j}" } });
            }
        }
        return results;
    }
}
