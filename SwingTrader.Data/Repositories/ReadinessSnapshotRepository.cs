using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class ReadinessSnapshotRepository(SwingTraderDbContext context) : IReadinessSnapshotRepository
{
    public async Task UpsertAsync(ReadinessSnapshot snapshot)
    {
        var existing = await context.ReadinessSnapshots
            .FirstOrDefaultAsync(s => s.AccountId == snapshot.AccountId && s.SnapshotDate == snapshot.SnapshotDate);

        if (existing is null)
        {
            snapshot.CreatedAt = snapshot.CreatedAt == default ? DateTime.UtcNow : snapshot.CreatedAt;
            context.ReadinessSnapshots.Add(snapshot);
        }
        else
        {
            existing.ScoredClosedTrades = snapshot.ScoredClosedTrades;
            existing.ObservedWinRate = snapshot.ObservedWinRate;
            existing.TradesPerWeekWeighted = snapshot.TradesPerWeekWeighted;
            existing.RegimeBullCount = snapshot.RegimeBullCount;
            existing.RegimeNeutralCount = snapshot.RegimeNeutralCount;
            existing.RegimeBearCount = snapshot.RegimeBearCount;
            existing.SystemRunningDays = snapshot.SystemRunningDays;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await context.SaveChangesAsync();
    }

    public async Task<List<ReadinessSnapshot>> GetRecentAsync(int accountId, int days = 30)
    {
        var recent = await context.ReadinessSnapshots
            .Where(s => s.AccountId == accountId)
            .OrderByDescending(s => s.SnapshotDate)
            .Take(days)
            .ToListAsync();
        return recent.OrderBy(s => s.SnapshotDate).ToList();
    }

    public Task<ReadinessSnapshot?> GetLatestAsync(int accountId) =>
        context.ReadinessSnapshots
            .Where(s => s.AccountId == accountId)
            .OrderByDescending(s => s.SnapshotDate)
            .FirstOrDefaultAsync();
}
