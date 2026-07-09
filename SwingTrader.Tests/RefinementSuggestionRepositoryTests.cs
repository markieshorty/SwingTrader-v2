using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class RefinementSuggestionRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task AddAsync_DefaultCreatedAt_IsPopulated()
    {
        await using var db = CreateDb();
        var repo = new RefinementSuggestionRepository(db);

        var added = await repo.AddAsync(new RefinementSuggestion { AccountId = 1, TradingMode = TradingMode.Demo });

        added.CreatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task GetLatestAsync_ReturnsMostRecentByGeneratedAt()
    {
        await using var db = CreateDb();
        var repo = new RefinementSuggestionRepository(db);
        await repo.AddAsync(new RefinementSuggestion { AccountId = 1, TradingMode = TradingMode.Demo, GeneratedAt = new DateTime(2026, 7, 1) });
        var latest = await repo.AddAsync(new RefinementSuggestion { AccountId = 1, TradingMode = TradingMode.Demo, GeneratedAt = new DateTime(2026, 7, 5) });

        var result = await repo.GetLatestAsync(1, TradingMode.Demo);

        result!.Id.Should().Be(latest.Id);
    }

    [Fact]
    public async Task GetByIdAsync_WrongAccount_ReturnsNull()
    {
        await using var db = CreateDb();
        var repo = new RefinementSuggestionRepository(db);
        var added = await repo.AddAsync(new RefinementSuggestion { AccountId = 1, TradingMode = TradingMode.Demo });

        var result = await repo.GetByIdAsync(2, added.Id);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetHistoryAsync_RespectsCountAndOrder()
    {
        await using var db = CreateDb();
        var repo = new RefinementSuggestionRepository(db);
        await repo.AddAsync(new RefinementSuggestion { AccountId = 1, TradingMode = TradingMode.Demo, GeneratedAt = new DateTime(2026, 7, 1) });
        await repo.AddAsync(new RefinementSuggestion { AccountId = 1, TradingMode = TradingMode.Demo, GeneratedAt = new DateTime(2026, 7, 2) });
        await repo.AddAsync(new RefinementSuggestion { AccountId = 1, TradingMode = TradingMode.Demo, GeneratedAt = new DateTime(2026, 7, 3) });

        var result = (await repo.GetHistoryAsync(1, TradingMode.Demo, count: 2)).ToList();

        result.Should().HaveCount(2);
        result[0].GeneratedAt.Should().Be(new DateTime(2026, 7, 3));
    }

    [Fact]
    public async Task UpdateAsync_PersistsChangesAndSetsUpdatedAt()
    {
        await using var db = CreateDb();
        var repo = new RefinementSuggestionRepository(db);
        var suggestion = await repo.AddAsync(new RefinementSuggestion { AccountId = 1, TradingMode = TradingMode.Demo, Status = RefinementStatus.Pending });

        suggestion.Status = RefinementStatus.Applied;
        await repo.UpdateAsync(suggestion);

        var reloaded = await repo.GetByIdAsync(1, suggestion.Id);
        reloaded!.Status.Should().Be(RefinementStatus.Applied);
        reloaded.UpdatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task SupersedeAllPendingAsync_MarksOnlyPendingRowsSuperseded()
    {
        await using var db = CreateDb();
        var repo = new RefinementSuggestionRepository(db);
        var pending1 = await repo.AddAsync(new RefinementSuggestion { AccountId = 1, TradingMode = TradingMode.Demo, Status = RefinementStatus.Pending });
        var pending2 = await repo.AddAsync(new RefinementSuggestion { AccountId = 1, TradingMode = TradingMode.Demo, Status = RefinementStatus.Pending });
        var applied = await repo.AddAsync(new RefinementSuggestion { AccountId = 1, TradingMode = TradingMode.Demo, Status = RefinementStatus.Applied });

        await repo.SupersedeAllPendingAsync(1, TradingMode.Demo);

        (await repo.GetByIdAsync(1, pending1.Id))!.Status.Should().Be(RefinementStatus.Superseded);
        (await repo.GetByIdAsync(1, pending2.Id))!.Status.Should().Be(RefinementStatus.Superseded);
        (await repo.GetByIdAsync(1, applied.Id))!.Status.Should().Be(RefinementStatus.Applied);
    }

    [Fact]
    public async Task SupersedeAllPendingAsync_NoPendingRows_DoesNotThrow()
    {
        await using var db = CreateDb();
        var repo = new RefinementSuggestionRepository(db);

        var act = async () => await repo.SupersedeAllPendingAsync(1, TradingMode.Demo);

        await act.Should().NotThrowAsync();
    }
}
