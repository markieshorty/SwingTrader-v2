using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IWatchlistHistoryRepository
{
    Task AddAsync(WatchlistHistory entry);
}
