using FluentAssertions;
using SwingTrader.Agents.Scorecard;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

public class ForwardPathStatsTests
{
    // Flat bars from a fixed base, so any move under test is the only move.
    private static List<HistoricalCandle> Flat(int count, decimal price = 100m) =>
        Enumerable.Range(0, count).Select(i => new HistoricalCandle
        {
            Symbol = "TEST",
            Date = new DateOnly(2026, 1, 1).AddDays(i),
            Open = price, High = price, Low = price, Close = price, Volume = 1_000_000,
        }).ToList();

    [Fact]
    public void Compute_EntersOnFirstBarStrictlyAfterSignal()
    {
        var bars = Flat(50);
        // Signal on bar 0's date; entry must be bar 1, matching
        // CounterfactualReplay so the two are comparable on the same signal.
        var s = ForwardPathStats.Compute(bars, bars[0].Date);

        s.Should().NotBeNull();
        s!.EntryDate.Should().Be(bars[1].Date);
    }

    [Fact]
    public void Compute_NoBarAfterSignal_ReturnsNull()
    {
        var bars = Flat(3);
        ForwardPathStats.Compute(bars, bars[^1].Date).Should().BeNull();
    }

    [Fact]
    public void Compute_ZeroOpen_ReturnsNull()
    {
        var bars = Flat(50);
        bars[1].Open = 0m;
        ForwardPathStats.Compute(bars, bars[0].Date).Should().BeNull();
    }

    // The horizon must be complete or the statistic is null. A partial window
    // understates both tails and biases everything built on it toward the
    // middle, which is exactly the kind of quiet distortion the calibration
    // cannot survive.
    [Fact]
    public void Compute_ShortWindow_LeavesHorizonStatsNull()
    {
        var bars = Flat(25); // entry at index 1 leaves 23 forward bars, < 40
        var s = ForwardPathStats.Compute(bars, bars[0].Date)!;

        s.Fwd5Pct.Should().NotBeNull();
        s.Fwd20Pct.Should().NotBeNull();
        s.Fwd40Pct.Should().BeNull();
        s.MaxFavorablePct.Should().BeNull();
        s.HitPlus25Within40.Should().BeNull();
        s.HitMinus25Within40.Should().BeNull();
    }

    [Fact]
    public void Compute_ForwardReturnsMeasuredFromEntryOpen()
    {
        var bars = Flat(60);
        bars[6].Close = 110m;  // entry index 1, so +5 bars is index 6
        bars[21].Close = 120m; // +20
        bars[41].Close = 130m; // +40

        var s = ForwardPathStats.Compute(bars, bars[0].Date)!;

        s.EntryPrice.Should().Be(100m);
        s.Fwd5Pct.Should().Be(10m);
        s.Fwd20Pct.Should().Be(20m);
        s.Fwd40Pct.Should().Be(30m);
    }

    // The right-tail event is an intraday touch, not a close: a target order
    // fills when the high reaches it. A bar that spikes to +30% and closes flat
    // still counts.
    [Fact]
    public void Compute_RightTailUsesIntradayHigh_NotClose()
    {
        var bars = Flat(60);
        bars[10].High = 130m; // touches +30%, closes back at 100

        var s = ForwardPathStats.Compute(bars, bars[0].Date)!;

        s.MaxFavorablePct.Should().Be(30m);
        s.HitPlus25Within40.Should().BeTrue();
        s.Fwd40Pct.Should().Be(0m); // the close-based return sees none of it
    }

    [Fact]
    public void Compute_AdverseTailUsesIntradayLow()
    {
        var bars = Flat(60);
        bars[10].Low = 70m;

        var s = ForwardPathStats.Compute(bars, bars[0].Date)!;

        s.MaxAdversePct.Should().Be(-30m);
        s.HitMinus25Within40.Should().BeTrue();
    }

    [Fact]
    public void Compute_MoveOutsideHorizon_DoesNotCount()
    {
        var bars = Flat(80);
        bars[45].High = 200m; // entry idx 1 + 40 = bar 41; this is past it

        var s = ForwardPathStats.Compute(bars, bars[0].Date)!;

        s.HitPlus25Within40.Should().BeFalse();
        s.MaxFavorablePct.Should().Be(0m);
    }

    [Fact]
    public void Compute_ExactlyAtThreshold_Counts()
    {
        var bars = Flat(60);
        bars[10].High = 125m;

        ForwardPathStats.Compute(bars, bars[0].Date)!.HitPlus25Within40.Should().BeTrue();
    }

    // Gross of costs by design: these are path facts, not tradeable results.
    // CounterfactualReplay owns the 0.25%/side round trip.
    [Fact]
    public void Compute_IsGrossOfCosts()
    {
        var bars = Flat(60);
        bars[41].Close = 110m;

        ForwardPathStats.Compute(bars, bars[0].Date)!.Fwd40Pct.Should().Be(10m);
    }
}
