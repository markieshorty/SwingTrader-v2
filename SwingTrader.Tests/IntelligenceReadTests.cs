using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

// Read-side queries behind the Intelligence page (docs/intelligence-page-plan).
public class IntelligenceReadTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task GetDeltaViewsSinceAsync_JoinsFilingIdentity_AndRespectsWindow()
    {
        await using var db = CreateDb();
        var repo = new FilingRepository(db);

        var filing = await repo.AddAsync(new Filing
        {
            Symbol = "AAPL",
            Cik = "0000320193",
            AccessionNumber = "0000320193-26-000001",
            FilingType = "10-Q",
            FiledAt = new DateOnly(2026, 7, 1),
            PrimaryDocument = "aapl-20260630.htm",
        });
        await repo.AddDeltaAsync(new FilingDelta
        {
            FilingId = filing.Id,
            Symbol = "AAPL",
            FiledAt = filing.FiledAt,
            Direction = -1m,
            Materiality = 0.6m,
            Delta = -0.6m,
        });

        var views = await repo.GetDeltaViewsSinceAsync(DateTime.UtcNow.AddDays(-1));
        views.Should().ContainSingle();
        views[0].Delta.Delta.Should().Be(-0.6m);
        views[0].FilingType.Should().Be("10-Q");
        views[0].Cik.Should().Be("0000320193");
        views[0].AccessionNumber.Should().Be("0000320193-26-000001");
        views[0].PrimaryDocument.Should().Be("aapl-20260630.htm");

        // Outside the window (ScoredAt is now, so a future 'since' excludes it)
        var none = await repo.GetDeltaViewsSinceAsync(DateTime.UtcNow.AddDays(1));
        none.Should().BeEmpty();
    }

    [Fact]
    public async Task CountFilingsSinceAsync_CountsAllStoredFilings_ChangedOrNot()
    {
        await using var db = CreateDb();
        var repo = new FilingRepository(db);

        await repo.AddAsync(new Filing { Symbol = "AAPL", AccessionNumber = "a-1", FilingType = "10-Q", FiledAt = new DateOnly(2026, 7, 1) });
        await repo.AddAsync(new Filing { Symbol = "MSFT", AccessionNumber = "m-1", FilingType = "10-K", FiledAt = new DateOnly(2026, 7, 2) });

        (await repo.CountFilingsSinceAsync(DateTime.UtcNow.AddDays(-1))).Should().Be(2);
        (await repo.CountFilingsSinceAsync(DateTime.UtcNow.AddDays(1))).Should().Be(0);
    }

    [Fact]
    public async Task GetLatestReasonsAsync_ReturnsLatestAddedReasonPerSymbol_ScopedToAccountAndSymbols()
    {
        await using var db = CreateDb();
        db.WatchlistHistory.AddRange(
            new WatchlistHistory { AccountId = 1, Symbol = "AAPL", Action = WatchlistAction.Added, Reason = "[Moat] old reason", WeekStarting = new DateOnly(2026, 6, 28) },
            new WatchlistHistory { AccountId = 1, Symbol = "AAPL", Action = WatchlistAction.Added, Reason = "[Moat] new reason", WeekStarting = new DateOnly(2026, 7, 5) },
            // Removed rows never surface as a pick rationale
            new WatchlistHistory { AccountId = 1, Symbol = "MSFT", Action = WatchlistAction.Removed, Reason = "rotated out", WeekStarting = new DateOnly(2026, 7, 5) },
            // Other account's history is invisible
            new WatchlistHistory { AccountId = 2, Symbol = "NVDA", Action = WatchlistAction.Added, Reason = "[Optionality] other account", WeekStarting = new DateOnly(2026, 7, 5) },
            // Symbol not requested
            new WatchlistHistory { AccountId = 1, Symbol = "TSLA", Action = WatchlistAction.Added, Reason = "[Contrarian] unrequested", WeekStarting = new DateOnly(2026, 7, 5) });
        await db.SaveChangesAsync();

        var repo = new WatchlistHistoryRepository(db);
        var reasons = await repo.GetLatestReasonsAsync(1, ["AAPL", "MSFT", "NVDA"]);

        reasons.Should().HaveCount(1);
        reasons["AAPL"].Should().Be("[Moat] new reason");
        // Case-insensitive lookup for UI convenience
        reasons.ContainsKey("aapl").Should().BeTrue();
    }
}
