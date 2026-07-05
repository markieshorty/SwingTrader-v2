using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class TierEvaluationRepository(SwingTraderDbContext context) : ITierEvaluationRepository
{
    public async Task<TierEvaluationRecord> AddAsync(TierEvaluationRecord record)
    {
        record.CreatedAt = record.CreatedAt == default ? DateTime.UtcNow : record.CreatedAt;
        context.TierEvaluationRecords.Add(record);
        await context.SaveChangesAsync();
        return record;
    }

    public Task<TierEvaluationRecord?> GetLatestAsync(int accountId) =>
        context.TierEvaluationRecords
            .Where(r => r.AccountId == accountId)
            .OrderByDescending(r => r.EvaluatedAt)
            .FirstOrDefaultAsync();

    public async Task<IEnumerable<TierEvaluationRecord>> GetHistoryAsync(int accountId, int count = 12) =>
        await context.TierEvaluationRecords
            .Where(r => r.AccountId == accountId)
            .OrderByDescending(r => r.EvaluatedAt)
            .Take(count)
            .ToListAsync();
}
