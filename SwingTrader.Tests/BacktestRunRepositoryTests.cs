using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

// The latest-completed-run lookup that lets the Strategy Lab restore an
// optimizer result after a page refresh: must pick the newest COMPLETED run
// of the requested mode only - never a failed run, never another mode's,
// never another account's.
public class BacktestRunRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static BacktestRun Run(int accountId, string mode, string status, DateTime? completedAt, string? resultJson = "{}") =>
        new()
        {
            AccountId = accountId,
            Status = status,
            RequestJson = $"{{\"Weights\":{{}},\"Mode\":\"{mode}\"}}",
            ResultJson = resultJson,
            CompletedAt = completedAt,
        };

    [Fact]
    public async Task GetLatestByModeAsync_PicksNewestCompletedOfThatModeOnly()
    {
        await using var db = CreateDb();
        var repo = new BacktestRunRepository(db);
        var t0 = new DateTime(2026, 7, 11, 10, 0, 0);

        await repo.AddAsync(Run(1, "sweep", "Completed", t0));                      // older sweep
        var newest = await repo.AddAsync(Run(1, "sweep", "Completed", t0.AddHours(2)));
        await repo.AddAsync(Run(1, "sweep", "Failed", t0.AddHours(3), null));       // newer but failed
        await repo.AddAsync(Run(1, "ab", "Completed", t0.AddHours(4)));             // newer but wrong mode
        await repo.AddAsync(Run(2, "sweep", "Completed", t0.AddHours(5)));          // newer but wrong account

        var latest = await repo.GetLatestByModeAsync(1, "sweep");

        latest.Should().NotBeNull();
        latest!.Id.Should().Be(newest.Id);
    }

    [Fact]
    public async Task GetLatestByModeAsync_InFlightRunBeatsAnyCompletedOne()
    {
        // Reattach-over-restore: with a sweep still running server-side, the
        // tab load must resume its poll rather than show an older result.
        await using var db = CreateDb();
        var repo = new BacktestRunRepository(db);
        var t0 = new DateTime(2026, 7, 11, 10, 0, 0);

        await repo.AddAsync(Run(1, "sweep", "Completed", t0.AddHours(5)));
        var running = await repo.AddAsync(Run(1, "sweep", "Running", null, null));
        await repo.AddAsync(Run(1, "ab", "Running", null, null)); // wrong mode

        var latest = await repo.GetLatestByModeAsync(1, "sweep");

        latest.Should().NotBeNull();
        latest!.Id.Should().Be(running.Id);
    }

    [Fact]
    public async Task GetLatestByModeAsync_OnlyFailedRuns_ReturnsNull()
    {
        await using var db = CreateDb();
        var repo = new BacktestRunRepository(db);
        await repo.AddAsync(Run(1, "sweep", "Failed", null, null));

        (await repo.GetLatestByModeAsync(1, "sweep")).Should().BeNull();
    }
}
