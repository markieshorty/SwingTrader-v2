using FluentAssertions;
using SwingTrader.Agents.Trials;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// The Trials page's statistics (transparency pivot): banding, the veto floor
// sweep and the tilt counterfactual, on hand-built trades.
public class TrialsMathTests
{
    private static Trade T(decimal entry, decimal exit, decimal? forward = null, decimal? mult = null) => new()
    {
        EntryPrice = entry,
        ExitPrice = exit,
        Quantity = 1m,
        ForwardScoreAtEntry = forward,
        SizeMultiplier = mult,
        Status = TradeStatus.Closed,
    };

    [Fact]
    public void ForwardScoreBands_BucketAndAverageCorrectly()
    {
        var trades = new[]
        {
            T(100, 110, forward: 7.5m),  // +10% in 7+
            T(100, 104, forward: 7.0m),  // +4%  in 7+
            T(100, 95,  forward: 5.5m),  // -5%  in 5-6
            T(100, 102),                 // unscored
        };

        var bands = TrialsMath.ForwardScoreBands(trades);

        bands.Single(b => b.Label == "7 +").Trades.Should().Be(2);
        bands.Single(b => b.Label == "7 +").AvgReturnPct.Should().Be(7.00m);
        bands.Single(b => b.Label == "7 +").WinRatePct.Should().Be(100.0m);
        bands.Single(b => b.Label == "5 – 6").AvgReturnPct.Should().Be(-5.00m);
        bands.Single(b => b.Label == "unscored").Trades.Should().Be(1);
    }

    [Fact]
    public void VetoFloorSweep_SplitsSkippedAndKeptAtEachFloor()
    {
        var trades = new[]
        {
            T(100, 90, forward: 3m),   // -10%, below every floor >= 4
            T(100, 105, forward: 5m),
            T(100, 110, forward: 7m),
        };

        var sweep = TrialsMath.VetoFloorSweep(trades);
        var floor6 = sweep.Single(r => r.Floor == 6m);

        floor6.Skipped.Should().Be(2);
        floor6.SkippedAvgPct.Should().Be(-2.50m); // (-10 + 5) / 2
        floor6.Kept.Should().Be(1);
        floor6.KeptAvgPct.Should().Be(10.00m);
    }

    [Fact]
    public void SizingTilt_TiltWeightedDivergesWhenUpsizedTradesWin()
    {
        var trades = new[]
        {
            T(100, 110, forward: 8m, mult: 1.4m),  // upsized winner +10%
            T(100, 96,  forward: 4m, mult: 0.7m),  // downsized loser -4%
        };

        var tilt = TrialsMath.SizingTilt(trades);

        tilt.TiltedTrades.Should().Be(2);
        tilt.EqualWeightedAvgPct.Should().Be(3.00m);            // (10 - 4) / 2
        // (10*1.4 + -4*0.7) / 2.1 = 11.2 / 2.1 = 5.33
        tilt.TiltWeightedAvgPct.Should().Be(5.33m);
        tilt.Bands.Single(b => b.Label.StartsWith("sized up")).AvgReturnPct.Should().Be(10.00m);
    }

    [Theory]
    [InlineData(10, 100, "far too early — do not act on this")]
    [InlineData(55, 100, "suggestive — half the required evidence")]
    [InlineData(100, 100, "decisive-n reached")]
    public void Grade_IsBlunt(int n, int target, string expected) =>
        TrialsMath.Grade(n, target).Should().Be(expected);
}
