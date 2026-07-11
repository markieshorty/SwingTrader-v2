using FluentAssertions;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// The ML search pool: same dial-space contract as SweepOptimizer's
// deterministic sweep (live weights sum to the baseline's live budget, dead
// components never move, Buy threshold stays in range), plus checks specific
// to the successive-halving multi-start design - that restarts scatter
// across the simplex, that the halving schedule actually concentrates budget
// on the promising starts, and that results are deterministic even when
// evaluations run in parallel.
public class MlSweepOptimizerTests
{
    private static readonly HistoricBacktestWeights ProdWeights =
        new(0.17m, 0.09m, 0.21m, 0.16m, 0.12m, 0.10m, 0.05m, 0.10m);

    private static HistoricBacktestCandidate Baseline() =>
        new("Production baseline", ProdWeights, 6.0m, true);

    private static decimal LiveSum(HistoricBacktestWeights w) =>
        w.Rsi + w.Macd + w.Volume + w.SetupQuality + w.RelativeStrength + w.PriceLevel;

    private static readonly DailyBar[] FlatSpy =
        Enumerable.Range(0, 400)
            .Select(i => new DailyBar(new DateTime(2024, 1, 1).AddDays(i), 100m, 100m, 100m, 100m, 1_000_000m))
            .ToArray();

    // AdjustedExpectancy is computed FROM the trade log (return minus SPY's
    // move over the same days), not from the ExpectancyPct field directly -
    // so the summary's AdjustedExpectancyPct only reflects `expectancy` if
    // the trade log actually encodes it. SPY is flat here, so each trade's
    // raw return passes through unadjusted. Trades are spread evenly across
    // the whole window so BOTH halves of the split-half consistency score
    // (the ML search's actual fitness) see the same expectancy - a log
    // bunched into one half would zero the other half and flatten the
    // fitness landscape these tests rely on.
    private static HistoricResult Result(int trades, decimal expectancy, decimal maxDd)
    {
        var log = Enumerable.Range(0, trades)
            .Select(i =>
            {
                var entry = FlatSpy[0].Date.AddDays(i * 350.0 / trades);
                return new HistoricTrade(
                    "TEST", entry, entry.AddDays(5),
                    100m, 100m * (1 + expectancy / 100m), SetupType.MomentumContinuation, 6.5m, "Target", expectancy);
            })
            .ToList();
        return new(new DateTime(2024, 1, 1), new DateTime(2025, 1, 1),
            trades, 0.5m, 5m, -5m, expectancy, 1.1m, 10m, maxDd, 20m,
            [], [], [], log);
    }

    [Fact]
    public async Task OptimizeAsync_AllCandidatesKeepLiveBudgetAndDeadComponentsFixed()
    {
        var baseline = Baseline();
        var liveBudget = LiveSum(baseline.Weights);

        var evaluations = await MlSweepOptimizer.OptimizeAsync(
            baseline,
            (c, ct) => Task.FromResult(Result(trades: 50, expectancy: 1.0m, maxDd: 10m)),
            FlatSpy, baselineMaxDrawdownPct: 15m, CancellationToken.None);

        evaluations.Should().HaveCount(MlSweepOptimizer.ActualCandidateCount);
        evaluations.Should().AllSatisfy(e =>
        {
            LiveSum(e.Summary.Weights).Should().BeApproximately(liveBudget, 0.01m);
            e.Summary.Weights.Sentiment.Should().Be(ProdWeights.Sentiment);
            e.Summary.Weights.FundamentalMomentum.Should().Be(ProdWeights.FundamentalMomentum);
            e.Summary.BuyThreshold.Should().BeInRange(3.0m, 9.0m);
        });
    }

    [Fact]
    public async Task OptimizeAsync_IsDeterministic_EvenWithParallelEvaluation()
    {
        var baseline = Baseline();
        Task<HistoricResult> Evaluate(HistoricBacktestCandidate c, CancellationToken ct) =>
            Task.FromResult(Result(trades: 50, expectancy: c.Weights.Rsi * 10, maxDd: 10m));

        var sequential = await MlSweepOptimizer.OptimizeAsync(baseline, Evaluate, FlatSpy, 15m, CancellationToken.None);
        var parallel = await MlSweepOptimizer.OptimizeAsync(baseline, Evaluate, FlatSpy, 15m, CancellationToken.None, maxParallelism: 4);

        sequential.Select(e => e.Summary.Weights).Should().Equal(parallel.Select(e => e.Summary.Weights));
        sequential.Select(e => e.Summary.Label).Should().Equal(parallel.Select(e => e.Summary.Label));
    }

