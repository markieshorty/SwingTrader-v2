using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

// GetClosedOnDateAsync backs ExecutionService's same-day re-buy guard - a
// same-day re-enqueue after an exit frees capital would otherwise
// immediately re-buy the exact symbol just sold if its signal was still
// sitting there approved.
public class TradeRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task GetClosedOnDateAsync_TradeClosedToday_IsReturned()
    {
        await using var db = CreateDb();
        var repo = new TradeRepository(db);
        var today = new DateOnly(2026, 7, 8);
        await repo.AddAsync(new Trade
        {
            AccountId = 1, Symbol = "WDAY", EntryPrice = 100m, Quantity = 1, Status = TradeStatus.Closed,
            OpenedAt = today.ToDateTime(TimeOnly.MinValue), ClosedAt = today.ToDateTime(new TimeOnly(16, 0)),
        });

        var result = await repo.GetClosedOnDateAsync(1, TradingMode.Demo, today);

        result.Should().ContainSingle(t => t.Symbol == "WDAY");
    }

    [Fact]
    public async Task GetClosedOnDateAsync_TradeOpenedTodayButClosedYesterday_IsNotReturned()
    {
        // Guards against accidentally filtering by OpenedAt instead of
        // ClosedAt - a position held for days and exited today must be
        // caught even though it wasn't opened today, and vice versa.
        await using var db = CreateDb();
        var repo = new TradeRepository(db);
        var today = new DateOnly(2026, 7, 8);
        var yesterday = today.AddDays(-1);
        await repo.AddAsync(new Trade
        {
            AccountId = 1, Symbol = "OLD", EntryPrice = 100m, Quantity = 1, Status = TradeStatus.Closed,
            OpenedAt = today.ToDateTime(TimeOnly.MinValue), ClosedAt = yesterday.ToDateTime(new TimeOnly(16, 0)),
        });

        var result = await repo.GetClosedOnDateAsync(1, TradingMode.Demo, today);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPendingTradesAsync_ReturnsOnlyPendingForModeAndAccount()
    {
        await using var db = CreateDb();
        var repo = new TradeRepository(db);
        await repo.AddAsync(new Trade { AccountId = 1, Symbol = "AAA", EntryPrice = 10m, Quantity = 1, Status = TradeStatus.Pending, TradingMode = TradingMode.Demo });
        await repo.AddAsync(new Trade { AccountId = 1, Symbol = "BBB", EntryPrice = 10m, Quantity = 1, Status = TradeStatus.Open, TradingMode = TradingMode.Demo });
        await repo.AddAsync(new Trade { AccountId = 1, Symbol = "CCC", EntryPrice = 10m, Quantity = 1, Status = TradeStatus.Cancelled, TradingMode = TradingMode.Demo });
        await repo.AddAsync(new Trade { AccountId = 1, Symbol = "DDD", EntryPrice = 10m, Quantity = 1, Status = TradeStatus.Pending, TradingMode = TradingMode.Live });
        await repo.AddAsync(new Trade { AccountId = 2, Symbol = "EEE", EntryPrice = 10m, Quantity = 1, Status = TradeStatus.Pending, TradingMode = TradingMode.Demo });

        var result = await repo.GetPendingTradesAsync(1, TradingMode.Demo);

        result.Should().ContainSingle(t => t.Symbol == "AAA");
    }

    [Fact]
    public async Task GetClosedOnDateAsync_StillOpenTrade_IsNotReturned()
    {
        await using var db = CreateDb();
        var repo = new TradeRepository(db);
        var today = new DateOnly(2026, 7, 8);
        await repo.AddAsync(new Trade
        {
            AccountId = 1, Symbol = "WDAY", EntryPrice = 100m, Quantity = 1, Status = TradeStatus.Open,
            OpenedAt = today.ToDateTime(TimeOnly.MinValue),
        });

        var result = await repo.GetClosedOnDateAsync(1, TradingMode.Demo, today);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetClosedOnDateAsync_DifferentAccount_IsNotReturned()
    {
        await using var db = CreateDb();
        var repo = new TradeRepository(db);
        var today = new DateOnly(2026, 7, 8);
        await repo.AddAsync(new Trade
        {
            AccountId = 2, Symbol = "WDAY", EntryPrice = 100m, Quantity = 1, Status = TradeStatus.Closed,
            OpenedAt = today.ToDateTime(TimeOnly.MinValue), ClosedAt = today.ToDateTime(new TimeOnly(16, 0)),
        });

        var result = await repo.GetClosedOnDateAsync(1, TradingMode.Demo, today);

        result.Should().BeEmpty();
    }
}
