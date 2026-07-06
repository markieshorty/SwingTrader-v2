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