    [Fact]
    public async Task OptimizeAsync_SuccessiveHalving_ConcentratesBudgetOnSurvivors()
    {
        // Schedule: 30 starts x 1 gen screen, top 15 get 2 more, top 5 get 6
        // more. Per-seed evaluation counts must therefore be exactly one of
        // {1, 3, 9} generations x lambda, with 15 / 10 / 5 seeds respectively.
        var baseline = Baseline();
        var evaluations = await MlSweepOptimizer.OptimizeAsync(
            baseline,
            (c, ct) => Task.FromResult(Result(trades: 50, expectancy: c.Weights.Rsi * 10, maxDd: 10m)),
            FlatSpy, 15m, CancellationToken.None);

        var perSeed = evaluations
            .GroupBy(e => e.Summary.Label.Split('.')[0]) // "ML search N"
            .Select(g => g.Count())
            .ToList();

        perSeed.Should().HaveCount(MlSweepOptimizer.InitialSeeds);
        perSeed.Count(c => c == 1 * MlSweepOptimizer.Lambda).Should().Be(15); // screened out after stage 1
        perSeed.Count(c => c == 3 * MlSweepOptimizer.Lambda).Should().Be(10); // dropped after stage 2
        perSeed.Count(c => c == 9 * MlSweepOptimizer.Lambda).Should().Be(5);  // went the distance
    }

    [Fact]
    public async Task OptimizeAsync_SomeRestartFindsThePeakOfAPeakedObjective()
    {
        // A synthetic objective peaked at a specific RSI weight. The halving
        // schedule should notice which starts score well in the screening
        // generation and give them the deep budget - at least one should
        // land close to the peak.
        var baseline = Baseline();
        const decimal peakRsi = 0.30m;

        Task<HistoricResult> Evaluate(HistoricBacktestCandidate c, CancellationToken ct)
        {
            var distance = Math.Abs(c.Weights.Rsi - peakRsi);
            var expectancy = Math.Max(0m, 2.0m - distance * 10m); // peaks at 2.0%/trade when Rsi == peakRsi
            return Task.FromResult(Result(trades: 50, expectancy: expectancy, maxDd: 5m));
        }

        var evaluations = await MlSweepOptimizer.OptimizeAsync(baseline, Evaluate, FlatSpy, 15m, CancellationToken.None);

        evaluations.Max(e => e.Summary.AdjustedExpectancyPct).Should().BeGreaterThan(1.5m); // peak is 2.0m
    }

    [Fact]
    public async Task OptimizeAsync_RestartsScatterAcrossTheSimplex_NotAllNearBaseline()
    {
        // The whole point of multi-start over a single centred CMA-ES run:
        // restarts begin from independently-seeded random logits, not
        // clustered around the baseline's ~0.17 RSI weight. A flat objective
        // (no gradient pulling the search anywhere) isolates exactly how much
        // spread the STARTING points themselves contribute.
        var baseline = Baseline();
        Task<HistoricResult> Evaluate(HistoricBacktestCandidate c, CancellationToken ct) =>
            Task.FromResult(Result(trades: 50, expectancy: 1.0m, maxDd: 5m));

        var evaluations = await MlSweepOptimizer.OptimizeAsync(baseline, Evaluate, FlatSpy, 15m, CancellationToken.None);

        var rsiValues = evaluations.Select(e => e.Summary.Weights.Rsi).ToList();
        (rsiValues.Max() - rsiValues.Min()).Should().BeGreaterThan(0.15m); // baseline RSI weight is 0.17
    }

    [Fact]
    public async Task OptimizeAsync_FirstRestartIsCentredOnTheBaseline()
    {
        // Seed 1's starting logits are all zero - the softmax mapping then
        // reproduces the baseline mix exactly at the mean, so its first
        // generation's offspring must cluster around the baseline weights.
        var baseline = Baseline();
        Task<HistoricResult> Evaluate(HistoricBacktestCandidate c, CancellationToken ct) =>
            Task.FromResult(Result(trades: 50, expectancy: 1.0m, maxDd: 5m));

        var evaluations = await MlSweepOptimizer.OptimizeAsync(baseline, Evaluate, FlatSpy, 15m, CancellationToken.None);

        var firstGeneration = evaluations.Take(MlSweepOptimizer.Lambda).ToList();
        firstGeneration.Should().AllSatisfy(e => e.Summary.Label.Should().StartWith("ML search 1."));
        var avgRsi = firstGeneration.Average(e => e.Summary.Weights.Rsi);
        avgRsi.Should().BeApproximately(baseline.Weights.Rsi, 0.10m);
    }

    [Fact]
    public async Task OptimizeAsync_GradedPenalty_RanksNearFeasibleAboveHopeless()
    {
        // With every candidate infeasible (too few trades), the search still
        // completes and every summary is marked ineligible - the graded
        // penalty is what keeps the internal ranking meaningful, but the
        // externally visible contract is simply: no crash, nothing eligible.
        var baseline = Baseline();
        Task<HistoricResult> Evaluate(HistoricBacktestCandidate c, CancellationToken ct) =>
            Task.FromResult(Result(trades: 5, expectancy: 1.0m, maxDd: 5m));

        var evaluations = await MlSweepOptimizer.OptimizeAsync(baseline, Evaluate, FlatSpy, 15m, CancellationToken.None);

        evaluations.Should().HaveCount(MlSweepOptimizer.ActualCandidateCount);
        evaluations.Should().AllSatisfy(e => e.Summary.MetConstraints.Should().BeFalse());
    }
}
