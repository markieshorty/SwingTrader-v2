using FluentAssertions;
using SwingTrader.Agents.Research;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Services;
using Xunit;

namespace SwingTrader.Tests;

public class GradedSetupDetectorTests
{
    private static List<StockCandle> Bars(int count, decimal price = 100m, decimal range = 0m)
    {
        var list = new List<StockCandle>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(new StockCandle
            {
                Symbol = "TEST",
                Timestamp = new DateTime(2026, 1, 1).AddDays(i),
                Open = price, Close = price,
                High = price + range / 2m, Low = price - range / 2m,
                Volume = 1_000_000,
            });
        }
        return list;
    }

    private static IndicatorResult Ind(
        decimal? rsi = null, decimal? lower = null, decimal? upper = null,
        decimal? macd = null, decimal? vol = null, decimal? ema9 = null, decimal? ema21 = null) =>
        new(Rsi14: rsi, Macd: null, MacdSignal: null, MacdHistogram: macd,
            BollingerUpper: upper, BollingerLower: lower, BollingerMid: null,
            Ema9: ema9, Ema21: ema21, VolumeRatio: vol);

    // The headline change: membership is graded, so a marginal qualifier and a
    // textbook one are no longer the same signal.
    [Fact]
    public void Oversold_DeeperDipScoresHigherThanMarginalOne()
    {
        var bars = Bars(30, 100m);
        bars[^1].Close = 100m;
        bars[^5].Close = 96m; // 4-bar recovery satisfied

        var marginal = GradedSetupDetector.Detect(
            Ind(rsi: 34.9m, lower: 99m), bars, SetupDialsV2.Legacy);
        var textbook = GradedSetupDetector.Detect(
            Ind(rsi: 28m, lower: 96m), bars, SetupDialsV2.Legacy);

        textbook.Memberships[SetupType.OversoldRecovery]
            .Should().BeGreaterThan(marginal.Memberships[SetupType.OversoldRecovery]);
    }

    // The guard the legacy detector never had: it lived only in ScoreRsi, so
    // detection produced falling knives for the scorer to mark down afterwards.
    [Fact]
    public void Oversold_BelowRsiFloorIsAFallingKnife_NotADip()
    {
        var bars = Bars(30, 100m);
        bars[^5].Close = 96m;

        GradedSetupDetector.Detect(Ind(rsi: 12m, lower: 96m), bars, SetupDialsV2.Legacy)
            .Memberships.Should().NotContainKey(SetupType.OversoldRecovery);
    }

    // SPEC D6: the retired "loose" variant is this setup with its guard off.
    [Fact]
    public void Oversold_LooseDialsAcceptAnUnconfirmedDip()
    {
        var bars = Bars(30, 100m);
        bars[^5].Close = 108m; // still falling - no recovery

        var confirmed = GradedSetupDetector.Detect(
            Ind(rsi: 30m, lower: 96m), bars, SetupDialsV2.Legacy);
        var loose = GradedSetupDetector.Detect(
            Ind(rsi: 30m, lower: 96m), bars, SetupDialsV2.LooseOversold);

        confirmed.Memberships.Should().NotContainKey(SetupType.OversoldRecovery);
        loose.Memberships.Should().ContainKey(SetupType.OversoldRecovery);
    }

    // No first-match-wins: the legacy detector returned ONE setup by list order,
    // so a name qualifying for several silently lost all but the earliest.
    [Fact]
    public void Detect_ReportsEverySetupANameQualifiesFor()
    {
        var bars = Bars(30, 100m, range: 1m);
        bars[^2].Close = 96m; // +4.2% day, feeding VolumeSpike

        var result = GradedSetupDetector.Detect(
            Ind(upper: 97m, macd: 0.5m, vol: 3.0m), bars, SetupDialsV2.Legacy);

        result.Memberships.Should().ContainKeys(SetupType.Breakout, SetupType.VolumeSpike);
    }

    // SPEC D5: it had no trigger, so it re-fired daily on the same ongoing fact.
    [Fact]
    public void Detect_NeverReturnsTrendFollowing_ItIsContextNow()
    {
        var bars = Bars(30, 100m);
        var result = GradedSetupDetector.Detect(
            Ind(rsi: 60m, ema9: 105m, ema21: 100m, macd: 0.4m, vol: 1.5m),
            bars, SetupDialsV2.Legacy);

        result.Memberships.Should().NotContainKey(SetupType.TrendFollowing);
        result.TrendStrength.Should().BeGreaterThan(0.5m, "an uptrend should read as one");
    }

    [Fact]
    public void TrendStrength_IsNeutralWhenUnknowable()
    {
        GradedSetupDetector.Detect(Ind(), Bars(30), SetupDialsV2.Legacy)
            .TrendStrength.Should().Be(0.5m);
    }

    // The missing Breakout quality measure: a break from a tight coil and a
    // break from noise scored identically before, which is plausibly why
    // Breakout carries the highest gate score and backtests as the drag.
    [Fact]
    public void Breakout_TightCoilBeatsNoisyRange()
    {
        var tight = Bars(30, 100m, range: 1m);
        var noisy = Bars(30, 100m, range: 40m);
        foreach (var b in new[] { tight, noisy }) b[^1].Close = 105m;

        var ind = Ind(upper: 100m, macd: 0.5m, vol: 2.5m);
        var tightM = GradedSetupDetector.Detect(ind, tight, SetupDialsV2.Legacy)
            .Memberships[SetupType.Breakout];
        var noisyM = GradedSetupDetector.Detect(ind, noisy, SetupDialsV2.Legacy)
            .Memberships.GetValueOrDefault(SetupType.Breakout);

        tightM.Should().BeGreaterThan(noisyM);
    }

    // Membership is the MINIMUM of the conditions - a setup is only as strong as
    // its weakest leg. A mean would let one badly-failed condition hide behind
    // several good ones, which is exactly the falling-knife case.
    [Fact]
    public void Membership_IsLimitedByItsWeakestCondition()
    {
        var bars = Bars(30, 100m, range: 1m);
        bars[^1].Close = 105m;

        // Everything strong except volume, which is barely at its floor.
        var result = GradedSetupDetector.Detect(
            Ind(upper: 100m, macd: 0.5m, vol: 1.5m), bars, SetupDialsV2.Legacy);

        result.Memberships.GetValueOrDefault(SetupType.Breakout)
            .Should().BeLessThan(0.2m + 0.0001m, "weak volume caps the whole setup");
    }

    [Fact]
    public void Detect_EmptyOrZeroPricedBars_YieldNothing()
    {
        GradedSetupDetector.Detect(Ind(rsi: 30m), [], SetupDialsV2.Legacy)
            .Memberships.Should().BeEmpty();

        var zero = Bars(5, 100m);
        zero[^1].Close = 0m;
        GradedSetupDetector.Detect(Ind(rsi: 30m), zero, SetupDialsV2.Legacy)
            .Memberships.Should().BeEmpty();
    }

    [Fact]
    public void Best_ReturnsTheStrongestSetup()
    {
        var bars = Bars(30, 100m, range: 1m);
        bars[^2].Close = 96m;

        var result = GradedSetupDetector.Detect(
            Ind(upper: 97m, macd: 0.5m, vol: 4.0m), bars, SetupDialsV2.Legacy);
        var (setup, membership) = result.Best();

        membership.Should().Be(result.Memberships.Values.Max());
        result.Memberships[setup].Should().Be(membership);
    }
}
