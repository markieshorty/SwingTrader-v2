using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class ReportRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task GetByDateAsync_MatchingRow_IsReturned()
    {
        await using var db = CreateDb();
        var repo = new ReportRepository(db);
        var date = new DateOnly(2026, 7, 8);
        await repo.AddAsync(new DailyReport { AccountId = 1, TradingMode = TradingMode.Demo, ReportDate = date });

        var result = await repo.GetByDateAsync(1, TradingMode.Demo, date);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetAllAsync_OrdersByDateDescending()
    {
        await using var db = CreateDb();
        var repo = new ReportRepository(db);
        await repo.AddAsync(new DailyReport { AccountId = 1, TradingMode = TradingMode.Demo, ReportDate = new DateOnly(2026, 7, 1) });
        await repo.AddAsync(new DailyReport { AccountId = 1, TradingMode = TradingMode.Demo, ReportDate = new DateOnly(2026, 7, 5) });

        var result = (await repo.GetAllAsync(1, TradingMode.Demo)).ToList();

        result.Should().HaveCount(2);
        result[0].ReportDate.Should().Be(new DateOnly(2026, 7, 5));
    }

    [Fact]
    public async Task GetUnsentReportsAsync_OnlyReturnsUnsent()
    {
        await using var db = CreateDb();
        var repo = new ReportRepository(db);
        await repo.AddAsync(new DailyReport { AccountId = 1, TradingMode = TradingMode.Demo, ReportDate = new DateOnly(2026, 7, 1), WasSent = true });
        await repo.AddAsync(new DailyReport { AccountId = 1, TradingMode = TradingMode.Demo, ReportDate = new DateOnly(2026, 7, 2), WasSent = false });

        var result = (await repo.GetUnsentReportsAsync(1, TradingMode.Demo)).ToList();

        result.Should().ContainSingle();
        result[0].WasSent.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_PersistsChangesAndSetsUpdatedAt()
    {
        await using var db = CreateDb();
        var repo = new ReportRepository(db);
        var report = await repo.AddAsync(new DailyReport { AccountId = 1, TradingMode = TradingMode.Demo, ReportDate = new DateOnly(2026, 7, 1), WasSent = false });

        report.WasSent = true;
        await repo.UpdateAsync(report);

        var reloaded = await repo.GetByIdAsync(1, report.Id);
        reloaded!.WasSent.Should().BeTrue();
        reloaded.UpdatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task DeleteAsync_RemovesMatchingRow()
    {
        await using var db = CreateDb();
        var repo = new ReportRepository(db);
        var report = await repo.AddAsync(new DailyReport { AccountId = 1, TradingMode = TradingMode.Demo, ReportDate = new DateOnly(2026, 7, 1) });

        await repo.DeleteAsync(1, report.Id);

        var reloaded = await repo.GetByIdAsync(1, report.Id);
        reloaded.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_NonExistentRow_DoesNotThrow()
    {
        await using var db = CreateDb();
        var repo = new ReportRepository(db);

        var act = async () => await repo.DeleteAsync(1, 999);

        await act.Should().NotThrowAsync();
    }
}
