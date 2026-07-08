using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class WatchlistHistoryRepository(SwingTraderDbContext context) : IWatchlistHistoryRepository
{
    public async Task AddAsync(WatchlistHistory entry)
    {
        entry.Symbol = entry.Symbol.ToUpperInvariant();
        context.WatchlistHistory.Add(entry);
        await context.SaveChangesAsync();
    }
}
