using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class PortfolioRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task GetLatestSnapshotAsync_ReturnsMostRecentByDate()
    {
        await using var db = CreateDb();
        var repo = new PortfolioRepository(db);
        await repo.AddAsync(new PortfolioSnapshot { AccountId = 1, TradingMode = TradingMode.Demo, SnapshotDate = new DateOnly(2026, 7, 1), TotalCapital = 100m });
        await repo.AddAsync(new PortfolioSnapshot { AccountId = 1, TradingMode = TradingMode.Demo, SnapshotDate = new DateOnly(2026, 7, 5), TotalCapital = 200m });

        var result = await repo.GetLatestSnapshotAsync(1, TradingMode.Demo);

        result!.TotalCapital.Should().Be(200m);
    }

    [Fact]
    public async Task GetLatestSnapshotAsync_NoRows_ReturnsNull()
    {
        await using var db = CreateDb();
        var repo = new PortfolioRepository(db);

        var result = await repo.GetLatestSnapshotAsync(1, TradingMode.Demo);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetSnapshotHistoryAsync_FiltersByDateRangeAndOrdersDescending()
    {
        await using var db = CreateDb();
        var repo = new PortfolioRepository(db);
        await repo.AddAsync(new PortfolioSnapshot { AccountId = 1, TradingMode = TradingMode.Demo, SnapshotDate = new DateOnly(2026, 6, 1), TotalCapital = 50m });
        await repo.AddAsync(new PortfolioSnapshot { AccountId = 1, TradingMode = TradingMode.Demo, SnapshotDate = new DateOnly(2026, 7, 1), TotalCapital = 100m });
        await repo.AddAsync(new PortfolioSnapshot { AccountId = 1, TradingMode = TradingMode.Demo, SnapshotDate = new DateOnly(2026, 7, 5), TotalCapital = 200m });

        var result = (await repo.GetSnapshotHistoryAsync(1, TradingMode.Demo, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31))).ToList();

        result.Should().HaveCount(2);
        result[0].TotalCapital.Should().Be(200m);
    }

    [Fact]
    public async Task UpdateAsync_PersistsChangesAndSetsUpdatedAt()
    {
        await using var db = CreateDb();
        var repo = new PortfolioRepository(db);
        var snapshot = await repo.AddAsync(new PortfolioSnapshot { AccountId = 1, TradingMode = TradingMode.Demo, SnapshotDate = new DateOnly(2026, 7, 1), TotalCapital = 100m });

        snapshot.TotalCapital = 150m;
        await repo.UpdateAsync(snapshot);

        var reloaded = await repo.GetByIdAsync(1, snapshot.Id);
        reloaded!.TotalCapital.Should().Be(150m);
        reloaded.UpdatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task DeleteAsync_RemovesMatchingRow()
    {
        await using var db = CreateDb();
        var repo = new PortfolioRepository(db);
        var snapshot = await repo.AddAsync(new PortfolioSnapshot { AccountId = 1, TradingMode = TradingMode.Demo, SnapshotDate = new DateOnly(2026, 7, 1), TotalCapital = 100m });

        await repo.DeleteAsync(1, snapshot.Id);

        var reloaded = await repo.GetByIdAsync(1, snapshot.Id);
        reloaded.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_WrongAccount_DoesNotDelete()
    {
        await using var db = CreateDb();
        var repo = new PortfolioRepository(db);
        var snapshot = await repo.AddAsync(new PortfolioSnapshot { AccountId = 1, TradingMode = TradingMode.Demo, SnapshotDate = new DateOnly(2026, 7, 1), TotalCapital = 100m });

        await repo.DeleteAsync(2, snapshot.Id);

        var reloaded = await repo.GetByIdAsync(1, snapshot.Id);
        reloaded.Should().NotBeNull();
    }
}
