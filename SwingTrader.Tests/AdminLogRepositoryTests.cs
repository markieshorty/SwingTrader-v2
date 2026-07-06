using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class AdminLogRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task LogAsync_PersistsEntryWithTimestamp()
    {
        await using var db = CreateDb();
        var repo = new AdminLogRepository(db);

        await repo.LogAsync(new AdminActionLog { AdminUserId = "admin1", TargetUserId = "user1", Action = "Suspend", Details = "Reason: abuse" });

        var stored = await db.AdminActionLogs.SingleAsync();
        stored.PerformedAt.Should().NotBe(default);
        stored.Action.Should().Be("Suspend");
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsMostRecentFirst()
    {
        await using var db = CreateDb();
        var repo = new AdminLogRepository(db);
        await repo.LogAsync(new AdminActionLog { AdminUserId = "a", TargetUserId = "u1", Action = "Suspend", PerformedAt = DateTime.UtcNow.AddMinutes(-10) });
        await repo.LogAsync(new AdminActionLog { AdminUserId = "a", TargetUserId = "u2", Action = "Unsuspend", PerformedAt = DateTime.UtcNow });

        var recent = await repo.GetRecentAsync();

        recent.Should().HaveCount(2);
        recent[0].TargetUserId.Should().Be("u2");
        recent[1].TargetUserId.Should().Be("u1");
    }

    [Fact]
    public async Task GetRecentAsync_RespectsCountLimit()
    {
        await using var db = CreateDb();
        var repo = new AdminLogRepository(db);
        for (var i = 0; i < 5; i++)
            await repo.LogAsync(new AdminActionLog { AdminUserId = "a", TargetUserId = $"u{i}", Action = "Suspend" });

        var recent = await repo.GetRecentAsync(count: 3);

        recent.Should().HaveCount(3);
    }
}
