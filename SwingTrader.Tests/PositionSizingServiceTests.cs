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
    public async Task CalculateAsync_HigherMaxPositionPctOfActive_YieldsLargerBudget()
    {
        var sut = new PositionSizingService();
        var tightProfile = new AccountRiskProfile { MaxPositionPctOfActive = 0.05m };
        var looseProfile = new AccountRiskProfile { MaxPositionPctOfActive = 0.30m };

        var tight = await sut.CalculateAsync(MakeSignal(), CapitalTier.Tier3, 0, 100000m, 100000m, tightProfile);
        var loose = await sut.CalculateAsync(MakeSignal(), CapitalTier.Tier3, 0, 100000m, 100000m, looseProfile);

        tight.EstimatedCost.Should().BeLessThan(loose.EstimatedCost);
    }
}
