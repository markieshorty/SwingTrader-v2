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

    public async Task<IEnumerable<WatchlistHistory>> GetHistoryAsync(int accountId, DateOnly from, DateOnly to) =>
        await context.WatchlistHistory
            .Where(h => h.AccountId == accountId && h.WeekStarting >= from && h.WeekStarting <= to)
            .OrderByDescending(h => h.WeekStarting)
            .ToListAsync();

    public async Task<IEnumerable<WatchlistHistory>> GetBySymbolAsync(int accountId, string symbol) =>
        await context.WatchlistHistory
            .Where(h => h.AccountId == accountId && h.Symbol == symbol.ToUpperInvariant())
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync();
}
