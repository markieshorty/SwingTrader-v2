using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Constants;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class WatchlistRepository(SwingTraderDbContext db) : IWatchlistRepository
{
    // Small starter list for brand-new accounts, matching the 'system'
    // account's original seed data. The WatchlistAgent's dynamic universe
    // (Finnhub index constituents) replaces this once that's ported -
    // this is just enough for a new account to not start completely empty.
    private static readonly (string Symbol, string Company, string Sector)[] StarterSymbols =
    [
        ("AAPL", "Apple Inc.", "Technology"),
        ("MSFT", "Microsoft Corporation", "Technology"),
        ("NVDA", "NVIDIA Corporation", "Technology"),
        ("GOOGL", "Alphabet Inc.", "Technology"),
        ("AMZN", "Amazon.com Inc.", "Consumer"),
        ("JNJ", "Johnson & Johnson", "Healthcare"),
        ("JPM", "JPMorgan Chase & Co.", "Finance"),
        ("V", "Visa Inc.", "Finance"),
        ("WMT", "Walmart Inc.", "Consumer"),
        ("HD", "The Home Depot", "Consumer"),
    ];

    public async Task SeedDefaultAsync(int accountId, CancellationToken ct = default)
    {
        var alreadySeeded = await db.Watchlists.AnyAsync(w => w.AccountId == accountId, ct);
        if (alreadySeeded) return;

        var now = DateTime.UtcNow;
        var watchlist = new Watchlist
        {
            AccountId = accountId,
            Name = "AI Picks",
            Type = WatchlistType.AiManaged,
            IsEnabled = true,
            IsDefault = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Watchlists.Add(watchlist);
        await db.SaveChangesAsync(ct);

        var items = StarterSymbols.Select(s => new WatchlistItem
        {
            AccountId = accountId,
            WatchlistId = watchlist.Id,
            Symbol = s.Symbol,
            CompanyName = s.Company,
            Sector = s.Sector,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        db.WatchlistItems.AddRange(items);
        await db.SaveChangesAsync(ct);
    }

    private Task<Watchlist?> GetDefaultWatchlistAsync(int accountId, CancellationToken ct = default) =>
        db.Watchlists.FirstOrDefaultAsync(w => w.AccountId == accountId && w.Type == WatchlistType.AiManaged && w.IsDefault, ct);

    public Task<WatchlistItem?> GetByIdAsync(int accountId, int id) =>
        db.WatchlistItems.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.AccountId == accountId && x.Id == id);

    public async Task<IEnumerable<WatchlistItem>> GetAllAsync(int accountId) =>
        await db.WatchlistItems.IgnoreQueryFilters()
            .Where(x => x.AccountId == accountId)
            .ToListAsync();

    public async Task<IEnumerable<WatchlistItem>> GetActiveAsync(int accountId)
    {
        var defaultWatchlist = await GetDefaultWatchlistAsync(accountId);
        if (defaultWatchlist is null) return [];

        return await db.WatchlistItems
            .Where(x => x.AccountId == accountId && x.WatchlistId == defaultWatchlist.Id)
            .ToListAsync();
    }

    public async Task<WatchlistItem?> GetBySymbolAsync(int accountId, string symbol)
    {
        var defaultWatchlist = await GetDefaultWatchlistAsync(accountId);
        if (defaultWatchlist is null) return null;

        return await db.WatchlistItems.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.AccountId == accountId && x.WatchlistId == defaultWatchlist.Id && x.Symbol == symbol.ToUpperInvariant());
    }

    public async Task<WatchlistItem> AddAsync(WatchlistItem item)
    {
        item.Symbol = item.Symbol.ToUpperInvariant();
        if (item.WatchlistId == 0)
        {
            var defaultWatchlist = await GetDefaultWatchlistAsync(item.AccountId)
                ?? throw new InvalidOperationException($"No default watchlist found for account {item.AccountId}.");
            item.WatchlistId = defaultWatchlist.Id;
        }
        db.WatchlistItems.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    public async Task UpdateAsync(WatchlistItem item)
    {
        item.UpdatedAt = DateTime.UtcNow;
        db.WatchlistItems.Update(item);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int accountId, int id)
    {
        var item = await db.WatchlistItems.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.AccountId == accountId && x.Id == id);
        if (item is not null)
        {
            item.IsActive = false;
            item.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    public async Task<List<Watchlist>> GetAllWatchlistsAsync(int accountId, CancellationToken ct = default) =>
        await db.Watchlists
            .Include(w => w.Items.Where(i => i.IsActive))
            .Where(w => w.AccountId == accountId)
            .ToListAsync(ct);

    public Task<Watchlist?> GetWatchlistAsync(int accountId, int watchlistId, CancellationToken ct = default) =>
        db.Watchlists
            .Include(w => w.Items.Where(i => i.IsActive))
            .FirstOrDefaultAsync(w => w.AccountId == accountId && w.Id == watchlistId, ct);

    public async Task<List<WatchlistItem>> GetAllEnabledSymbolsAsync(int accountId, CancellationToken ct = default)
    {
        // ForceIntoFinalList items are included even from a disabled
        // watchlist - that's the whole point of the flag: research a
        // specific pick without switching its entire (possibly manual/idea)
        // watchlist live.
        var items = await db.WatchlistItems
            .Where(i => i.AccountId == accountId && (i.Watchlist!.IsEnabled || i.ForceIntoFinalList))
            .ToListAsync(ct);

        // Dedup by symbol - a symbol on multiple enabled watchlists is
        // researched once. Arbitrary-but-stable tie-break on the lowest item Id.
        return items
            .GroupBy(i => i.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(i => i.Id).First())
            .ToList();
    }

    public async Task<List<string>> GetActiveSymbolsAcrossAccountsAsync(CancellationToken ct = default)
    {
        // No AccountId filter - this is the scope of platform-level jobs
        // (FilingSync) maintaining shared data for whatever anyone watches.
        // The IsActive global query filter still applies.
        var symbols = await db.WatchlistItems
            .Where(i => i.Watchlist!.IsEnabled || i.ForceIntoFinalList)
            .Select(i => i.Symbol)
            .ToListAsync(ct);
        return symbols
            .Select(s => s.ToUpperInvariant())
            .Distinct()
            .OrderBy(s => s)
            .ToList();
    }

    public async Task<Watchlist> CreateWatchlistAsync(int accountId, string name, WatchlistType type, string? description, CancellationToken ct = default)
    {
        // The AI-managed and Qualitative lists are system-owned; the only list a
        // user creates is a single CUSTOM manual one. Reject anything else so the
        // three-list model holds regardless of what the caller sends.
        if (type != WatchlistType.Manual)
            throw new ValidationException("Only a manual (custom) watchlist can be created.");

        var customCount = await db.Watchlists.CountAsync(
            w => w.AccountId == accountId && w.Type == WatchlistType.Manual, ct);
        if (customCount >= WatchlistLimits.MaxCustomWatchlists)
            throw new ValidationException(
                $"You already have a custom watchlist. At most {WatchlistLimits.MaxCustomWatchlists} is allowed — " +
                "edit or delete the existing one instead.");

        var now = DateTime.UtcNow;
        var watchlist = new Watchlist
        {
            AccountId = accountId,
            Name = name,
            Type = type,
            Description = description,
            IsEnabled = false,
            IsDefault = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Watchlists.Add(watchlist);
        await db.SaveChangesAsync(ct);
        return watchlist;
    }

    public async Task<Watchlist> EnsureSystemWatchlistAsync(int accountId, WatchlistType type, string name, string? description, bool enabled = false, CancellationToken ct = default)
    {
        if (type == WatchlistType.Manual)
            throw new ValidationException("Manual lists go through CreateWatchlistAsync.");

        var existing = await db.Watchlists.FirstOrDefaultAsync(w => w.AccountId == accountId && w.Type == type, ct);
        if (existing is not null) return existing;

        var now = DateTime.UtcNow;
        var watchlist = new Watchlist
        {
            AccountId = accountId,
            Name = name,
            Type = type,
            Description = description,
            IsEnabled = enabled, // caller decides; qualitative arrives enabled since 20 Jul 2026
            IsDefault = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Watchlists.Add(watchlist);
        await db.SaveChangesAsync(ct);
        return watchlist;
    }

    public async Task UpdateWatchlistAsync(int accountId, int watchlistId, string name, string? description, bool topMoversEnabled, CancellationToken ct = default)
    {
        var watchlist = await db.Watchlists.FirstOrDefaultAsync(w => w.AccountId == accountId && w.Id == watchlistId, ct)
            ?? throw new InvalidOperationException($"Watchlist {watchlistId} not found for account {accountId}.");

        watchlist.Name = name;
        watchlist.Description = description;
        watchlist.TopMoversEnabled = topMoversEnabled;
        watchlist.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> IsTopMoversEnabledAsync(int accountId, CancellationToken ct = default)
    {
        var defaultWatchlist = await db.Watchlists.FirstOrDefaultAsync(
            w => w.AccountId == accountId && w.Type == WatchlistType.AiManaged && w.IsDefault, ct);
        return defaultWatchlist?.TopMoversEnabled ?? false;
    }

    public async Task EnableWatchlistAsync(int accountId, int watchlistId, CancellationToken ct = default)
    {
        var watchlist = await db.Watchlists
            .Include(w => w.Items.Where(i => i.IsActive))
            .FirstOrDefaultAsync(w => w.AccountId == accountId && w.Id == watchlistId, ct)
            ?? throw new InvalidOperationException($"Watchlist {watchlistId} not found for account {accountId}.");

        if (watchlist.IsEnabled) return;

        var enabledCount = await db.Watchlists.CountAsync(w => w.AccountId == accountId && w.IsEnabled, ct);
        if (enabledCount >= WatchlistLimits.MaxEnabledWatchlists)
            throw new ValidationException($"At most {WatchlistLimits.MaxEnabledWatchlists} watchlists can be enabled at once.");

        // No shared union cap any more: each list is independently size-capped
        // (MaxSymbolsPerWatchlist), and the fixed three-list model bounds the
        // total, so enabling a list can't blow a global budget.
        watchlist.IsEnabled = true;
        watchlist.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task DisableWatchlistAsync(int accountId, int watchlistId, CancellationToken ct = default)
    {
        var watchlist = await db.Watchlists.FirstOrDefaultAsync(w => w.AccountId == accountId && w.Id == watchlistId, ct)
            ?? throw new InvalidOperationException($"Watchlist {watchlistId} not found for account {accountId}.");

        watchlist.IsEnabled = false;
        watchlist.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteWatchlistAsync(int accountId, int watchlistId, CancellationToken ct = default)
    {
        var watchlist = await db.Watchlists.FirstOrDefaultAsync(w => w.AccountId == accountId && w.Id == watchlistId, ct)
            ?? throw new InvalidOperationException($"Watchlist {watchlistId} not found for account {accountId}.");

        // Only the user's custom (manual) list is deletable. The AI-managed and
        // Claude Qualitative lists are system-owned - they can be enabled or
        // disabled, never removed.
        if (watchlist.Type != WatchlistType.Manual)
            throw new ValidationException("Only your custom watchlist can be deleted — the AI-managed and Qualitative lists can only be disabled.");

        if (watchlist.IsDefault)
            throw new ValidationException("The default watchlist can't be deleted - set another watchlist as default first.");

        db.Watchlists.Remove(watchlist); // cascades to its WatchlistItems
        await db.SaveChangesAsync(ct);
    }

    public async Task SetDefaultWatchlistAsync(int accountId, int watchlistId, CancellationToken ct = default)
    {
        var target = await db.Watchlists.FirstOrDefaultAsync(w => w.AccountId == accountId && w.Id == watchlistId, ct)
            ?? throw new InvalidOperationException($"Watchlist {watchlistId} not found for account {accountId}.");

        var all = await db.Watchlists.Where(w => w.AccountId == accountId).ToListAsync(ct);
        foreach (var w in all)
            w.IsDefault = w.Id == watchlistId;
        target.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<WatchlistItem> AddSymbolAsync(int accountId, int watchlistId, string symbol, string companyName, string sector, CancellationToken ct = default)
    {
        var watchlist = await db.Watchlists.FirstOrDefaultAsync(w => w.AccountId == accountId && w.Id == watchlistId, ct)
            ?? throw new InvalidOperationException($"Watchlist {watchlistId} not found for account {accountId}.");

        var symbolUpper = symbol.ToUpperInvariant();

        var existing = await db.WatchlistItems.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.WatchlistId == watchlistId && i.Symbol == symbolUpper, ct);

        if (existing is not null)
        {
            if (existing.IsActive)
                throw new ValidationException($"'{symbolUpper}' is already on this watchlist.");

            existing.IsActive = true;
            existing.CompanyName = companyName;
            existing.Sector = sector;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var activeCount = await db.WatchlistItems.CountAsync(i => i.WatchlistId == watchlistId, ct);
        if (activeCount >= WatchlistLimits.MaxSymbolsPerWatchlist)
            throw new ValidationException($"A watchlist can hold at most {WatchlistLimits.MaxSymbolsPerWatchlist} symbols.");

        var now = DateTime.UtcNow;
        var item = new WatchlistItem
        {
            AccountId = accountId,
            WatchlistId = watchlistId,
            Symbol = symbolUpper,
            CompanyName = companyName,
            Sector = sector,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.WatchlistItems.Add(item);
        await db.SaveChangesAsync(ct);
        return item;
    }

    public async Task RemoveSymbolAsync(int accountId, int watchlistId, string symbol, CancellationToken ct = default)
    {
        var symbolUpper = symbol.ToUpperInvariant();
        var item = await db.WatchlistItems.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.AccountId == accountId && i.WatchlistId == watchlistId && i.Symbol == symbolUpper, ct);

        if (item is not null)
        {
            item.IsActive = false;
            item.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<List<WatchlistItem>> GetSymbolsAsync(int accountId, int watchlistId, CancellationToken ct = default) =>
        await db.WatchlistItems
            .Where(i => i.AccountId == accountId && i.WatchlistId == watchlistId)
            .ToListAsync(ct);

    public async Task SetForceIntoFinalListAsync(int accountId, int watchlistId, string symbol, bool force, CancellationToken ct = default)
    {
        var symbolUpper = symbol.ToUpperInvariant();
        var item = await db.WatchlistItems
            .Include(i => i.Watchlist)
            .FirstOrDefaultAsync(i => i.AccountId == accountId && i.WatchlistId == watchlistId && i.Symbol == symbolUpper, ct)
            ?? throw new InvalidOperationException($"Symbol '{symbol}' not found on watchlist {watchlistId}.");

        item.ForceIntoFinalList = force;
        item.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
