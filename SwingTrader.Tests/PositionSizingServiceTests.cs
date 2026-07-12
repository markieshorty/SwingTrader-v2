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
    public async Task CalculateAsync_FlatMode_BudgetsFixedSliceOfPortfolio_IgnoringTier()
    {
        // Flat 15% of a £10,000 account = £1,500/position - even at Tier 1,
        // whose pool sizing would only have allowed £10,000 x 10% x 40% = £400.
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile
        {
            SizingMode = PositionSizingMode.Flat,
            FlatPositionPct = 0.15m,
            MaxOpenPositions = 2,
            LockedCapitalPct = 0.60m, // 15% x 2 = 30% <= 40% un-locked
        };

        var result = await sut.CalculateAsync(MakeSignal(price: 100m), CapitalTier.Tier1, currentOpenPositions: 0,
            availableCash: 10000m, totalPortfolioValue: 10000m, profile);

        result.CanTrade.Should().BeTrue();
        result.EstimatedCost.Should().Be(1500m);
    }

    [Fact]
    public async Task CalculateAsync_FlatMode_CashBufferStillApplies()
    {
        // Only £300 cash on a £10,000 book: spendable = 300 - 200 buffer = £100,
        // well under the £1,500 flat budget.
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile
        {
            SizingMode = PositionSizingMode.Flat,
            FlatPositionPct = 0.15m,
            MaxOpenPositions = 2,
            LockedCapitalPct = 0.60m,
        };

        var result = await sut.CalculateAsync(MakeSignal(price: 10m), CapitalTier.Tier3, currentOpenPositions: 0,
            availableCash: 300m, totalPortfolioValue: 10000m, profile);

        result.CanTrade.Should().BeTrue();
        result.EstimatedCost.Should().BeLessThanOrEqualTo(100m);
    }

    [Fact]
    public async Task CalculateAsync_FlatMode_MaxOpenPositionsStillEnforced()
    {
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile
        {
            SizingMode = PositionSizingMode.Flat,
            FlatPositionPct = 0.15m,
            MaxOpenPositions = 2,
            LockedCapitalPct = 0.60m,
        };

        var result = await sut.CalculateAsync(MakeSignal(), CapitalTier.Tier3, currentOpenPositions: 2,
            availableCash: 10000m, totalPortfolioValue: 10000m, profile);

        result.CanTrade.Should().BeFalse();
        result.RejectionReason.Should().Contain("Max open positions");
    }

    [Fact]
    public async Task CalculateAsync_OpenPositionsAtProfileMax_Rejects()
    {
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile { MaxOpenPositions = 2 };

        var result = await sut.CalculateAsync(MakeSignal(), CapitalTier.Tier1, currentOpenPositions: 2,
            availableCash: 10000m, totalPortfolioValue: 10000m, profile);

        result.CanTrade.Should().BeFalse();
        result.RejectionReason.Should().Contain("2");
    }

    [Fact]
    public async Task CalculateAsync_BelowProfileMaxOpenPositions_Allowed()
    {
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile { MaxOpenPositions = 5 };

        var result = await sut.CalculateAsync(MakeSignal(), CapitalTier.Tier1, currentOpenPositions: 2,
            availableCash: 10000m, totalPortfolioValue: 10000m, profile);

        result.CanTrade.Should().BeTrue();
    }

    [Fact]
    public async Task CalculateAsync_ActiveCapitalPoolFullyDeployed_Rejects()
    {
        // Tier1 active pool = 10% of £10,000 = £1,000. With £1,000 already
        // deployed the pool has no headroom - the per-position cap alone never
        // bounded TOTAL deployment, so this used to pass on cash alone.
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile { MaxOpenPositions = 10 };

        var result = await sut.CalculateAsync(MakeSignal(), CapitalTier.Tier1, currentOpenPositions: 3,
            availableCash: 8000m, totalPortfolioValue: 10000m, profile, openPositionsValue: 1000m);

        result.CanTrade.Should().BeFalse();
        result.RejectionReason.Should().Contain("fully deployed");
    }

    [Fact]
    public async Task CalculateAsync_PartialActiveHeadroom_ClampsBudgetToRemainingPool()
    {
        // Tier1 pool £1,000; £900 deployed -> £100 headroom, even though the
        // per-position cap (33% of £1,000 = £330) and cash would allow more.
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile { MaxOpenPositions = 10, MaxPositionPctOfActive = 0.33m };

        var result = await sut.CalculateAsync(MakeSignal(price: 100m), CapitalTier.Tier1, currentOpenPositions: 3,
            availableCash: 8000m, totalPortfolioValue: 10000m, profile, openPositionsValue: 900m);

        result.CanTrade.Should().BeTrue();
        result.EstimatedCost.Should().BeLessThanOrEqualTo(100m);
    }

    [Fact]
    public async Task CalculateAsync_ConvictionDoesNotAffectBudget()
    {
        // Conviction-weighted sizing was tried and reverted (2026-07-10): the
        // backtest showed conviction isn't predictive above ~7 with current
        // weights, so scaling budget by it halved returns. Sizing must be
        // conviction-agnostic until that changes - this pins the revert.
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile();

        var low = await sut.CalculateAsync(
            new StockSignal { Symbol = "AAA", CurrentPrice = 100m, ConvictionScore = 6.0m },
            CapitalTier.Tier3, 0, 100000m, 100000m, profile);
        var high = await sut.CalculateAsync(
            new StockSignal { Symbol = "AAA", CurrentPrice = 100m, ConvictionScore = 9.0m },
            CapitalTier.Tier3, 0, 100000m, 100000m, profile);

        low.EstimatedCost.Should().Be(high.EstimatedCost);
    }

    [Fact]
    public async Task CalculateAsync_HigherMaxPositionPctOfActive_YieldsLargerBudget()
    {
        var sut = new PositionSizingService();
        var tightProfile = new AccountRiskProfile { MaxPositionPctOfActive = 0.05m };
        var looseProfile = new AccountRiskProfile { MaxPositionPctOfActive = 0.30m };

        var tight = await sut.CalculateAsync(MakeSignal(), CapitalTier.Tier3, 0, 100000m, 100000m, tightProfile);
        var loose = await sut.CalculateAsync(MakeSignal(), CapitalTier.Tier3, 0, 100000m, 100000m, looseProfile);

        tight.EstimatedCost.Should().BeLessThan(loose.EstimatedCost);
    }

    // ── Funnel F2: Forward-score sizing multiplier ───────────────────────────

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
    public async Task CalculateAsync_ForwardTilt_ScalesTheBudget_AndReportsTheMultiplier()
    {
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile { SizingAggressiveness = 1.0m };
        StockSignal Sig(decimal? forward) => new()
        {
            Symbol = "AAA", CurrentPrice = 100m, ForwardScore = forward,
        };

        var strong = await sut.CalculateAsync(Sig(10m), CapitalTier.Tier3, 0, 100000m, 100000m, profile);
        var weak = await sut.CalculateAsync(Sig(0m), CapitalTier.Tier3, 0, 100000m, 100000m, profile);
        var noScore = await sut.CalculateAsync(Sig(null), CapitalTier.Tier3, 0, 100000m, 100000m, profile);

        strong.AppliedMultiplier.Should().Be(1.5m);
        weak.AppliedMultiplier.Should().Be(0.5m);
        noScore.AppliedMultiplier.Should().Be(1m);
        strong.EstimatedCost.Should().Be(noScore.EstimatedCost * 1.5m);
        weak.EstimatedCost.Should().Be(noScore.EstimatedCost * 0.5m);
    }

    [Fact]
    public async Task CalculateAsync_ForwardTilt_NeverEscapesTheCashAndPoolClamps()
    {
        // A 1.5x tilt on a budget already limited by spendable cash must not
        // spend more cash - the tilt redistributes, the rails stay supreme.
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile { SizingAggressiveness = 1.0m };
        var signal = new StockSignal { Symbol = "AAA", CurrentPrice = 10m, ForwardScore = 10m };

        var result = await sut.CalculateAsync(signal, CapitalTier.Tier3, 0,
            availableCash: 300m, totalPortfolioValue: 10000m, profile);

        result.CanTrade.Should().BeTrue();
        result.EstimatedCost.Should().BeLessThanOrEqualTo(100m); // 300 cash - 200 buffer
    }

    [Fact]
    public async Task CalculateAsync_DialAtZero_IsIdenticalToPreF2Sizing()
    {
        // The F2 deploy-safety property: aggressiveness 0 (the default) means
        // a maxed-out forward score changes nothing at all.
        var sut = new PositionSizingService();
        var profile = new AccountRiskProfile(); // SizingAggressiveness defaults 0
        var withForward = new StockSignal { Symbol = "AAA", CurrentPrice = 100m, ForwardScore = 10m };
        var withoutForward = new StockSignal { Symbol = "AAA", CurrentPrice = 100m };

        var a = await sut.CalculateAsync(withForward, CapitalTier.Tier1, 0, 10000m, 10000m, profile);
        var b = await sut.CalculateAsync(withoutForward, CapitalTier.Tier1, 0, 10000m, 10000m, profile);

        a.EstimatedCost.Should().Be(b.EstimatedCost);
        a.AppliedMultiplier.Should().Be(1m);
    }
}
