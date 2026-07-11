using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Constants;
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

        // Enable 9 more (10 total, at cap) - each on its own disabled watchlist
        // so they never approach the total-symbol union cap.
        for (var i = 0; i < WatchlistLimits.MaxEnabledWatchlists - 1; i++)
        {
            var w = await repo.CreateWatchlistAsync(1, $"W{i}", WatchlistType.Manual, null);
            await repo.EnableWatchlistAsync(1, w.Id);
        }

        var over = await repo.CreateWatchlistAsync(1, "Over", WatchlistType.Manual, null);
        var act = async () => await repo.EnableWatchlistAsync(1, over.Id);

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
    public async Task EnableWatchlistAsync_UnionOverTotalSymbolCap_Throws()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1); // default watchlist: 10 symbols, already enabled

        var w2 = await repo.CreateWatchlistAsync(1, "W2", WatchlistType.Manual, null);
        for (var i = 0; i < 50; i++)
            await repo.AddSymbolAsync(1, w2.Id, $"SYM{i}", "Co", "Sector");
        await repo.EnableWatchlistAsync(1, w2.Id); // union now 60 - fine

        var w3 = await repo.CreateWatchlistAsync(1, "W3", WatchlistType.Manual, null);
        for (var i = 50; i < 100; i++)
            await repo.AddSymbolAsync(1, w3.Id, $"SYM{i}", "Co", "Sector");

        // Union would be 10 + 50 + 50 = 110, over the 100 cap.
        var act = async () => await repo.EnableWatchlistAsync(1, w3.Id);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*100*");
    }

    [Fact]
    public async Task EnableWatchlistAsync_OverlappingSymbolsDedupedForUnionCap_DoesNotThrow()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1); // 10 symbols, already enabled

        var w2 = await repo.CreateWatchlistAsync(1, "W2", WatchlistType.Manual, null);
        for (var i = 0; i < 50; i++)
            await repo.AddSymbolAsync(1, w2.Id, $"SYM{i}", "Co", "Sector");
        await repo.EnableWatchlistAsync(1, w2.Id); // union now 60

        var w3 = await repo.CreateWatchlistAsync(1, "W3", WatchlistType.Manual, null);
        // Fully overlapping with w2's symbols - union should stay at 60, not 110.
        for (var i = 0; i < 40; i++)
            await repo.AddSymbolAsync(1, w3.Id, $"SYM{i}", "Co", "Sector");

        var act = async () => await repo.EnableWatchlistAsync(1, w3.Id);

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
    public async Task AddSymbolAsync_ToEnabledWatchlist_OverTotalUnionCap_Throws()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1); // 10 symbols, enabled

        var w2 = await repo.CreateWatchlistAsync(1, "W2", WatchlistType.Manual, null);
        for (var i = 0; i < 50; i++)
            await repo.AddSymbolAsync(1, w2.Id, $"SYM{i}", "Co", "Sector"); // disabled, so no cap check yet
        await repo.EnableWatchlistAsync(1, w2.Id); // union now 60

        var w3 = await repo.CreateWatchlistAsync(1, "W3", WatchlistType.Manual, null);
        for (var i = 50; i < 89; i++)
            await repo.AddSymbolAsync(1, w3.Id, $"SYM{i}", "Co", "Sector"); // still disabled
        await repo.EnableWatchlistAsync(1, w3.Id); // union now 60 + 39 = 99

        await repo.AddSymbolAsync(1, w3.Id, "SYM99", "Co", "Sector"); // union now exactly 100 - fine

        var act = async () => await repo.AddSymbolAsync(1, w3.Id, "NEWSYM", "Co", "Sector");

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*100*");
    }

    [Fact]
    public async Task EnableWatchlistAsync_CountsForcedItemsOnStillDisabledWatchlists()
    {
        // Regression: EnableWatchlistAsync's cap check must count force-listed
        // items sitting on OTHER disabled watchlists - GetAllEnabledSymbolsAsync
        // (what Research actually queries) already includes them, so a check
        // that ignores ForceIntoFinalList silently undercounts the true union.
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1); // 10 symbols, enabled

        var forcedA = await repo.CreateWatchlistAsync(1, "IdeasA", WatchlistType.Manual, null);
        var forcedB = await repo.CreateWatchlistAsync(1, "IdeasB", WatchlistType.Manual, null);
        for (var i = 0; i < 89; i++)
        {
            var source = i < 50 ? forcedA : forcedB;
            await repo.AddSymbolAsync(1, source.Id, $"FORCED{i}", "Co", "Sector");
            await repo.SetForceIntoFinalListAsync(1, source.Id, $"FORCED{i}", true);
        }
        // Union is now 10 + 89 = 99, entirely via forced items on disabled watchlists.

        var w2 = await repo.CreateWatchlistAsync(1, "W2", WatchlistType.Manual, null);
        await repo.AddSymbolAsync(1, w2.Id, "OVERFLOW1", "Co", "Sector");
        await repo.AddSymbolAsync(1, w2.Id, "OVERFLOW2", "Co", "Sector"); // disabled, no check yet

        var act = async () => await repo.EnableWatchlistAsync(1, w2.Id); // would push union to 101

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*100*");
    }

    [Fact]
    public async Task AddSymbolAsync_SymbolAlreadyInEnabledUnion_DoesNotCountTwice()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1); // 10 symbols including AAPL, enabled

        var w2 = await repo.CreateWatchlistAsync(1, "W2", WatchlistType.Manual, null);
        await repo.EnableWatchlistAsync(1, w2.Id);

        // AAPL is already in the enabled union via the default watchlist - adding
        // it to w2 (also enabled) doesn't grow the union, so it should never be
        // blocked by the total cap regardless of how close to 100 things are.
        var act = async () => await repo.AddSymbolAsync(1, w2.Id, "AAPL", "Apple", "Tech");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AddSymbolAsync_ToDisabledWatchlist_IgnoresTotalUnionCap()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1); // 10 symbols, enabled

        var w2 = await repo.CreateWatchlistAsync(1, "W2", WatchlistType.Manual, null);
        for (var i = 0; i < 50; i++)
            await repo.AddSymbolAsync(1, w2.Id, $"SYM{i}", "Co", "Sector");
        await repo.EnableWatchlistAsync(1, w2.Id); // enabled union now 60

        // A disabled watchlist can hold symbols that would push the union past
        // 100 if it were counted - it doesn't affect what Research actually
        // scores until enabled, and EnableWatchlistAsync is the gate that
        // matters at that point, not AddSymbolAsync.
        var disabled = await repo.CreateWatchlistAsync(1, "Disabled", WatchlistType.Manual, null);
        for (var i = 0; i < 49; i++)
            await repo.AddSymbolAsync(1, disabled.Id, $"XYZ{i}", "Co", "Sector");

        var act = async () => await repo.AddSymbolAsync(1, disabled.Id, "XYZ49", "Co", "Sector");

        await act.Should().NotThrowAsync();
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
    public async Task GetAllEnabledSymbolsAsync_IncludesForcedItemFromDisabledWatchlist()
    {
        // The whole point of ForceIntoFinalList: research a specific pick
        // without switching its entire (possibly disabled/idea) watchlist live.
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1);
        var disabled = await repo.CreateWatchlistAsync(1, "Ideas", WatchlistType.Manual, null);
        await repo.AddSymbolAsync(1, disabled.Id, "FORCEME", "Co", "Sector");
        await repo.SetForceIntoFinalListAsync(1, disabled.Id, "FORCEME", true);
        await repo.AddSymbolAsync(1, disabled.Id, "NOTFORCED", "Co", "Sector");

        var symbols = await repo.GetAllEnabledSymbolsAsync(1);

        symbols.Should().Contain(s => s.Symbol == "FORCEME");
        symbols.Should().NotContain(s => s.Symbol == "NOTFORCED"); // still excluded - disabled watchlist, not forced
    }

    [Fact]
    public async Task SetForceIntoFinalListAsync_CanBeToggledOff()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1);
        var disabled = await repo.CreateWatchlistAsync(1, "Ideas", WatchlistType.Manual, null);
        await repo.AddSymbolAsync(1, disabled.Id, "FORCEME", "Co", "Sector");
        await repo.SetForceIntoFinalListAsync(1, disabled.Id, "FORCEME", true);

        await repo.SetForceIntoFinalListAsync(1, disabled.Id, "FORCEME", false);

        var symbols = await repo.GetAllEnabledSymbolsAsync(1);
        symbols.Should().NotContain(s => s.Symbol == "FORCEME");
    }

    [Fact]
    public async Task SetForceIntoFinalListAsync_OverTotalUnionCap_Throws()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1); // 10 symbols, enabled

        var w2 = await repo.CreateWatchlistAsync(1, "W2", WatchlistType.Manual, null);
        for (var i = 0; i < 50; i++)
            await repo.AddSymbolAsync(1, w2.Id, $"SYM{i}", "Co", "Sector");
        await repo.EnableWatchlistAsync(1, w2.Id); // union now 60

        var w3 = await repo.CreateWatchlistAsync(1, "W3", WatchlistType.Manual, null);
        for (var i = 50; i < 90; i++)
            await repo.AddSymbolAsync(1, w3.Id, $"SYM{i}", "Co", "Sector");
        await repo.EnableWatchlistAsync(1, w3.Id); // union now exactly 100

        var disabled = await repo.CreateWatchlistAsync(1, "Ideas", WatchlistType.Manual, null);
        await repo.AddSymbolAsync(1, disabled.Id, "FORCEME", "Co", "Sector");

        var act = async () => await repo.SetForceIntoFinalListAsync(1, disabled.Id, "FORCEME", true);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*100*");

        var symbols = await repo.GetAllEnabledSymbolsAsync(1);
        symbols.Should().NotContain(s => s.Symbol == "FORCEME");
    }

    [Fact]
    public async Task SetForceIntoFinalListAsync_AtUnionCap_StillFits_DoesNotThrow()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1); // 10 symbols, enabled

        var w2 = await repo.CreateWatchlistAsync(1, "W2", WatchlistType.Manual, null);
        var w3 = await repo.CreateWatchlistAsync(1, "W3", WatchlistType.Manual, null);
        for (var i = 0; i < 89; i++)
        {
            var target = i < 50 ? w2 : w3;
            await repo.AddSymbolAsync(1, target.Id, $"SYM{i}", "Co", "Sector");
        }
        await repo.EnableWatchlistAsync(1, w2.Id);
        await repo.EnableWatchlistAsync(1, w3.Id); // union now exactly 99

        var disabled = await repo.CreateWatchlistAsync(1, "Ideas", WatchlistType.Manual, null);
        await repo.AddSymbolAsync(1, disabled.Id, "FORCEME", "Co", "Sector");

        var act = async () => await repo.SetForceIntoFinalListAsync(1, disabled.Id, "FORCEME", true); // union now exactly 100

        await act.Should().NotThrowAsync();

        var symbols = await repo.GetAllEnabledSymbolsAsync(1);
        symbols.Should().Contain(s => s.Symbol == "FORCEME");
    }

    [Fact]
    public async Task SetForceIntoFinalListAsync_SymbolAlreadyInEnabledUnion_DoesNotCountTwiceOrThrow()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1); // 10 symbols including AAPL, enabled

        var w2 = await repo.CreateWatchlistAsync(1, "W2", WatchlistType.Manual, null);
        var w3 = await repo.CreateWatchlistAsync(1, "W3", WatchlistType.Manual, null);
        for (var i = 0; i < 89; i++)
        {
            var target = i < 50 ? w2 : w3;
            await repo.AddSymbolAsync(1, target.Id, $"SYM{i}", "Co", "Sector");
        }
        await repo.EnableWatchlistAsync(1, w2.Id);
        await repo.EnableWatchlistAsync(1, w3.Id); // union now exactly 99

        var disabled = await repo.CreateWatchlistAsync(1, "Ideas", WatchlistType.Manual, null);
        // AAPL is already in the enabled union via the default watchlist, so
        // force-listing it here from a disabled watchlist doesn't grow the
        // union - it must never be blocked by the total cap.
        await repo.AddSymbolAsync(1, disabled.Id, "AAPL", "Apple", "Tech");

        var act = async () => await repo.SetForceIntoFinalListAsync(1, disabled.Id, "AAPL", true);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SetForceIntoFinalListAsync_ItemOnAlreadyEnabledWatchlist_IgnoresUnionCap()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1); // 10 symbols, enabled

        var w2 = await repo.CreateWatchlistAsync(1, "W2", WatchlistType.Manual, null);
        var w3 = await repo.CreateWatchlistAsync(1, "W3", WatchlistType.Manual, null);
        for (var i = 0; i < 90; i++)
        {
            var target = i < 50 ? w2 : w3;
            await repo.AddSymbolAsync(1, target.Id, $"SYM{i}", "Co", "Sector");
        }
        await repo.EnableWatchlistAsync(1, w2.Id);
        await repo.EnableWatchlistAsync(1, w3.Id); // union now exactly 100

        // SYM0 is on w2, which is already enabled - it's already counted in the
        // union, so forcing it (a redundant no-op in terms of union growth)
        // must never be blocked by the cap even though the union is maxed out.
        var act = async () => await repo.SetForceIntoFinalListAsync(1, w2.Id, "SYM0", true);

        await act.Should().NotThrowAsync();
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

    [Fact]
    public async Task LegacyGetBySymbolAsync_IgnoresSameSymbolOnOtherWatchlists()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1);
        var manual = await repo.CreateWatchlistAsync(1, "Manual", WatchlistType.Manual, null);
        await repo.AddSymbolAsync(1, manual.Id, "ZZZZ", "Zzz Corp", "Other");

        var found = await repo.GetBySymbolAsync(1, "ZZZZ");

        found.Should().BeNull();
    }

    [Fact]
    public async Task LegacyGetBySymbolAsync_FindsSymbolOnDefaultWatchlist()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1);

        var found = await repo.GetBySymbolAsync(1, "aapl");

        found.Should().NotBeNull();
        found!.Symbol.Should().Be("AAPL");
    }

    [Fact]
    public async Task RemoveSymbolAsync_SoftDeletesItem()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        var created = await repo.CreateWatchlistAsync(1, "W", WatchlistType.Manual, null);
        await repo.AddSymbolAsync(1, created.Id, "AAPL", "Apple", "Tech");

        await repo.RemoveSymbolAsync(1, created.Id, "AAPL");

        // GetSymbolsAsync applies the IsActive query filter, so a removed
        // item disappears from the normal view...
        var symbols = await repo.GetSymbolsAsync(1, created.Id);
        symbols.Should().BeEmpty();

        // ...but the row itself is soft-deleted, not hard-deleted.
        var raw = await db.WatchlistItems.IgnoreQueryFilters().SingleAsync(i => i.WatchlistId == created.Id);
        raw.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task DisableWatchlistAsync_TurnsOffEnabledFlag()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1);
        var defaultWatchlist = (await repo.GetAllWatchlistsAsync(1))[0];

        await repo.DisableWatchlistAsync(1, defaultWatchlist.Id);

        var reloaded = await repo.GetWatchlistAsync(1, defaultWatchlist.Id);
        reloaded!.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateWatchlistAsync_RenamesAndSetsDescription()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        var created = await repo.CreateWatchlistAsync(1, "Old Name", WatchlistType.Manual, null);

        await repo.UpdateWatchlistAsync(1, created.Id, "New Name", "New description", topMoversEnabled: true);

        var reloaded = await repo.GetWatchlistAsync(1, created.Id);
        reloaded!.Name.Should().Be("New Name");
        reloaded.Description.Should().Be("New description");
        reloaded.TopMoversEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task IsTopMoversEnabledAsync_DefaultsToFalse()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1);

        (await repo.IsTopMoversEnabledAsync(1)).Should().BeFalse();
    }

    [Fact]
    public async Task IsTopMoversEnabledAsync_ReflectsUpdateOnDefaultWatchlist()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1);
        var defaultWatchlist = (await repo.GetAllWatchlistsAsync(1))[0];

        await repo.UpdateWatchlistAsync(1, defaultWatchlist.Id, defaultWatchlist.Name, defaultWatchlist.Description, topMoversEnabled: true);

        (await repo.IsTopMoversEnabledAsync(1)).Should().BeTrue();
    }

    [Fact]
    public async Task IsTopMoversEnabledAsync_IgnoresNonDefaultWatchlist()
    {
        await using var db = CreateDb();
        var repo = new WatchlistRepository(db);
        await repo.SeedDefaultAsync(1);
        var manual = await repo.CreateWatchlistAsync(1, "Manual", WatchlistType.Manual, null);

        await repo.UpdateWatchlistAsync(1, manual.Id, manual.Name, manual.Description, topMoversEnabled: true);

        (await repo.IsTopMoversEnabledAsync(1)).Should().BeFalse();
    }
}
