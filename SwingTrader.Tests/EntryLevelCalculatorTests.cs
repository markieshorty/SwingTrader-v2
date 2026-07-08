using FluentAssertions;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Trading;
using Xunit;

namespace SwingTrader.Tests;

// Shared by ReportGenerationService and ExecutionService - the same
// percentage table must produce identical distances regardless of which
// price snapshot it's applied to (Report's ~6:30 ET quote vs. Execution's
// own live quote right before placing the order).
public class EntryLevelCalculatorTests
{
    [Theory]
    [InlineData(SetupType.Breakout, 0.940)]
    [InlineData(SetupType.VolumeSpike, 0.960)]
    [InlineData(SetupType.TrendFollowing, 0.950)]
    public void Calculate_StopLossDistance_MatchesSetupType(SetupType setupType, double expectedFraction)
    {
        var (stopLoss, _) = EntryLevelCalculator.Calculate(setupType, convictionScore: 5m, price: 100m);

        stopLoss.Should().Be(Math.Round(100m * (decimal)expectedFraction, 2));
    }

    [Theory]
    [InlineData(9.5, 1.120)]
    [InlineData(9.0, 1.120)]
    [InlineData(8.5, 1.100)]
    [InlineData(8.0, 1.100)]
    [InlineData(5.0, 1.080)]
    public void Calculate_TargetDistance_MatchesConvictionTier(double convictionScore, double expectedFraction)
    {
        var (_, target) = EntryLevelCalculator.Calculate(SetupType.TrendFollowing, (decimal)convictionScore, price: 100m);

        target.Should().Be(Math.Round(100m * (decimal)expectedFraction, 2));
    }

    [Fact]
    public void Calculate_SamePercentages_ScaleToWhicheverPriceIsPassedIn()
    {
        // The whole point of extracting this - Report and Execution each
        // supply their own live price, and must get the same distance.
        var (reportStop, reportTarget) = EntryLevelCalculator.Calculate(SetupType.Breakout, 9.0m, price: 50m);
        var (executionStop, executionTarget) = EntryLevelCalculator.Calculate(SetupType.Breakout, 9.0m, price: 55m);

        (reportStop / 50m).Should().Be(executionStop / 55m);
        (reportTarget / 50m).Should().Be(executionTarget / 55m);
    }
}
