using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class SignalRepository(SwingTraderDbContext context) : ISignalRepository
{
    public Task<StockSignal?> GetByIdAsync(int accountId, int id) =>
        context.StockSignals.FirstOrDefaultAsync(x => x.AccountId == accountId && x.Id == id);

    public async Task<IEnumerable<StockSignal>> GetAllAsync(int accountId) =>
        await context.StockSignals.Where(x => x.AccountId == accountId).ToListAsync();

    public async Task<IEnumerable<StockSignal>> GetByDateAsync(int accountId, DateOnly date) =>
        await context.StockSignals.Where(x => x.AccountId == accountId && x.SignalDate == date).ToListAsync();

    public async Task<IEnumerable<StockSignal>> GetSinceDateAsync(int accountId, DateOnly fromDate) =>
        await context.StockSignals.Where(x => x.AccountId == accountId && x.SignalDate >= fromDate).ToListAsync();

    public async Task<IEnumerable<StockSignal>> GetUnexecutedSignalsAsync(int accountId) =>
        await context.StockSignals.Where(x => x.AccountId == accountId && !x.WasExecuted).ToListAsync();

    public async Task<IEnumerable<StockSignal>> GetBySymbolAsync(int accountId, string symbol) =>
        await context.StockSignals
            .Where(x => x.AccountId == accountId && x.Symbol == symbol.ToUpperInvariant())
            .OrderByDescending(x => x.SignalDate)
            .ToListAsync();

    public async Task<StockSignal> AddAsync(StockSignal signal)
    {
        signal.Symbol = signal.Symbol.ToUpperInvariant();
        context.StockSignals.Add(signal);
        await context.SaveChangesAsync();
        return signal;
    }

    public async Task UpdateAsync(StockSignal signal)
    {
        signal.UpdatedAt = DateTime.UtcNow;
        context.StockSignals.Update(signal);
        await context.SaveChangesAsync();
    }

    public async Task<StockSignal> UpsertAsync(StockSignal signal)
    {
        var existing = await context.StockSignals.FirstOrDefaultAsync(s =>
            s.AccountId == signal.AccountId &&
            s.Symbol == signal.Symbol.ToUpperInvariant() &&
            s.SignalDate == signal.SignalDate);

        if (existing is null)
            return await AddAsync(signal);

        existing.Recommendation = signal.Recommendation;
        existing.ConvictionScore = signal.ConvictionScore;
        existing.EarningsSetupType = signal.EarningsSetupType;
        existing.DaysUntilEarnings = signal.DaysUntilEarnings;
        existing.Reasoning = signal.Reasoning;
        // WasExecuted is deliberately NOT copied from the incoming signal -
        // callers (Research's earnings-gate short-circuit) always construct
        // a brand-new StockSignal with WasExecuted defaulted to false, which
        // would otherwise silently clear an existing "already bought today"
        // flag the moment that symbol gets rescored later the same day.
        // WasExecuted can only be legitimately set by ExecutionService's own
        // direct UpdateAsync call after actually placing a trade.
        await UpdateAsync(existing);
        return existing;
    }

    public async Task DeleteAsync(int accountId, int id)
    {
        var signal = await context.StockSignals.FirstOrDefaultAsync(x => x.AccountId == accountId && x.Id == id);
        if (signal is not null)
        {
            context.StockSignals.Remove(signal);
            await context.SaveChangesAsync();
        }
    }
}
