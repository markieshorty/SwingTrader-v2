using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class ReadinessSnapshotRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task UpsertAsync_NoExistingRow_Inserts()
    {
        await using var db = CreateDb();
        var repo = new ReadinessSnapshotRepository(db);

        await repo.UpsertAsync(new ReadinessSnapshot { AccountId = 1, TradingMode = TradingMode.Demo, SnapshotDate = new DateOnly(2026, 7, 8), ScoredClosedTrades = 5 });

        var latest = await repo.GetLatestAsync(1, TradingMode.Demo);
        latest!.ScoredClosedTrades.Should().Be(5);
        latest.CreatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task UpsertAsync_ExistingRowSameDate_Updates()
    {
        await using var db = CreateDb();
        var repo = new ReadinessSnapshotRepository(db);
        await repo.UpsertAsync(new ReadinessSnapshot { AccountId = 1, TradingMode = TradingMode.Demo, SnapshotDate = new DateOnly(2026, 7, 8), ScoredClosedTrades = 5 });

        await repo.UpsertAsync(new ReadinessSnapshot { AccountId = 1, TradingMode = TradingMode.Demo, SnapshotDate = new DateOnly(2026, 7, 8), ScoredClosedTrades = 8 });

        db.ReadinessSnapshots.Count(s => s.AccountId == 1).Should().Be(1);
        var latest = await repo.GetLatestAsync(1, TradingMode.Demo);
        latest!.ScoredClosedTrades.Should().Be(8);
        latest.UpdatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsAscendingByDateWithinLimit()
    {
        await using var db = CreateDb();
        var repo = new ReadinessSnapshotRepository(db);
        await repo.UpsertAsync(new ReadinessSnapshot { AccountId = 1, TradingMode = TradingMode.Demo, SnapshotDate = new DateOnly(2026, 7, 1) });
        await repo.UpsertAsync(new ReadinessSnapshot { AccountId = 1, TradingMode = TradingMode.Demo, SnapshotDate = new DateOnly(2026, 7, 2) });
        await repo.UpsertAsync(new ReadinessSnapshot { AccountId = 1, TradingMode = TradingMode.Demo, SnapshotDate = new DateOnly(2026, 7, 3) });

        var result = await repo.GetRecentAsync(1, TradingMode.Demo, days: 2);

        result.Should().HaveCount(2);
        result[0].SnapshotDate.Should().Be(new DateOnly(2026, 7, 2));
        result[1].SnapshotDate.Should().Be(new DateOnly(2026, 7, 3));
    }

    [Fact]
    public async Task GetLatestAsync_NoRows_ReturnsNull()
    {
        await using var db = CreateDb();
        var repo = new ReadinessSnapshotRepository(db);

        var result = await repo.GetLatestAsync(1, TradingMode.Demo);

        result.Should().BeNull();
    }
}
