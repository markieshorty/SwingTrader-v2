using FluentAssertions;
using SwingTrader.Agents.Execution;
using SwingTrader.Core.Constants;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Core.Trading;
using Xunit;

namespace SwingTrader.Tests;

public class AtrCalculatorTests
{
    private sealed record Bar(decimal High, decimal Low, decimal Close);

    private static decimal? Atr(IReadOnlyList<Bar> bars, int endIdx, int period = 14) =>
        AtrCalculator.Compute(bars, b => b.High, b => b.Low, b => b.Close, endIdx, period);

    [Fact]
    public void Compute_ConstantRange_ReturnsThatRange()
    {
        // Flat closes, constant 2-point daily range: every true range is 2.
        var bars = Enumerable.Range(0, 20).Select(_ => new Bar(101m, 99m, 100m)).ToList();
        Atr(bars, bars.Count - 1).Should().Be(2m);
    }

    [Fact]
    public void Compute_GapCountsViaPrevClose()
    {
        // A gap day whose intraday range is small still registers the gap:
        // TR = max(high-low, |high-prevClose|, |low-prevClose|).
        var bars = Enumerable.Range(0, 15).Select(_ => new Bar(101m, 99m, 100m)).ToList();
        bars.Add(new Bar(111m, 110m, 110.5m)); // gapped +10, range only 1
        var atr = Atr(bars, bars.Count - 1);
        // 13 ordinary TRs of 2 + one gap TR of 11 (111 - prev close 100)
        atr.Should().Be((13 * 2m + 11m) / 14m);
    }

    [Theory]
    [InlineData(10)] // too short: period+1 bars needed
    [InlineData(14)] // exactly period bars: still one short
    public void Compute_InsufficientHistory_ReturnsNull(int count)
    {
        var bars = Enumerable.Range(0, count).Select(_ => new Bar(101m, 99m, 100m)).ToList();
        Atr(bars, bars.Count - 1).Should().BeNull();
    }

    [Fact]
    public void Compute_BadData_ReturnsNull()
    {
        var bars = Enumerable.Range(0, 20).Select(_ => new Bar(101m, 99m, 100m)).ToList();
        bars[12] = new Bar(0m, 0m, 0m);
        Atr(bars, bars.Count - 1).Should().BeNull();
    }
}

public class AtrRiskParitySizingTests
{
    private static readonly PositionSizingService Sut = new();

    private static AccountRiskProfile AtrProfile() => new()
    {
        AccountId = 1,
        MaxOpenPositions = 3,
        FlatPositionPct = 0.5m,
        LockedCapitalPct = 0m,
        SizingStyle = SizingStyle.AtrRiskParity,
        RiskPerTradePct = 0.01m,
        AtrStopMultiple = 2.0m,
        AtrTargetMultiple = 3.5m,
    };

    private static StockSignal Signal(decimal priceUsd, decimal? atr) => new()
    {
        AccountId = 1, Symbol = "TEST", CurrentPrice = priceUsd, Atr14 = atr,
    };

    [Fact]
    public async Task AtrStyle_SizesFromRiskBudgetOverStopDistance()
    {
        // £10,000 portfolio, 1% risk = £100. ATR $2, stop 2xATR = $4; at
        // usdToBase 0.8 the stop distance is £3.20 -> 31.25 shares desired.
        // Price $100 -> £80/share; desired budget £2,500 - inside every clamp.
        var result = await Sut.CalculateAsync(
            Signal(100m, 2m), 0, availableCash: 10_000m, totalPortfolioValue: 10_000m,
            AtrProfile(), priceOverride: 80m, openPositionsValue: 0m, usdToBaseRate: 0.8m);

        result.CanTrade.Should().BeTrue();
        result.Quantity.Should().Be(31.25m);
        // If the stop is hit: 31.25 shares x £3.20 = exactly the £100 risk budget.
        (result.Quantity * 3.20m).Should().Be(100m);
    }

    [Fact]
    public async Task AtrStyle_FlatClampsRemainTheCeiling()
    {
        // A tiny ATR asks for a huge position; the flat-slice cap (50% of
        // £10,000 = £5,000) must clamp it - risk parity never exceeds the
        // legacy envelope.
        var result = await Sut.CalculateAsync(
            Signal(100m, 0.05m), 0, availableCash: 100_000m, totalPortfolioValue: 10_000m,
            AtrProfile(), priceOverride: 80m, openPositionsValue: 0m, usdToBaseRate: 0.8m);

        result.CanTrade.Should().BeTrue();
        (result.Quantity * 80m).Should().BeLessThanOrEqualTo(5_000m);
    }

    [Fact]
    public async Task AtrStyle_MissingAtr_FallsBackToFlatSizing()
    {
        var withAtr = await Sut.CalculateAsync(
            Signal(100m, null), 0, 10_000m, 10_000m, AtrProfile(), 80m, 0m, 0.8m);
        var flatProfile = AtrProfile();
        flatProfile.SizingStyle = SizingStyle.FlatPercent;
        var flat = await Sut.CalculateAsync(
            Signal(100m, null), 0, 10_000m, 10_000m, flatProfile, 80m, 0m, 0.8m);

        withAtr.CanTrade.Should().BeTrue();
        withAtr.Quantity.Should().Be(flat.Quantity);
    }

    [Fact]
    public async Task FlatStyle_IgnoresAtrEntirely()
    {
        var profile = AtrProfile();
        profile.SizingStyle = SizingStyle.FlatPercent;
        var withAtr = await Sut.CalculateAsync(Signal(100m, 2m), 0, 10_000m, 10_000m, profile, 80m, 0m, 0.8m);
        var without = await Sut.CalculateAsync(Signal(100m, null), 0, 10_000m, 10_000m, profile, 80m, 0m, 0.8m);

        withAtr.Quantity.Should().Be(without.Quantity);
    }
}
