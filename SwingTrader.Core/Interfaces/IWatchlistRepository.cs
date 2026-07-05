using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IWatchlistRepository
{
    // Seeds the starter watchlist for a brand-new account.
    Task SeedDefaultAsync(int accountId, CancellationToken ct = default);

    Task<WatchlistItem?> GetByIdAsync(int accountId, int id);
    Task<IEnumerable<WatchlistItem>> GetAllAsync(int accountId);
    Task<IEnumerable<WatchlistItem>> GetActiveAsync(int accountId);
    Task<WatchlistItem?> GetBySymbolAsync(int accountId, string symbol);
    Task<WatchlistItem> AddAsync(WatchlistItem item);
    Task UpdateAsync(WatchlistItem item);
    Task DeleteAsync(int accountId, int id);
}
