using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class WatchlistRepositoryTests
{
    private static SwingTraderDbContext CreateDb(string? dbName = null) =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task SeedDefaultAsync_CreatesDefaultAiManagedWatchlistWithStarterSymbols()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);

        await repo.SeedDefaultAsync(1);

        var watchlists = await repo.GetAllWatchlistsAsync(1);
        watchlists.Should().HaveCount(1);
        watchlists[0].Type.Should().Be(WatchlistType.AiManaged);
        watchlists[0].IsDefault.Should().BeTrue();
        watchlists[0].IsEnabled.Should().BeTrue();
        watchlists[0].Items.Should().HaveCount(10);
    }

    [Fact]
    public async Task CreateWatchlistAsync_StartsDisabledAndNotDefault()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1);

        var created = await repo.CreateWatchlistAsync(1, "My Manual List", WatchlistType.Manual, "test");

        created.IsEnabled.Should().BeFalse();
        created.IsDefault.Should().BeFalse();
    }

    [Fact]
    public async Task EnableWatchlistAsync_AtCap_Throws()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1); // 1 enabled already (default)
        var w2 = await repo.CreateWatchlistAsync(1, "W2", WatchlistType.Manual, null);
        var w3 = await repo.CreateWatchlistAsync(1, "W3", WatchlistType.Manual, null);
        var w4 = await repo.CreateWatchlistAsync(1, "W4", WatchlistType.Manual, null);
        await repo.EnableWatchlistAsync(1, w2.Id);
        await repo.EnableWatchlistAsync(1, w3.Id); // now 3 enabled (default + w2 + w3)

        var act = async () => await repo.EnableWatchlistAsync(1, w4.Id);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task EnableWatchlistAsync_AlreadyEnabled_IsNoOpAndDoesNotCountTwice()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1);
        var defaultWatchlist = (await repo.GetAllWatchlistsAsync(1))[0];

        var act = async () => await repo.EnableWatchlistAsync(1, defaultWatchlist.Id);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteWatchlistAsync_DefaultWatchlist_Throws()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1);
        var defaultWatchlist = (await repo.GetAllWatchlistsAsync(1))[0];

        var act = async () => await repo.DeleteWatchlistAsync(1, defaultWatchlist.Id);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task DeleteWatchlistAsync_NonDefault_Succeeds()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1);
        var created = await repo.CreateWatchlistAsync(1, "Extra", WatchlistType.Manual, null);

        await repo.DeleteWatchlistAsync(1, created.Id);

        var watchlists = await repo.GetAllWatchlistsAsync(1);
        watchlists.Should().ContainSingle();
    }

    [Fact]
    public async Task SetDefaultWatchlistAsync_MovesDefaultFlagToTarget()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1);
        var created = await repo.CreateWatchlistAsync(1, "New Default", WatchlistType.Manual, null);

        await repo.SetDefaultWatchlistAsync(1, created.Id);

        var watchlists = await repo.GetAllWatchlistsAsync(1);
        watchlists.Single(w => w.Id == created.Id).IsDefault.Should().BeTrue();
        watchlists.Single(w => w.Id != created.Id).IsDefault.Should().BeFalse();
    }

    [Fact]
    public async Task AddSymbolAsync_AtCap_Throws()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        var created = await repo.CreateWatchlistAsync(1, "Full", WatchlistType.Manual, null);
        for (var i = 0; i < 50; i++)
            await repo.AddSymbolAsync(1, created.Id, $"SYM{i}", "Co", "Sector");

        var act = async () => await repo.AddSymbolAsync(1, created.Id, "OVER", "Co", "Sector");

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task AddSymbolAsync_DuplicateActiveSymbol_Throws()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        var created = await repo.CreateWatchlistAsync(1, "W", WatchlistType.Manual, null);
        await repo.AddSymbolAsync(1, created.Id, "AAPL", "Apple", "Tech");

        var act = async () => await repo.AddSymbolAsync(1, created.Id, "aapl", "Apple", "Tech");

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task AddSymbolAsync_ReactivatesPreviouslyRemovedSymbol()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        var created = await repo.CreateWatchlistAsync(1, "W", WatchlistType.Manual, null);
        await repo.AddSymbolAsync(1, created.Id, "AAPL", "Apple", "Tech");
        await repo.RemoveSymbolAsync(1, created.Id, "AAPL");

        var readded = await repo.AddSymbolAsync(1, created.Id, "AAPL", "Apple Inc.", "Technology");

        readded.IsActive.Should().BeTrue();
        readded.CompanyName.Should().Be("Apple Inc.");
    }

    [Fact]
    public async Task GetAllEnabledSymbolsAsync_DedupsAcrossEnabledWatchlists()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1); // enabled, has AAPL among starters
        var w2 = await repo.CreateWatchlistAsync(1, "W2", WatchlistType.Manual, null);
        await repo.EnableWatchlistAsync(1, w2.Id);
        await repo.AddSymbolAsync(1, w2.Id, "AAPL", "Apple", "Tech"); // overlaps with default
        await repo.AddSymbolAsync(1, w2.Id, "UNIQUE", "Unique Co", "Other");

        var symbols = await repo.GetAllEnabledSymbolsAsync(1);

        symbols.Count(s => s.Symbol == "AAPL").Should().Be(1);
        symbols.Should().Contain(s => s.Symbol == "UNIQUE");
    }

    [Fact]
    public async Task GetAllEnabledSymbolsAsync_ExcludesDisabledWatchlists()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1);
        var disabled = await repo.CreateWatchlistAsync(1, "Disabled", WatchlistType.Manual, null);
        await repo.AddSymbolAsync(1, disabled.Id, "SHOULDNOTAPPEAR", "Co", "Sector");

        var symbols = await repo.GetAllEnabledSymbolsAsync(1);

        symbols.Should().NotContain(s => s.Symbol == "SHOULDNOTAPPEAR");
    }

    [Fact]
    public async Task LegacyGetActiveAsync_OperatesAgainstDefaultWatchlist()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1);

        var active = await repo.GetActiveAsync(1);

        active.Should().HaveCount(10);
    }
}
