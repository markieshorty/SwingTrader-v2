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
}
