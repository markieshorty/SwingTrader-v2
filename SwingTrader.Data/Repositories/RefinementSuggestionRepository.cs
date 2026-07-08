using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class RefinementSuggestionRepository(SwingTraderDbContext context) : IRefinementSuggestionRepository
{
    public async Task<RefinementSuggestion> AddAsync(RefinementSuggestion suggestion)
    {
        suggestion.CreatedAt = suggestion.CreatedAt == default ? DateTime.UtcNow : suggestion.CreatedAt;
        context.RefinementSuggestions.Add(suggestion);
        await context.SaveChangesAsync();
        return suggestion;
    }

    public Task<RefinementSuggestion?> GetLatestAsync(int accountId, TradingMode tradingMode) =>
        context.RefinementSuggestions
            .Where(r => r.AccountId == accountId && r.TradingMode == tradingMode)
            .OrderByDescending(r => r.GeneratedAt)
            .FirstOrDefaultAsync();

    public Task<RefinementSuggestion?> GetByIdAsync(int accountId, int id) =>
        context.RefinementSuggestions.FirstOrDefaultAsync(r => r.AccountId == accountId && r.Id == id);

    public async Task<IEnumerable<RefinementSuggestion>> GetHistoryAsync(int accountId, TradingMode tradingMode, int count = 12) =>
        await context.RefinementSuggestions
            .Where(r => r.AccountId == accountId && r.TradingMode == tradingMode)
            .OrderByDescending(r => r.GeneratedAt)
            .Take(count)
            .ToListAsync();

    public async Task UpdateAsync(RefinementSuggestion suggestion)
    {
        suggestion.UpdatedAt = DateTime.UtcNow;
        context.RefinementSuggestions.Update(suggestion);
        await context.SaveChangesAsync();
    }

    public async Task SupersedeAllPendingAsync(int accountId, TradingMode tradingMode)
    {
        var pending = await context.RefinementSuggestions
            .Where(r => r.AccountId == accountId && r.TradingMode == tradingMode && r.Status == RefinementStatus.Pending)
            .ToListAsync();
        foreach (var p in pending)
        {
            p.Status = RefinementStatus.Superseded;
            p.UpdatedAt = DateTime.UtcNow;
        }
        if (pending.Count > 0)
            await context.SaveChangesAsync();
    }
}
