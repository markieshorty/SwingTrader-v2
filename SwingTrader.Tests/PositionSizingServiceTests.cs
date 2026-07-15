using FluentAssertions;
using SwingTrader.Agents.Execution;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

public class PositionSizingServiceTests
{
    private static StockSignal MakeSignal(decimal price = 100m) => new() { Symbol = "AAA", CurrentPrice = price };

    [Fact]
    public async Task CalculateAsync_BudgetsFixedSliceOfPortfolio()
    {
        // Flat 15% of a £10,000 account = £1,500 per position.
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile
        {
            SizingMode = PositionSizingMode.Flat,
            FlatPositionPct = 0.15m,
            MaxOpenPositions = 2,
            LockedCapitalPct = 0.60m, // 15% x 2 = 30% <= 40% un-locked
        };

        var result = await sut.CalculateAsync(MakeSignal(price: 100m), currentOpenPositions: 0,
            availableCash: 10000m, totalPortfolioValue: 10000m, profile);

        result.CanTrade.Should().BeTrue();
        result.EstimatedCost.Should().Be(1500m);
    }

    [Fact]
    public async Task CalculateAsync_CashBufferStillApplies()
    {
        // Only £300 cash on a £10,000 book: spendable = 300 - 200 buffer = £100,
        // well under the £1,500 flat budget.
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile
        {
            FlatPositionPct = 0.15m,
            MaxOpenPositions = 2,
            LockedCapitalPct = 0.60m,
        };

        var result = await sut.CalculateAsync(MakeSignal(price: 10m), currentOpenPositions: 0,
            availableCash: 300m, totalPortfolioValue: 10000m, profile);

        result.CanTrade.Should().BeTrue();
        result.EstimatedCost.Should().BeLessThanOrEqualTo(100m);
    }

    [Fact]
    public async Task CalculateAsync_OpenPositionsAtProfileMax_Rejects()
    {
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile { MaxOpenPositions = 2 };

        var result = await sut.CalculateAsync(MakeSignal(), currentOpenPositions: 2,
            availableCash: 10000m, totalPortfolioValue: 10000m, profile);

        result.CanTrade.Should().BeFalse();
        result.RejectionReason.Should().Contain("Max open positions");
    }

    [Fact]
    public async Task CalculateAsync_BelowProfileMaxOpenPositions_Allowed()
    {
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile { MaxOpenPositions = 5 };

        var result = await sut.CalculateAsync(MakeSignal(), currentOpenPositions: 2,
            availableCash: 10000m, totalPortfolioValue: 10000m, profile);

        result.CanTrade.Should().BeTrue();
    }

    [Fact]
    public async Task CalculateAsync_DeployableShareFullyCommitted_Rejects()
    {
        // Locked 60% -> deployable = £4,000. With £4,000 already in open
        // positions there's no un-locked headroom left; the reserve behind
        // locked capital must never be spent.
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile { MaxOpenPositions = 10, LockedCapitalPct = 0.60m };

        var result = await sut.CalculateAsync(MakeSignal(), currentOpenPositions: 3,
            availableCash: 8000m, totalPortfolioValue: 10000m, profile, openPositionsValue: 4000m);

        result.CanTrade.Should().BeFalse();
        result.RejectionReason.Should().Contain("fully committed");
    }

    [Fact]
    public async Task CalculateAsync_PartialDeployableHeadroom_ClampsBudget()
    {
        // Deployable £4,000; £3,900 already deployed -> £100 headroom, even
        // though the flat budget (10% of £10,000 = £1,000) and cash allow more.
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile { MaxOpenPositions = 10, LockedCapitalPct = 0.60m };

        var result = await sut.CalculateAsync(MakeSignal(price: 100m), currentOpenPositions: 3,
            availableCash: 8000m, totalPortfolioValue: 10000m, profile, openPositionsValue: 3900m);

        result.CanTrade.Should().BeTrue();
        result.EstimatedCost.Should().BeLessThanOrEqualTo(100m);
    }

    [Fact]
    public async Task CalculateAsync_HigherFlatPositionPct_YieldsLargerBudget()
    {
        var sut = new PositionSizingService();
        var tightProfile = new AccountRiskProfile { FlatPositionPct = 0.05m };
        var looseProfile = new AccountRiskProfile { FlatPositionPct = 0.20m };

        var tight = await sut.CalculateAsync(MakeSignal(), 0, 100000m, 100000m, tightProfile);
        var loose = await sut.CalculateAsync(MakeSignal(), 0, 100000m, 100000m, looseProfile);

        tight.EstimatedCost.Should().BeLessThan(loose.EstimatedCost);
    }

