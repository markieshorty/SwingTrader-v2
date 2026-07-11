using FluentAssertions;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// The ML search pool: same dial-space contract as SweepOptimizer's
// deterministic sweep (live weights sum to the baseline's live budget, dead
// components never move, Buy threshold stays in range), plus checks specific
// to the multi-start design - that restarts actually scatter across the
// simplex (not all collapsing near the baseline) and that, given a peaked
// objective, at least one of the 30 independent restarts finds it.
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
    // raw return passes through unadjusted.
    private static HistoricResult Result(int trades, decimal expectancy, decimal maxDd)
    {
        var log = Enumerable.Range(0, trades)
            .Select(i => new HistoricTrade(
                "TEST", FlatSpy[0].Date.AddDays(i), FlatSpy[0].Date.AddDays(i + 5),
                100m, 100m * (1 + expectancy / 100m), SetupType.MomentumContinuation, 6.5m, "Target", expectancy))
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

        var summaries = await MlSweepOptimizer.OptimizeAsync(
            baseline,
            (c, ct) => Task.FromResult(Result(trades: 50, expectancy: 1.0m, maxDd: 10m)),
            FlatSpy, baselineMaxDrawdownPct: 15m, CancellationToken.None);

        summaries.Should().HaveCount(MlSweepOptimizer.ActualCandidateCount);
        summaries.Should().AllSatisfy(s =>
        {
            LiveSum(s.Weights).Should().BeApproximately(liveBudget, 0.01m);
            s.Weights.Sentiment.Should().Be(ProdWeights.Sentiment);
            s.Weights.FundamentalMomentum.Should().Be(ProdWeights.FundamentalMomentum);
            s.BuyThreshold.Should().BeInRange(3.0m, 9.0m);
        });
    }

    [Fact]
    public async Task OptimizeAsync_IsDeterministic_SameInputsSameCandidates()
    {
        var baseline = Baseline();
        Task<HistoricResult> Evaluate(HistoricBacktestCandidate c, CancellationToken ct) =>
            Task.FromResult(Result(trades: 50, expectancy: 1.0m, maxDd: 10m));

        var first = await MlSweepOptimizer.OptimizeAsync(baseline, Evaluate, FlatSpy, 15m, CancellationToken.None);
        var second = await MlSweepOptimizer.OptimizeAsync(baseline, Evaluate, FlatSpy, 15m, CancellationToken.None);

        first.Select(s => s.Weights).Should().Equal(second.Select(s => s.Weights));
        first.Select(s => s.BuyThreshold).Should().Equal(second.Select(s => s.BuyThreshold));
    }

    [Fact]
    public async Task OptimizeAsync_SomeRestartFindsThePeakOfAPeakedObjective()
    {
        // A synthetic objective peaked at a specific RSI weight, scored via
        // AdjustedExpectancy-equivalent (SPY is flat, so each trade's raw
        // return passes straight through). With 30 independent restarts x 3
        // generations of local adaptation each, at least one should land
        // close to the peak - the multi-start payoff a single run can't
        // guarantee if its one search happens to concentrate elsewhere.
        var baseline = Baseline();
        const decimal peakRsi = 0.30m;

        Task<HistoricResult> Evaluate(HistoricBacktestCandidate c, CancellationToken ct)
        {
            var distance = Math.Abs(c.Weights.Rsi - peakRsi);
            var expectancy = Math.Max(0m, 2.0m - distance * 10m); // peaks at 2.0%/trade when Rsi == peakRsi
            return Task.FromResult(Result(trades: 50, expectancy: expectancy, maxDd: 5m));
        }

        var summaries = await MlSweepOptimizer.OptimizeAsync(baseline, Evaluate, FlatSpy, 15m, CancellationToken.None);

        summaries.Max(s => s.AdjustedExpectancyPct).Should().BeGreaterThan(1.5m); // peak is 2.0m
    }

    [Fact]
    public async Task OptimizeAsync_RestartsScatterAcrossTheSimplex_NotAllNearBaseline()
    {
        // The whole point of multi-start over a single centred CMA-ES run:
        // restarts 1..29 begin from independently-seeded random points, not
        // clustered around the baseline's ~0.17 RSI weight. A flat objective
        // (no gradient pulling the search anywhere) isolates exactly how much
        // spread the STARTING points themselves contribute.
        var baseline = Baseline();
        Task<HistoricResult> Evaluate(HistoricBacktestCandidate c, CancellationToken ct) =>
            Task.FromResult(Result(trades: 50, expectancy: 1.0m, maxDd: 5m));

        var summaries = await MlSweepOptimizer.OptimizeAsync(baseline, Evaluate, FlatSpy, 15m, CancellationToken.None);

        var rsiValues = summaries.Select(s => s.Weights.Rsi).ToList();
        (rsiValues.Max() - rsiValues.Min()).Should().BeGreaterThan(0.15m); // baseline RSI weight is 0.17
    }

    [Fact]
    public async Task OptimizeAsync_FirstRestartIsCentredOnTheBaseline()
    {
        // Seed 0's starting MEAN is never randomised (unlike seeds 1..29) -
        // guarantees the ML search always covers the exact region the
        // deterministic sweep centres on too. Its evaluated offspring carry
        // Gaussian noise around that mean, so check they cluster close to the
        // baseline on average rather than expecting an exact match.
        var baseline = Baseline();
        Task<HistoricResult> Evaluate(HistoricBacktestCandidate c, CancellationToken ct) =>
            Task.FromResult(Result(trades: 50, expectancy: 1.0m, maxDd: 5m));

        var summaries = await MlSweepOptimizer.OptimizeAsync(baseline, Evaluate, FlatSpy, 15m, CancellationToken.None);

        var firstSeedBlock = summaries.Take(MlSweepOptimizer.BudgetPerSeed).ToList();
        var avgRsi = firstSeedBlock.Average(s => s.Weights.Rsi);
        avgRsi.Should().BeApproximately(baseline.Weights.Rsi, 0.10m);
    }
}
