using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class TierEvaluationRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task AddAsync_DefaultCreatedAt_IsPopulatedAndPersists()
    {
        await using var db = CreateDb();
        var repo = new TierEvaluationRepository(db);

        var added = await repo.AddAsync(new TierEvaluationRecord
        {
            AccountId = 1,
            TradingMode = TradingMode.Demo,
            CurrentTier = CapitalTier.Tier1,
            SuggestedTier = CapitalTier.Tier2,
        });

        added.CreatedAt.Should().NotBe(default);
        db.TierEvaluationRecords.Should().ContainSingle(r => r.Id == added.Id);
    }

    [Fact]
    public async Task AddAsync_ExplicitCreatedAt_IsPreserved()
    {
        await using var db = CreateDb();
        var repo = new TierEvaluationRepository(db);
        var explicitTime = new DateTime(2026, 6, 1);

        var added = await repo.AddAsync(new TierEvaluationRecord
        {
            AccountId = 1,
            TradingMode = TradingMode.Demo,
            CreatedAt = explicitTime,
        });

        added.CreatedAt.Should().Be(explicitTime);
    }
}