    [Fact]
    public async Task CalculateAsync_ConvictionDoesNotAffectBudget()
    {
        // Conviction-weighted sizing was tried and reverted (2026-07-10): the
        // backtest showed conviction isn't predictive above ~7 with current
        // weights, so scaling budget by it halved returns. Sizing must be
        // conviction-agnostic - this pins the revert.
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile();

        var low = await sut.CalculateAsync(
            new StockSignal { Symbol = "AAA", CurrentPrice = 100m, ConvictionScore = 6.0m }, 0, 100000m, 100000m, profile);
        var high = await sut.CalculateAsync(
            new StockSignal { Symbol = "AAA", CurrentPrice = 100m, ConvictionScore = 9.0m }, 0, 100000m, 100000m, profile);

        low.EstimatedCost.Should().Be(high.EstimatedCost);
    }

    // ── Funnel Forward-score sizing multiplier ───────────────────────────────

    [Theory]
    [InlineData(null, false, 1.0, 1.0)]   // no forward score -> exactly 1
    [InlineData(8.0, true, 1.0, 1.0)]     // degraded -> exactly 1, however strong
    [InlineData(8.0, false, 0.0, 1.0)]    // dial at 0 (the default) -> exactly 1
    [InlineData(10.0, false, 1.0, 1.5)]   // max score, full dial -> +MaxSizingTilt
    [InlineData(0.0, false, 1.0, 0.5)]    // min score, full dial -> -MaxSizingTilt
    [InlineData(5.0, false, 1.0, 1.0)]    // neutral score -> 1 at any dial
    [InlineData(7.5, false, 0.5, 1.125)]  // tilt 0.5 x aggr 0.5 x maxTilt 0.5
    public void ComputeForwardMultiplier_MapsScoreAndDialToTheTiltBand(
        double? forward, bool degraded, double aggressiveness, double expected) =>
        PositionSizingService.ComputeForwardMultiplier(
                (decimal?)forward, degraded, (decimal)aggressiveness)
            .Should().Be((decimal)expected);

    [Fact]
    public async Task CalculateAsync_FunnelMode_ForwardTilt_ScalesTheBudget_AndReportsTheMultiplier()
    {
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile { SizingMode = PositionSizingMode.Funnel, SizingAggressiveness = 1.0m };
        StockSignal Sig(decimal? forward) => new() { Symbol = "AAA", CurrentPrice = 100m, ForwardScore = forward };

        var strong = await sut.CalculateAsync(Sig(10m), 0, 100000m, 100000m, profile);
        var weak = await sut.CalculateAsync(Sig(0m), 0, 100000m, 100000m, profile);
        var noScore = await sut.CalculateAsync(Sig(null), 0, 100000m, 100000m, profile);

        strong.AppliedMultiplier.Should().Be(1.5m);
        weak.AppliedMultiplier.Should().Be(0.5m);
        noScore.AppliedMultiplier.Should().Be(1m);
        strong.EstimatedCost.Should().Be(noScore.EstimatedCost * 1.5m);
        weak.EstimatedCost.Should().Be(noScore.EstimatedCost * 0.5m);
    }

    [Fact]
    public async Task CalculateAsync_FlatMode_IgnoresAggressiveness()
    {
        // In Flat mode the funnel dial never moves size, even wide open with a
        // maxed forward score.
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile { SizingMode = PositionSizingMode.Flat, SizingAggressiveness = 1.0m };
        var strong = new StockSignal { Symbol = "AAA", CurrentPrice = 100m, ForwardScore = 10m };

        var result = await sut.CalculateAsync(strong, 0, 100000m, 100000m, profile);

        result.AppliedMultiplier.Should().Be(1m);
    }

    [Fact]
    public async Task CalculateAsync_ForwardTilt_NeverEscapesTheCashClamp()
    {
        // A 1.5x tilt on a budget already limited by spendable cash must not
        // spend more cash - the tilt redistributes, the rails stay supreme.
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile { SizingMode = PositionSizingMode.Funnel, SizingAggressiveness = 1.0m };
        var signal = new StockSignal { Symbol = "AAA", CurrentPrice = 10m, ForwardScore = 10m };

        var result = await sut.CalculateAsync(signal, 0,
            availableCash: 300m, totalPortfolioValue: 10000m, profile);

        result.CanTrade.Should().BeTrue();
        result.EstimatedCost.Should().BeLessThanOrEqualTo(100m); // 300 cash - 200 buffer
    }

    [Fact]
    public async Task CalculateAsync_FunnelDialAtZero_IsIdenticalToFlat()
    {
        // The deploy-safety property: aggressiveness 0 (the default) means a
        // maxed forward score changes nothing at all.
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile { SizingMode = PositionSizingMode.Funnel }; // aggressiveness defaults 0
        var withForward = new StockSignal { Symbol = "AAA", CurrentPrice = 100m, ForwardScore = 10m };
        var withoutForward = new StockSignal { Symbol = "AAA", CurrentPrice = 100m };

        var a = await sut.CalculateAsync(withForward, 0, 10000m, 10000m, profile);
        var b = await sut.CalculateAsync(withoutForward, 0, 10000m, 10000m, profile);

        a.EstimatedCost.Should().Be(b.EstimatedCost);
        a.AppliedMultiplier.Should().Be(1m);
    }
}
