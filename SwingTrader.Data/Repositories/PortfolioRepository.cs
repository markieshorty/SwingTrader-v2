using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class PortfolioRepository(SwingTraderDbContext context) : IPortfolioRepository
{
    public Task<PortfolioSnapshot?> GetByIdAsync(int accountId, int id) =>
        context.PortfolioSnapshots.FirstOrDefaultAsync(x => x.AccountId == accountId && x.Id == id);

    public Task<PortfolioSnapshot?> GetLatestSnapshotAsync(int accountId, TradingMode tradingMode) =>
        context.PortfolioSnapshots
            .Where(x => x.AccountId == accountId && x.TradingMode == tradingMode)
            .OrderByDescending(x => x.SnapshotDate)
            .ThenByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync();

    public async Task<IEnumerable<PortfolioSnapshot>> GetSnapshotHistoryAsync(int accountId, TradingMode tradingMode, DateOnly from, DateOnly to) =>
        await context.PortfolioSnapshots
            .Where(x => x.AccountId == accountId && x.TradingMode == tradingMode && x.SnapshotDate >= from && x.SnapshotDate <= to)
            .OrderByDescending(x => x.SnapshotDate)
            .ThenByDescending(x => x.CreatedAt)
            .ToListAsync();

    public async Task<PortfolioSnapshot> AddAsync(PortfolioSnapshot snapshot)
    {
        context.PortfolioSnapshots.Add(snapshot);
        await context.SaveChangesAsync();
        return snapshot;
    }

    public async Task UpdateAsync(PortfolioSnapshot snapshot)
    {
        snapshot.UpdatedAt = DateTime.UtcNow;
        context.PortfolioSnapshots.Update(snapshot);
        await context.SaveChangesAsync();
    }

    public async Task DeleteAsync(int accountId, int id)
    {
        var snapshot = await context.PortfolioSnapshots.FirstOrDefaultAsync(x => x.AccountId == accountId && x.Id == id);
        if (snapshot is not null)
        {
            context.PortfolioSnapshots.Remove(snapshot);
            await context.SaveChangesAsync();
        }
    }
}
