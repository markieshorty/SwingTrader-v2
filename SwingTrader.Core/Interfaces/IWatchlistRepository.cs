using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IWatchlistRepository
{
    // Seeds the starter watchlist for a brand-new account.
    Task SeedDefaultAsync(int accountId, CancellationToken ct = default);

    // Legacy account-scoped accessors - operate against the account's default
    // (Type == AiManaged && IsDefault) watchlist, so the Watchlist Agent and
    // Research pipeline code that predates multiple watchlists keeps working
    // unchanged.
    Task<WatchlistItem?> GetByIdAsync(int accountId, int id);
    Task<IEnumerable<WatchlistItem>> GetAllAsync(int accountId);
    Task<IEnumerable<WatchlistItem>> GetActiveAsync(int accountId);
    Task<WatchlistItem?> GetBySymbolAsync(int accountId, string symbol);
    Task<WatchlistItem> AddAsync(WatchlistItem item);
    Task UpdateAsync(WatchlistItem item);
    Task DeleteAsync(int accountId, int id);

    // Multiple named watchlists.
    Task<List<Watchlist>> GetAllWatchlistsAsync(int accountId, CancellationToken ct = default);
    Task<Watchlist?> GetWatchlistAsync(int accountId, int watchlistId, CancellationToken ct = default);

    // Deduplicated (by symbol) union of items across every enabled watchlist -
    // what the Research pipeline scans.
    Task<List<WatchlistItem>> GetAllEnabledSymbolsAsync(int accountId, CancellationToken ct = default);

    Task<Watchlist> CreateWatchlistAsync(int accountId, string name, WatchlistType type, string? description, CancellationToken ct = default);
    Task UpdateWatchlistAsync(int accountId, int watchlistId, string name, string? description, CancellationToken ct = default);

    // Validates: at most WatchlistLimits.MaxEnabledWatchlists enabled at once.
    Task EnableWatchlistAsync(int accountId, int watchlistId, CancellationToken ct = default);
    Task DisableWatchlistAsync(int accountId, int watchlistId, CancellationToken ct = default);

    // Fails if IsDefault == true - a default watchlist can't be deleted, only replaced via SetDefaultWatchlistAsync first.
    Task DeleteWatchlistAsync(int accountId, int watchlistId, CancellationToken ct = default);
    Task SetDefaultWatchlistAsync(int accountId, int watchlistId, CancellationToken ct = default);

    // Validates: at most WatchlistLimits.MaxSymbolsPerWatchlist per watchlist. Callers
    // are expected to have already validated the symbol exists (e.g. via Finnhub) -
    // kept out of this Core-layer repository, which has no HTTP client dependency.
    Task<WatchlistItem> AddSymbolAsync(int accountId, int watchlistId, string symbol, string companyName, string sector, CancellationToken ct = default);
    Task RemoveSymbolAsync(int accountId, int watchlistId, string symbol, CancellationToken ct = default);
    Task<List<WatchlistItem>> GetSymbolsAsync(int accountId, int watchlistId, CancellationToken ct = default);
}
