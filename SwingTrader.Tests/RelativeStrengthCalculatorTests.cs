using FluentAssertions;
using SwingTrader.Infrastructure.Market;
using Xunit;

namespace SwingTrader.Tests;

// The pure RS algorithm both the live service and the historic backtester
// call - band boundaries, lerp interiors, insufficient-data null, and the
// 5-day window taking the LAST five closes of a longer series.
public class RelativeStrengthCalculatorTests
{
    // Builds a 5-close series ending with the given % return over the window.
    private static List<decimal> Series(decimal returnPct)
    {
        var start = 100m;
        var end = start * (1 + returnPct / 100m);
        return [start, start, start, start, end];
    }

    [Theory]
    // Band edges (exact values, not approximations - the bands are the spec).
    [InlineData(3.0, 1.00)]     // >= +3% caps at 1.0
    [InlineData(5.0, 1.00)]
    [InlineData(1.0, 0.80)]     // +1% -> bottom of the 0.8-1.0 band
    [InlineData(0.0, 0.60)]     // 0% -> bottom of the 0.6-0.8 band
    [InlineData(-1.0, 0.40)]    // -1% -> bottom of the 0.4-0.6 band
    [InlineData(-3.0, 0.20)]    // -3% -> bottom of the 0.2-0.4 band
    [InlineData(-4.0, 0.00)]    // < -3% floors at 0
    // Lerp midpoints inside each band.
    [InlineData(2.0, 0.90)]     // halfway +1..+3 -> halfway 0.8..1.0
    [InlineData(0.5, 0.70)]
    [InlineData(-0.5, 0.50)]
    [InlineData(-2.0, 0.30)]
    public void ScoreRelativeReturn_MatchesBandSpec(double rel, double expected)
    {
        RelativeStrengthCalculator.ScoreRelativeReturn((decimal)rel).Should().Be((decimal)expected);
    }

    [Fact]
    public void Compute_ReturnsAndScore_FromFiveDayWindow()
    {
        // Stock +4%, ETF +1% -> relative +3% -> score 1.0.
        var outcome = RelativeStrengthCalculator.Compute(Series(4m), Series(1m));

        outcome.Should().NotBeNull();
        outcome!.StockReturn5d.Should().Be(4m);
        outcome.EtfReturn5d.Should().Be(1m);
        outcome.RelativeReturn.Should().Be(3m);
        outcome.Score.Should().Be(1.00m);
    }

    [Fact]
    public void Compute_UsesOnlyTheLastFiveCloses()
    {
        // Leading closes are garbage; only the final five matter. The stock's
        // 5-day window is flat (0%) even though the whole series doubled.
        var stock = new List<decimal> { 50m, 60m, 70m, 100m, 100m, 100m, 100m, 100m };
        var etf = Series(0m);

        var outcome = RelativeStrengthCalculator.Compute(stock, etf);

        outcome!.StockReturn5d.Should().Be(0m);
        outcome.RelativeReturn.Should().Be(0m);
        outcome.Score.Should().Be(0.60m);
    }

    [Fact]
    public void Compute_FewerThanFiveCloses_ReturnsNull()
    {
        var four = new List<decimal> { 100m, 101m, 102m, 103m };

        RelativeStrengthCalculator.Compute(four, Series(0m)).Should().BeNull();
        RelativeStrengthCalculator.Compute(Series(0m), four).Should().BeNull();
    }

    [Fact]
    public void Compute_ZeroStartingClose_ReturnsNull_NotDivideByZero()
    {
        var zeroStart = new List<decimal> { 0m, 100m, 100m, 100m, 100m };

        RelativeStrengthCalculator.Compute(zeroStart, Series(0m)).Should().BeNull();
        RelativeStrengthCalculator.Compute(Series(0m), zeroStart).Should().BeNull();
    }
}
