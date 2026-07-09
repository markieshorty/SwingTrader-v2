using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class WatchlistHistoryRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task AddAsync_UppercasesSymbolAndPersists()
    {
        await using var db = CreateDb();
        var repo = new WatchlistHistoryRepository(db);

        await repo.AddAsync(new WatchlistHistory
        {
            AccountId = 1,
            Symbol = "aapl",
            CompanyName = "Apple Inc.",
            Action = WatchlistAction.Added,
            Reason = "High conviction",
            WeekStarting = new DateOnly(2026, 7, 6),
        });

        var entry = db.WatchlistHistory.Single();
        entry.Symbol.Should().Be("AAPL");
        entry.Action.Should().Be(WatchlistAction.Added);
    }
}
