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
}
