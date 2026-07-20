using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using SwingTrader.Core.Interfaces;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class WorkerHeartbeatRepositoryTests
{
    private readonly IActivityLogRepository _activityLog = Substitute.For<IActivityLogRepository>();

    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task UpsertAsync_NoExistingRow_Inserts()
    {
        await using var db = CreateDb();
        var repo = new WorkerHeartbeatRepository(db, _activityLog);

        await repo.UpsertAsync(1, "ResearchWorker", "Success", "ran fine");

        var result = await repo.GetAsync(1, "ResearchWorker");
        result.Should().NotBeNull();
        result!.LastRunResult.Should().Be("Success");
    }

    [Fact]
    public async Task UpsertAsync_ExistingRow_UpdatesFields()
    {
        await using var db = CreateDb();
        var repo = new WorkerHeartbeatRepository(db, _activityLog);
        await repo.UpsertAsync(1, "ResearchWorker", "Success", "first run");

        await repo.UpsertAsync(1, "ResearchWorker", "Failed", "second run");

        db.WorkerHeartbeats.Count(w => w.WorkerName == "ResearchWorker").Should().Be(1);
        var result = await repo.GetAsync(1, "ResearchWorker");
        result!.LastRunResult.Should().Be("Failed");
        result.UpdatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task UpsertAsync_AlwaysLogsToActivityLog()
    {
        await using var db = CreateDb();
        var repo = new WorkerHeartbeatRepository(db, _activityLog);

        await repo.UpsertAsync(1, "ResearchWorker", "Success", "ran fine");

        await _activityLog.Received(1).LogAsync(1, "WorkerRun", "ResearchWorker", "Success", "ran fine", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_UnknownWorker_ReturnsNull()
    {
        await using var db = CreateDb();
        var repo = new WorkerHeartbeatRepository(db, _activityLog);

        var result = await repo.GetAsync(1, "Nonexistent");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_OrdersByWorkerName()
    {
        await using var db = CreateDb();
        var repo = new WorkerHeartbeatRepository(db, _activityLog);
        await repo.UpsertAsync(1, "ZWorker", "Success", null);
        await repo.UpsertAsync(1, "AWorker", "Success", null);

        var result = (await repo.GetAllAsync()).ToList();

        result.Should().HaveCount(2);
        result[0].WorkerName.Should().Be("AWorker");
    }

    [Fact]
    public async Task UpsertAsync_SameWorkerDifferentAccounts_KeepsSeparateRows()
    {
        await using var db = CreateDb();
        var repo = new WorkerHeartbeatRepository(db, _activityLog);

        await repo.UpsertAsync(1, "Watchlist", "Running", "1/5 screening");
        await repo.UpsertAsync(2, "Watchlist", "Running", "3/5 selecting");

        db.WorkerHeartbeats.Count(w => w.WorkerName == "Watchlist").Should().Be(2);
        (await repo.GetAsync(1, "Watchlist"))!.LastRunMessage.Should().Be("1/5 screening");
        (await repo.GetAsync(2, "Watchlist"))!.LastRunMessage.Should().Be("3/5 selecting");
    }
}
