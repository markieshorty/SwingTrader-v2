using FluentAssertions;
using SwingTrader.Core.Enums;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.Market;
using Xunit;

namespace SwingTrader.Tests;

// The pure support/resistance algorithm both the live service and the
// historic backtester call: swing-point detection, clustering, the 3-part
// breakout condition, proximity scoring, context priority, and the
// insufficient-data fallback.
public class PriceLevelCalculatorTests
{
    private static readonly PriceLevelConfig Cfg = new(); // production defaults

    // Flat bar: high/low hug the close, volume 1M.
    private static PriceBar Flat(decimal price, decimal volume = 1_000_000m) =>
        new(price + 0.5m, price - 0.5m, price, volume);

    // A series of flat bars at `basePrice` with a single swing high of
    // `peak` planted at `peakIndex` (2+ bars from each edge -> detectable).
    private static List<PriceBar> WithSwingHigh(int count, decimal basePrice, decimal peak, int peakIndex)
    {
        var bars = Enumerable.Range(0, count).Select(_ => Flat(basePrice)).ToList();
        bars[peakIndex] = new PriceBar(peak, basePrice - 0.5m, basePrice, 1_000_000m);
        return bars;
    }

    [Fact]
    public void Compute_FewerThanMinCandles_InsufficientData_Neutral()
    {
        var bars = Enumerable.Range(0, Cfg.MinCandles - 1).Select(_ => Flat(100m)).ToList();

        var r = PriceLevelCalculator.Compute(bars, 100m, Cfg);

        r.Context.Should().Be(PriceLevelContext.InsufficientData);
        r.Score.Should().Be(0.5m);
    }

    [Fact]
    public void SwingHigh_BecomesResistance_OnlyWhenHigherThanTwoBarsEachSide()
    {
        // Peak at index 10 -> detected as resistance above the current price.
        var detected = PriceLevelCalculator.Compute(WithSwingHigh(30, 100m, 120m, 10), 100m, Cfg);
        detected.NearestResistance.Should().Be(120m);

        // Same peak on the LAST bar -> no 2 bars after it -> not a swing
        // point, so no resistance exists (price sits above all history).
        var atEdge = PriceLevelCalculator.Compute(WithSwingHigh(30, 100m, 120m, 29), 100m, Cfg);
        atEdge.NearestResistance.Should().BeNull();
    }

    [Fact]
    public void SwingLow_NearSupport_Scores085()
    {
        // Swing low at 99 with price 1% above it (within the 2% proximity).
        var bars = Enumerable.Range(0, 30).Select(_ => Flat(102m)).ToList();
        bars[10] = new PriceBar(102.5m, 99m, 102m, 1_000_000m);

        var r = PriceLevelCalculator.Compute(bars, 100m, Cfg);

        r.Context.Should().Be(PriceLevelContext.NearSupport);
        r.NearestSupport.Should().Be(99m);
        r.Score.Should().Be(0.85m);
    }

    [Fact]
    public void NearResistance_WithinProximity_Scores015()
    {
        // Resistance at 101.5, price 100 -> 1.5% away (within 2%), and keep
        // yesterday's close AT the current price so no breakout fires.
        var r = PriceLevelCalculator.Compute(WithSwingHigh(30, 100m, 101.5m, 10), 100m, Cfg);

        r.Context.Should().Be(PriceLevelContext.NearResistance);
        r.Score.Should().Be(0.15m);
    }

    [Fact]
    public void Breakout_RequiresAllThreeConditions()
    {
        // Resistance at 105; yesterday closed below it; price now above.
        List<PriceBar> Bars(decimal lastVolume)
        {
            var bars = WithSwingHigh(30, 100m, 105m, 10);
            bars[^1] = new PriceBar(106.5m, 100m, 106m, lastVolume);
            return bars;
        }

        // Volume 2x the 20-bar average -> breakout, score 1.0.
        var confirmed = PriceLevelCalculator.Compute(Bars(2_000_000m), 106m, Cfg);
        confirmed.Context.Should().Be(PriceLevelContext.JustBrokeResistance);
        confirmed.Score.Should().Be(1.0m);

        // Same move on average volume (ratio < 1.3) -> NOT a breakout.
        var unconfirmed = PriceLevelCalculator.Compute(Bars(1_000_000m), 106m, Cfg);
        unconfirmed.Context.Should().NotBe(PriceLevelContext.JustBrokeResistance);

        // Volume fine but yesterday already closed ABOVE the level -> no cross.
        var bars = WithSwingHigh(30, 100m, 105m, 10);
        bars[^2] = new PriceBar(106.5m, 105.5m, 106m, 1_000_000m);
        bars[^1] = new PriceBar(106.5m, 105.5m, 106m, 2_000_000m);
        var noCross = PriceLevelCalculator.Compute(bars, 106m, Cfg);
        noCross.Context.Should().NotBe(PriceLevelContext.JustBrokeResistance);
    }

    [Fact]
    public void Clustering_MergesLevelsWithinClusterPct()
    {
        // Two swing highs 1% apart (within the 1.5% cluster) -> one level,
        // the higher one (descending sort keeps the first of each cluster).
        var bars = WithSwingHigh(40, 100m, 121.2m, 10);
        bars[20] = new PriceBar(120m, 99.5m, 100m, 1_000_000m);

        var r = PriceLevelCalculator.Compute(bars, 100m, Cfg);

        r.NearestResistance.Should().Be(121.2m);
    }

    [Fact]
    public void AboveAllResistance_AtNewHigh_Scores060()
    {
        // Swing high at 105 but price is 110 -> nothing above -> new high.
        var r = PriceLevelCalculator.Compute(WithSwingHigh(30, 100m, 105m, 10), 110m, Cfg);

        r.Context.Should().Be(PriceLevelContext.AtNewHigh);
        r.Score.Should().Be(0.60m);
    }

    [Fact]
    public void BetweenLevels_Scores050()
    {
        // Support at 90, resistance at 110, price 100 -> >2% from both.
        var bars = Enumerable.Range(0, 40).Select(_ => Flat(100m)).ToList();
        bars[10] = new PriceBar(110m, 99.5m, 100m, 1_000_000m);
        bars[20] = new PriceBar(100.5m, 90m, 100m, 1_000_000m);

        var r = PriceLevelCalculator.Compute(bars, 100m, Cfg);

        r.Context.Should().Be(PriceLevelContext.BetweenLevels);
        r.NearestSupport.Should().Be(90m);
        r.NearestResistance.Should().Be(110m);
        r.Score.Should().Be(0.50m);
    }
}
