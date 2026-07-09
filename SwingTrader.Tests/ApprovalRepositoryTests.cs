using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class ApprovalRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task GetByDateAsync_MatchingRow_IsReturned()
    {
        await using var db = CreateDb();
        var repo = new ApprovalRepository(db);
        var date = new DateOnly(2026, 7, 8);
        await repo.AddAsync(new TradeApproval { AccountId = 1, TradingMode = TradingMode.Demo, TradeDate = date, IsApproved = true });

        var result = await repo.GetByDateAsync(1, TradingMode.Demo, date);

        result.Should().NotBeNull();
        result!.IsApproved.Should().BeTrue();
    }

    [Fact]
    public async Task GetByDateAsync_WrongTradingMode_ReturnsNull()
    {
        await using var db = CreateDb();
        var repo = new ApprovalRepository(db);
        var date = new DateOnly(2026, 7, 8);
        await repo.AddAsync(new TradeApproval { AccountId = 1, TradingMode = TradingMode.Demo, TradeDate = date, IsApproved = true });

        var result = await repo.GetByDateAsync(1, TradingMode.Live, date);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdAsync_WrongAccount_ReturnsNull()
    {
        await using var db = CreateDb();
        var repo = new ApprovalRepository(db);
        var added = await repo.AddAsync(new TradeApproval { AccountId = 1, TradingMode = TradingMode.Demo, TradeDate = new DateOnly(2026, 7, 8) });

        var result = await repo.GetByIdAsync(2, added.Id);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ListRecentAsync_OrdersByDateDescendingAndRespectsCount()
    {
        await using var db = CreateDb();
        var repo = new ApprovalRepository(db);
        await repo.AddAsync(new TradeApproval { AccountId = 1, TradingMode = TradingMode.Demo, TradeDate = new DateOnly(2026, 7, 1) });
        await repo.AddAsync(new TradeApproval { AccountId = 1, TradingMode = TradingMode.Demo, TradeDate = new DateOnly(2026, 7, 3) });
        await repo.AddAsync(new TradeApproval { AccountId = 1, TradingMode = TradingMode.Demo, TradeDate = new DateOnly(2026, 7, 2) });

        var result = await repo.ListRecentAsync(1, TradingMode.Demo, 2);

        result.Should().HaveCount(2);
        result[0].TradeDate.Should().Be(new DateOnly(2026, 7, 3));
        result[1].TradeDate.Should().Be(new DateOnly(2026, 7, 2));
    }

    [Fact]
    public async Task UpdateAsync_PersistsChangesAndSetsUpdatedAt()
    {
        await using var db = CreateDb();
        var repo = new ApprovalRepository(db);
        var approval = await repo.AddAsync(new TradeApproval { AccountId = 1, TradingMode = TradingMode.Demo, TradeDate = new DateOnly(2026, 7, 8), IsApproved = false });

        approval.IsApproved = true;
        await repo.UpdateAsync(approval);

        var reloaded = await repo.GetByIdAsync(1, approval.Id);
        reloaded!.IsApproved.Should().BeTrue();
        reloaded.UpdatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task AnyApprovedAsync_NoApprovedRows_ReturnsFalse()
    {
        await using var db = CreateDb();
        var repo = new ApprovalRepository(db);
        await repo.AddAsync(new TradeApproval { AccountId = 1, TradingMode = TradingMode.Demo, TradeDate = new DateOnly(2026, 7, 8), IsApproved = false });

        var result = await repo.AnyApprovedAsync(1, TradingMode.Demo);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task AnyApprovedAsync_HasApprovedRow_ReturnsTrue()
    {
        await using var db = CreateDb();
        var repo = new ApprovalRepository(db);
        await repo.AddAsync(new TradeApproval { AccountId = 1, TradingMode = TradingMode.Demo, TradeDate = new DateOnly(2026, 7, 8), IsApproved = true });

        var result = await repo.AnyApprovedAsync(1, TradingMode.Demo);

        result.Should().BeTrue();
    }
}
