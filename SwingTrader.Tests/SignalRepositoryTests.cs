using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

// UpsertAsync is how Research writes a fresh score for a symbol that
// already has a signal row today (e.g. the earnings-gate short-circuit, or
// a long-running Research pass reprocessing a symbol later the same day).
// It must never clobber WasExecuted - confirmed live that it did (WDAY
// got bought twice the same day because a mid-run Research rescore reset
// WasExecuted back to false after ExecutionService had already set it true).
public class SignalRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task UpsertAsync_ExistingSignalAlreadyExecuted_PreservesWasExecutedTrue()
    {
        await using var db = CreateDb();
        var repo = new SignalRepository(db);
        var today = new DateOnly(2026, 7, 8);
        var original = await repo.AddAsync(new StockSignal
        {
            AccountId = 1, Symbol = "WDAY", SignalDate = today,
            Recommendation = Recommendation.Buy, WasExecuted = true,
        });

        // Research rescoring the same symbol later the same day always
        // constructs a brand-new StockSignal, which defaults WasExecuted
        // to false - this must not overwrite the existing true value.
        await repo.UpsertAsync(new StockSignal
        {
            AccountId = 1, Symbol = "WDAY", SignalDate = today,
            Recommendation = Recommendation.Hold, ConvictionScore = 3.0m, WasExecuted = false,
        });

        var updated = await repo.GetByIdAsync(1, original.Id);
        updated!.WasExecuted.Should().BeTrue();
        // ...while the scoring fields DO refresh - a (midday) rescore must
        // actually update the signal, not just leave the row untouched.
        updated.Recommendation.Should().Be(Recommendation.Hold);
        updated.ConvictionScore.Should().Be(3.0m);
    }

    [Fact]
    public async Task UpsertAsync_NewSignal_DefaultsWasExecutedFalse()
    {
        await using var db = CreateDb();
        var repo = new SignalRepository(db);
        var today = new DateOnly(2026, 7, 8);

        var result = await repo.UpsertAsync(new StockSignal
        {
            AccountId = 1, Symbol = "NEW", SignalDate = today,
            Recommendation = Recommendation.Buy,
        });

        result.WasExecuted.Should().BeFalse();
    }
}
