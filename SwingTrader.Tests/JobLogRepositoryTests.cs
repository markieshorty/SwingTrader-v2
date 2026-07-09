using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class JobLogRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static readonly DateOnly Date = new(2026, 7, 8);

    [Fact]
    public async Task CreateEnqueuedAsync_CreatesRowWithEnqueuedStatus()
    {
        await using var db = CreateDb();
        var repo = new JobLogRepository(db);

        var entry = await repo.CreateEnqueuedAsync(1, "Research", Date);

        entry.Status.Should().Be(JobStatus.Enqueued);
        entry.EnqueuedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task TryCreateEnqueuedAsync_FirstClaimWins_SecondReturnsFalse()
    {
        // The claim-first enqueue fix: the scheduler inserts the job-log row
        // BEFORE sending to Service Bus, so a concurrent scheduler execution
        // must get false here and skip its send - this is what prevents the
        // observed double-enqueue (two Execution messages in one tick).
        await using var db = CreateDb();
        var repo = new JobLogRepository(db);

        var first = await repo.TryCreateEnqueuedAsync(1, "Execution", Date);
        var second = await repo.TryCreateEnqueuedAsync(1, "Execution", Date);

        first.Should().BeTrue();
        second.Should().BeFalse();
        (await repo.FindAsync(1, "Execution", Date))!.Status.Should().Be(JobStatus.Enqueued);
    }

    [Fact]
    public async Task TryCreateEnqueuedAsync_DifferentDayOrType_BothSucceed()
    {
        await using var db = CreateDb();
        var repo = new JobLogRepository(db);

        (await repo.TryCreateEnqueuedAsync(1, "Execution", Date)).Should().BeTrue();
        (await repo.TryCreateEnqueuedAsync(1, "Execution", Date.AddDays(1))).Should().BeTrue();
        (await repo.TryCreateEnqueuedAsync(1, "Report", Date)).Should().BeTrue();
    }

    [Fact]
    public async Task FindAsync_UnknownCombination_ReturnsNull()
    {
        await using var db = CreateDb();
        var repo = new JobLogRepository(db);

        var result = await repo.FindAsync(1, "Research", Date);

        result.Should().BeNull();
    }

    [Fact]
    public async Task MarkProcessingAsync_IncrementsAttemptCountAndSetsStatus()
    {
        await using var db = CreateDb();
        var repo = new JobLogRepository(db);
        await repo.CreateEnqueuedAsync(1, "Research", Date);

        await repo.MarkProcessingAsync(1, "Research", Date);

        var entry = await repo.FindAsync(1, "Research", Date);
        entry!.Status.Should().Be(JobStatus.Processing);
        entry.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task MarkCompletedAsync_SetsStatusAndCompletedAt()
    {
        await using var db = CreateDb();
        var repo = new JobLogRepository(db);
        await repo.CreateEnqueuedAsync(1, "Research", Date);

        await repo.MarkCompletedAsync(1, "Research", Date);

        var entry = await repo.FindAsync(1, "Research", Date);
        entry!.Status.Should().Be(JobStatus.Completed);
        entry.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkFailedAsync_SetsStatusAndErrorMessage()
    {
        await using var db = CreateDb();
        var repo = new JobLogRepository(db);
        await repo.CreateEnqueuedAsync(1, "Research", Date);

        await repo.MarkFailedAsync(1, "Research", Date, "boom");

        var entry = await repo.FindAsync(1, "Research", Date);
        entry!.Status.Should().Be(JobStatus.Failed);
        entry.ErrorMessage.Should().Be("boom");
    }

    [Fact]
    public async Task MarkProcessingAsync_UnknownEntry_DoesNotThrow()
    {
        await using var db = CreateDb();
        var repo = new JobLogRepository(db);

        var act = async () => await repo.MarkProcessingAsync(1, "Research", Date);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteAsync_RemovesEntry()
    {
        await using var db = CreateDb();
        var repo = new JobLogRepository(db);
        await repo.CreateEnqueuedAsync(1, "Research", Date);

        await repo.DeleteAsync(1, "Research", Date);

        var entry = await repo.FindAsync(1, "Research", Date);
        entry.Should().BeNull();
    }
}
