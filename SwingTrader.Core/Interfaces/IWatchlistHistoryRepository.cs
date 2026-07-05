using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IWatchlistHistoryRepository
{
    Task AddAsync(WatchlistHistory entry);
    Task<IEnumerable<WatchlistHistory>> GetHistoryAsync(int accountId, DateOnly from, DateOnly to);
    Task<IEnumerable<WatchlistHistory>> GetBySymbolAsync(int accountId, string symbol);
}
