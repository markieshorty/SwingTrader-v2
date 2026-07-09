using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using SwingTrader.Core.Interfaces;
using Xunit;

namespace SwingTrader.Tests;

public class StrategyWeightsRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static StrategyWeights ValidWeights(int accountId, bool isActive = true, MarketRegime? regime = null) => new()
    {
        AccountId = accountId,
        RsiWeight = 0.17m,
        MacdWeight = 0.09m,
        VolumeWeight = 0.21m,
        SentimentWeight = 0.16m,
        SetupQualityWeight = 0.12m,
        RelativeStrengthWeight = 0.10m,
        PriceLevelWeight = 0.05m,
        FundamentalMomentumWeight = 0.10m,
        BuyThreshold = 6.0m,
        WatchThreshold = 5.0m,
        StopLossPctDefault = 0.05m,
        IsActive = isActive,
        ApplicableRegime = regime,
        Source = "Default",
    };

    [Fact]
    public async Task GetActiveWeightsAsync_NoRegime_ReturnsGeneralActiveRow()
    {
        await using var db = CreateDb();
        var repo = new StrategyWeightsRepository(db);
        await repo.AddAsync(ValidWeights(1));

        var result = await repo.GetActiveWeightsAsync(1);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetActiveWeightsAsync_WithRegime_PrefersRegimeSpecificRow()
    {
        await using var db = CreateDb();
        var repo = new StrategyWeightsRepository(db);
        var general = await repo.AddAsync(ValidWeights(1));
        var bull = await repo.AddAsync(ValidWeights(1, regime: MarketRegime.Bull));

        var result = await repo.GetActiveWeightsAsync(1, MarketRegime.Bull);

        result!.Id.Should().Be(bull.Id);
    }

    [Fact]
    public async Task GetActiveWeightsAsync_RegimeRequestedButNoneExists_FallsBackToGeneral()
    {
        await using var db = CreateDb();
        var repo = new StrategyWeightsRepository(db);
        var general = await repo.AddAsync(ValidWeights(1));

        var result = await repo.GetActiveWeightsAsync(1, MarketRegime.Bear);

        result!.Id.Should().Be(general.Id);
    }

    [Fact]
    public async Task SetActiveAsync_DeactivatesOtherGeneralRows()
    {
        await using var db = CreateDb();
        var repo = new StrategyWeightsRepository(db);
        var first = await repo.AddAsync(ValidWeights(1));
        var second = await repo.AddAsync(ValidWeights(1, isActive: false));

        await repo.SetActiveAsync(1, second.Id);

        var active = await repo.GetActiveWeightsAsync(1);
        active!.Id.Should().Be(second.Id);
    }

    [Fact]
    public async Task SetActiveAsync_UnknownId_Throws()
    {
        await using var db = CreateDb();
        var repo = new StrategyWeightsRepository(db);

        var act = async () => await repo.SetActiveAsync(1, 999);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SetRegimeActiveAsync_SetsApplicableRegimeAndActivatesRow()
    {
        await using var db = CreateDb();
        var repo = new StrategyWeightsRepository(db);
        var row = await repo.AddAsync(ValidWeights(1, isActive: false));

        await repo.SetRegimeActiveAsync(1, row.Id, MarketRegime.Bull);

        var active = await repo.GetActiveWeightsAsync(1, MarketRegime.Bull);
        active!.Id.Should().Be(row.Id);
    }

    [Fact]
    public async Task UpdateWeightsAsync_UpdatesActiveGeneralRow()
    {
        await using var db = CreateDb();
        var repo = new StrategyWeightsRepository(db);
        await repo.AddAsync(ValidWeights(1));

        await repo.UpdateWeightsAsync(1, new StrategyWeightsUpdate(
            RsiWeight: 0.20m, MacdWeight: 0.10m, VolumeWeight: 0.20m, SentimentWeight: 0.15m,
            SetupQualityWeight: 0.10m, RelativeStrengthWeight: 0.10m, PriceLevelWeight: 0.05m,
            FundamentalMomentumWeight: 0.10m, BuyThreshold: 6.5m, WatchThreshold: 5.5m, StopLossPctDefault: 0.06m));

        var active = await repo.GetActiveWeightsAsync(1);
        active!.RsiWeight.Should().Be(0.20m);
        active.BuyThreshold.Should().Be(6.5m);
    }

    [Fact]
    public async Task UpdateWeightsAsync_NoActiveRow_Throws()
    {
        await using var db = CreateDb();
        var repo = new StrategyWeightsRepository(db);

        var act = async () => await repo.UpdateWeightsAsync(1, new StrategyWeightsUpdate(
            RsiWeight: 0.20m, MacdWeight: 0.10m, VolumeWeight: 0.20m, SentimentWeight: 0.15m,
            SetupQualityWeight: 0.10m, RelativeStrengthWeight: 0.10m, PriceLevelWeight: 0.05m,
            FundamentalMomentumWeight: 0.10m, BuyThreshold: 6.5m, WatchThreshold: 5.5m, StopLossPctDefault: 0.06m));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SeedDefaultAsync_NoExistingRows_CreatesDefault()
    {
        await using var db = CreateDb();
        var repo = new StrategyWeightsRepository(db);

        await repo.SeedDefaultAsync(1);

        var active = await repo.GetActiveWeightsAsync(1);
        active.Should().NotBeNull();
        active!.Source.Should().Be("Default");
    }

    [Fact]
    public async Task SeedDefaultAsync_AlreadySeeded_DoesNotAddDuplicate()
    {
        await using var db = CreateDb();
        var repo = new StrategyWeightsRepository(db);
        await repo.AddAsync(ValidWeights(1));

        await repo.SeedDefaultAsync(1);

        db.StrategyWeights.Count(w => w.AccountId == 1).Should().Be(1);
    }
}
