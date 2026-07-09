using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class SystemChecklistRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task CompleteAsync_NoExistingRow_CreatesCompletedEntry()
    {
        await using var db = CreateDb();
        var repo = new SystemChecklistRepository(db);

        await repo.CompleteAsync(1, "AccountIdVerified", "looks good");

        var entry = db.SystemChecklists.Single();
        entry.CompletedAt.Should().NotBeNull();
        entry.Notes.Should().Be("looks good");
    }

    [Fact]
    public async Task CompleteAsync_CalledTwice_KeepsOriginalCompletionTimestamp()
    {
        await using var db = CreateDb();
        var repo = new SystemChecklistRepository(db);
        await repo.CompleteAsync(1, "AccountIdVerified", "first");
        var firstTimestamp = db.SystemChecklists.Single().CompletedAt;

        await repo.CompleteAsync(1, "AccountIdVerified", "second");

        db.SystemChecklists.Count().Should().Be(1);
        db.SystemChecklists.Single().CompletedAt.Should().Be(firstTimestamp);
    }
}
