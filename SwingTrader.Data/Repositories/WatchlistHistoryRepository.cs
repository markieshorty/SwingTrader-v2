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

    public async Task<Dictionary<string, string>> GetLatestReasonsAsync(
        int accountId, IReadOnlyCollection<string> symbols, CancellationToken ct = default)
    {
        var upper = symbols.Select(s => s.ToUpperInvariant()).Distinct().ToList();
        var rows = await context.WatchlistHistory
            .Where(h => h.AccountId == accountId
                && h.Action == Core.Enums.WatchlistAction.Added
                && upper.Contains(h.Symbol))
            .OrderByDescending(h => h.Id)
            .ToListAsync(ct);

        return rows
            .GroupBy(h => h.Symbol)
            .ToDictionary(g => g.Key, g => g.First().Reason, StringComparer.OrdinalIgnoreCase);
    }
}
