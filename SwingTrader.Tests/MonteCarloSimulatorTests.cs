using FluentAssertions;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Core.Enums;
using Xunit;

namespace SwingTrader.Tests;

// The trade-order bootstrap: deterministic (fixed seed), percentile ordering
// sane, degenerate inputs handled honestly. It measures SEQUENCE risk on the
// run's own trades - never inventing new ones.
public class MonteCarloSimulatorTests
{
    private static HistoricResult ResultWith(params decimal[] returns)
    {
        var d = new DateTime(2024, 1, 1);
        var log = returns.Select(r =>
            new HistoricTrade("X", d, d.AddDays(5), 100m, 100m + r, SetupType.MomentumContinuation, 6.5m, "Target", r)).ToList();
        return new HistoricResult(d, d.AddYears(2), log.Count, 0.5m, 5m, -5m, log.Average(t => t.ReturnPct),
            1.2m, 40m, 12m, 30m, [], [], [], log, CalmarRatio: 1.5m);
    }

    [Fact]
    public void Run_IsDeterministic_SameInputsSameAnswer()
    {
        var result = ResultWith(5m, -3m, 8m, -2m, 4m, -6m, 7m, 1m, -1m, 3m, 2m, -4m);

        var a = MonteCarloSimulator.Run(result, 0.15m, resamples: 500);
        var b = MonteCarloSimulator.Run(result, 0.15m, resamples: 500);

        a.Should().Be(b); // records: full value equality
    }

    [Fact]
    public void Run_PercentilesAreOrdered_AndCarryActuals()
    {
        var result = ResultWith(5m, -3m, 8m, -2m, 4m, -6m, 7m, 1m, -1m, 3m, 2m, -4m);

        var mc = MonteCarloSimulator.Run(result, 0.15m, resamples: 1000);

        mc.P5TotalReturnPct.Should().BeLessThanOrEqualTo(mc.MedianTotalReturnPct);
        mc.MedianTotalReturnPct.Should().BeLessThanOrEqualTo(mc.P95TotalReturnPct);
        mc.MedianMaxDrawdownPct.Should().BeLessThanOrEqualTo(mc.P95MaxDrawdownPct);
        mc.ActualTotalReturnPct.Should().Be(40m);
        mc.ActualCalmarRatio.Should().Be(1.5m);
        mc.Trades.Should().Be(12);
    }

    [Fact]
    public void Run_AllWinningTrades_ZeroProbabilityOfLoss_RobustVerdict()
    {
        var result = ResultWith(3m, 5m, 2m, 8m, 4m, 6m, 1m, 7m, 2m, 3m);

        var mc = MonteCarloSimulator.Run(result, 0.15m, resamples: 500);

        mc.ProbabilityOfLossPct.Should().Be(0m);
        mc.P5TotalReturnPct.Should().BeGreaterThan(0m);
        mc.Verdict.Should().StartWith("Robust");
    }

    [Fact]
    public void Run_AllLosingTrades_CertainLoss_FragileVerdict()
    {
        var result = ResultWith(-3m, -5m, -2m, -8m, -4m, -6m, -1m, -7m, -2m, -3m);

        var mc = MonteCarloSimulator.Run(result, 0.15m, resamples: 500);

        mc.ProbabilityOfLossPct.Should().Be(100m);
        mc.Verdict.Should().StartWith("Sequence-fragile");
    }

    [Fact]
    public void Run_TooFewTrades_SaysSoInsteadOfPretending()
    {
        var result = ResultWith(5m, -3m, 8m);

        var mc = MonteCarloSimulator.Run(result, 0.15m);

        mc.Resamples.Should().Be(0);
        mc.Verdict.Should().Contain("Not enough trades");
    }
}
