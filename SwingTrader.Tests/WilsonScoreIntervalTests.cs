using FluentAssertions;
using SwingTrader.Agents.Readiness;
using Xunit;

namespace SwingTrader.Tests;

// Backs the Readiness page's win-rate confidence interval (recently fixed
// to actually display as a percentage) - a normal approximation would
// misbehave at small sample sizes / extreme win rates, which is exactly
// the regime this app operates in early on.
public class WilsonScoreIntervalTests
{
    [Fact]
    public void Calculate_ZeroTrades_ReturnsFullUncertaintyRange()
    {
        var (low, high) = WilsonScoreInterval.Calculate(wins: 0, total: 0, confidence: 0.95m);

        low.Should().Be(0m);
        high.Should().Be(1m);
    }

    [Fact]
    public void Calculate_BoundsAreAlwaysWithinZeroToOne()
    {
        var (low, high) = WilsonScoreInterval.Calculate(wins: 0, total: 5, confidence: 0.95m);

        low.Should().BeGreaterThanOrEqualTo(0m);
        high.Should().BeLessThanOrEqualTo(1m);
    }

    [Fact]
    public void Calculate_AllWins_UpperBoundNeverExceedsOne()
    {
        var (low, high) = WilsonScoreInterval.Calculate(wins: 5, total: 5, confidence: 0.95m);

        high.Should().BeLessThanOrEqualTo(1m);
        low.Should().BeGreaterThan(0m); // Wilson interval doesn't collapse to a point even at p=1.
    }

    [Fact]
    public void Calculate_ObservedRateAlwaysFallsWithinTheInterval()
    {
        var (low, high) = WilsonScoreInterval.Calculate(wins: 6, total: 10, confidence: 0.95m);
        var observed = 0.6m;

        observed.Should().BeGreaterThanOrEqualTo(low);
        observed.Should().BeLessThanOrEqualTo(high);
    }

    [Fact]
    public void Calculate_LargerSampleSize_ProducesNarrowerInterval()
    {
        // Same 60% observed win rate, but more trades should tighten the
        // confidence interval - more data, more certainty.
        var (lowSmall, highSmall) = WilsonScoreInterval.Calculate(wins: 6, total: 10, confidence: 0.95m);
        var (lowLarge, highLarge) = WilsonScoreInterval.Calculate(wins: 60, total: 100, confidence: 0.95m);

        (highLarge - lowLarge).Should().BeLessThan(highSmall - lowSmall);
    }

    [Fact]
    public void Calculate_HigherConfidenceLevel_ProducesWiderInterval()
    {
        var (low90, high90) = WilsonScoreInterval.Calculate(wins: 6, total: 10, confidence: 0.90m);
        var (low99, high99) = WilsonScoreInterval.Calculate(wins: 6, total: 10, confidence: 0.99m);

        (high99 - low99).Should().BeGreaterThan(high90 - low90);
    }

    [Fact]
    public void Calculate_UnrecognisedConfidenceLevel_FallsBackTo90PercentZScore()
    {
        var (low90, high90) = WilsonScoreInterval.Calculate(wins: 6, total: 10, confidence: 0.90m);
        var (lowUnknown, highUnknown) = WilsonScoreInterval.Calculate(wins: 6, total: 10, confidence: 0.5m);

        lowUnknown.Should().Be(low90);
        highUnknown.Should().Be(high90);
    }
}
