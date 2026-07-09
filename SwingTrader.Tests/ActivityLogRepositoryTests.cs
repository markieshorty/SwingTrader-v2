using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class ActivityLogRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task LogAsync_ResolvesTradingModeFromAccount()
    {
        await using var db = CreateDb();
        db.Accounts.Add(new Account { Id = 2, TradingMode = TradingMode.Live });
        await db.SaveChangesAsync();
        var repo = new ActivityLogRepository(db);

        await repo.LogAsync(2, "Execution", "Order placed", "Success");

        var entry = db.ActivityLogs.Single();
        entry.TradingMode.Should().Be(TradingMode.Live);
    }

    [Fact]
    public async Task LogAsync_SystemAccount_DoesNotLookUpTradingMode()
    {
        await using var db = CreateDb();
        var repo = new ActivityLogRepository(db);

        await repo.LogAsync(SwingTraderDbContext.SystemAccountId, "Worker", "Heartbeat", "Success");

        var entry = db.ActivityLogs.Single();
        entry.TradingMode.Should().Be(default(TradingMode));
    }

    [Fact]
    public async Task GetRecentAsync_IncludesSystemEntriesAndAccountEntries_OrderedDescending()
    {
        await using var db = CreateDb();
        db.Accounts.Add(new Account { Id = 1, TradingMode = TradingMode.Demo });
        await db.SaveChangesAsync();
        var repo = new ActivityLogRepository(db);
        await repo.LogAsync(1, "Execution", "First", "Success");
        await repo.LogAsync(SwingTraderDbContext.SystemAccountId, "Worker", "System event", "Success");
        await repo.LogAsync(1, "Execution", "Second", "Success");

        var result = (await repo.GetRecentAsync(1, TradingMode.Demo)).ToList();

        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetRecentAsync_ExcludesOtherAccountsAndWrongTradingMode()
    {
        await using var db = CreateDb();
        db.Accounts.Add(new Account { Id = 1, TradingMode = TradingMode.Demo });
        db.Accounts.Add(new Account { Id = 2, TradingMode = TradingMode.Demo });
        await db.SaveChangesAsync();
        var repo = new ActivityLogRepository(db);
        await repo.LogAsync(1, "Execution", "Mine", "Success");
        await repo.LogAsync(2, "Execution", "NotMine", "Success");

        var result = (await repo.GetRecentAsync(1, TradingMode.Demo)).ToList();

        result.Should().ContainSingle(x => x.Title == "Mine");
    }

    [Fact]
    public async Task GetRecentAsync_RespectsLimit()
    {
        await using var db = CreateDb();
        db.Accounts.Add(new Account { Id = 1, TradingMode = TradingMode.Demo });
        await db.SaveChangesAsync();
        var repo = new ActivityLogRepository(db);
        for (var i = 0; i < 5; i++)
            await repo.LogAsync(1, "Execution", $"Entry {i}", "Success");

        var result = (await repo.GetRecentAsync(1, TradingMode.Demo, limit: 3)).ToList();

        result.Should().HaveCount(3);
    }
}
