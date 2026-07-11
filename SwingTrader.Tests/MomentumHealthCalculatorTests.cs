using FluentAssertions;
using SwingTrader.Agents.Monitor;
using Xunit;

namespace SwingTrader.Tests;

// The pure probation algorithm shared by the live monitor and the historic
// backtester - component bands, verdict thresholds, and the neutral-on-
// missing-data rule. Weights: RSI 0.30, volume 0.25, price 0.25, RS 0.20.
public class MomentumHealthCalculatorTests
{
    private const decimal Threshold = 0.5m;

    [Fact]
    public void AllPositive_ScoresOne_Confirmed()
    {
        // RSI rising above 50, volume ≥0.8, price ≥+1.5%, RS >+0.5.
        var o = MomentumHealthCalculator.Compute(
            rsiToday: 60m, rsiAtEntry: 50m, volumeRatio: 1.0m,
            currentPrice: 102m, entryPrice: 100m, relativeReturn: 1.0m, Threshold);

        o.Score.Should().Be(1.00m);
        o.Verdict.Should().Be("Confirmed");
    }

    [Fact]
    public void AllNegative_ScoresZero_Exit()
    {
        var o = MomentumHealthCalculator.Compute(
            rsiToday: 40m, rsiAtEntry: 50m, volumeRatio: 0.3m,
            currentPrice: 97m, entryPrice: 100m, relativeReturn: -2m, Threshold);

        o.Score.Should().Be(0.00m);
        o.Verdict.Should().Be("Exit");
    }

    [Fact]
    public void AllMissing_ScoresNeutral_Borderline()
    {
        // Never exit on missing data: nulls everywhere = 0.5 = Borderline.
        var o = MomentumHealthCalculator.Compute(
            null, null, null, currentPrice: 0m, entryPrice: 100m, null, Threshold);

        o.Score.Should().Be(0.50m);
        o.Verdict.Should().Be("Borderline");
    }

    // Verdict bands around threshold 0.5: >= 0.75 Confirmed, >= 0.5
    // Borderline, else Exit. Component scores only land on multiples of the
    // band values, so each case states its exact achievable score.
    [Theory]
    // RSI rising ≥50 (0.30) + volume ≥0.8 (0.25) + price 0..1.5% (0.125) + RS mid (0.10) = 0.775
    [InlineData(60, 50, 1.0, 100.5, 0, 0.775, "Confirmed")]
    // RSI rising <50 (0.15) + volume ≥0.8 (0.25) + price 0..1.5% (0.125) + RS mid (0.10) = 0.625
    [InlineData(45, 40, 1.0, 100.5, 0, 0.625, "Borderline")]
    // RSI falling (0) + volume mid (0.125) + price ≥1.5% (0.25) + RS strong (0.20) = 0.575
    [InlineData(40, 50, 0.6, 102, 1, 0.575, "Borderline")]
    // RSI falling (0) + volume mid (0.125) + price 0..1.5% (0.125) + RS mid (0.10) = 0.35
    [InlineData(40, 50, 0.6, 100.5, 0, 0.35, "Exit")]
    public void VerdictBands_AroundThreshold(
        double rsi, double rsiEntry, double vol, double price, double rel, double expectedScore, string expected)
    {
        var o = MomentumHealthCalculator.Compute(
            (decimal)rsi, (decimal)rsiEntry, (decimal)vol, (decimal)price, entryPrice: 100m, (decimal)rel, Threshold);

        o.Score.Should().Be((decimal)expectedScore);
        o.Verdict.Should().Be(expected);
    }
}
