using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SwingTrader.Agents.Watchlist;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class WatchlistUpdateServiceTests
{
    private readonly IWatchlistHistoryRepository _history = Substitute.For<IWatchlistHistoryRepository>();
    private readonly ITradeRepository _trades = Substitute.For<ITradeRepository>();

    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task UpdateAsync_OnlyTouchesDefaultAiManagedWatchlist_LeavesManualWatchlistUntouched()
    {
        await using var db = CreateDb();
        var watchlistRepo = new WatchlistRepository(db);
        await watchlistRepo.SeedDefaultAsync(1); // default AiManaged watchlist w/ 10 starters
        var manual = await watchlistRepo.CreateWatchlistAsync(1, "My Manual List", WatchlistType.Manual, null);
        await watchlistRepo.AddSymbolAsync(1, manual.Id, "ZZZZ", "Zzz Corp", "Other");

        _trades.GetOpenTradesAsync(1).Returns([]);
        var sut = new WatchlistUpdateService(watchlistRepo, _history, _trades, NullLogger<WatchlistUpdateService>.Instance);

        // Replace the entire AI-managed selection with a single new symbol.
        await sut.UpdateAsync(1, [new WatchlistSelection("NEWSYM", "New Co", "Tech", "Selected by agent")]);

        var manualSymbols = await watchlistRepo.GetSymbolsAsync(1, manual.Id);
        manualSymbols.Should().ContainSingle(s => s.Symbol == "ZZZZ" && s.IsActive);

        var defaultActive = await watchlistRepo.GetActiveAsync(1);
        defaultActive.Should().ContainSingle(s => s.Symbol == "NEWSYM");
    }

    [Fact]
    public async Task UpdateAsync_ProtectsOpenTradeSymbolsFromRemoval()
    {
        await using var db = CreateDb();
        var watchlistRepo = new WatchlistRepository(db);
        await watchlistRepo.SeedDefaultAsync(1); // includes AAPL among starters

        _trades.GetOpenTradesAsync(1).Returns([new Trade { AccountId = 1, Symbol = "AAPL" }]);
        var sut = new WatchlistUpdateService(watchlistRepo, _history, _trades, NullLogger<WatchlistUpdateService>.Instance);

        // New selection doesn't include AAPL, but an open trade holds it.
        await sut.UpdateAsync(1, [new WatchlistSelection("MSFT", "Microsoft Corporation", "Technology", "kept")]);

        var active = await watchlistRepo.GetActiveAsync(1);
        active.Should().Contain(s => s.Symbol == "AAPL");
    }

    [Fact]
    public async Task UpdateAsync_SelectionsBeyondCapacity_AreSkippedNotApplied()
    {
        await using var db = CreateDb();
        var watchlistRepo = new WatchlistRepository(db);
        await watchlistRepo.SeedDefaultAsync(1); // default AiManaged watchlist w/ 10 starters

        // Two other enabled watchlists near their own 50-symbol cap, leaving
        // only 2 spots free in the 100-symbol union (10 default starters + 44
        // + 44 = 98) before the AI-managed refresh below runs.
        var otherA = await watchlistRepo.CreateWatchlistAsync(1, "Manual A", WatchlistType.Manual, null);
        var otherB = await watchlistRepo.CreateWatchlistAsync(1, "Manual B", WatchlistType.Manual, null);
        await watchlistRepo.EnableWatchlistAsync(1, otherA.Id);
        await watchlistRepo.EnableWatchlistAsync(1, otherB.Id);
        for (var i = 0; i < 44; i++)
        {
            await watchlistRepo.AddSymbolAsync(1, otherA.Id, $"AA{i}", $"A {i}", "Other");
            await watchlistRepo.AddSymbolAsync(1, otherB.Id, $"BB{i}", $"B {i}", "Other");
        }

        _trades.GetOpenTradesAsync(1).Returns([]);
        var sut = new WatchlistUpdateService(watchlistRepo, _history, _trades, NullLogger<WatchlistUpdateService>.Instance);

        // Keeps all 10 existing starters (so nothing is removed to free space)
        // and adds 10 brand-new symbols on top - only 2 spots remain in the
        // union (98 already used), so 8 of the 10 new picks should be skipped
        // rather than pushing the union past 100.
        var starters = (await watchlistRepo.GetActiveAsync(1))
            .Select(w => new WatchlistSelection(w.Symbol, w.CompanyName, w.Sector, "kept"));
        var newPicks = Enumerable.Range(0, 10)
            .Select(i => new WatchlistSelection($"NEW{i}", $"New {i}", "Tech", "Selected by agent"));
        var selections = starters.Concat(newPicks).ToList();

        var result = await sut.UpdateAsync(1, selections);

        var unionCount = (await watchlistRepo.GetAllEnabledSymbolsAsync(1)).Count;
        unionCount.Should().Be(100);
        result.Added.Should().Be(2);
        result.SkippedForCapacity.Should().HaveCount(8);
    }
}
