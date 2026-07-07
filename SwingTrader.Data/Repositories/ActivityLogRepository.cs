using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class ActivityLogRepository(SwingTraderDbContext context) : IActivityLogRepository
{
    public async Task LogAsync(int accountId, string category, string title, string result, string? message = null, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        context.ActivityLogs.Add(new ActivityLog
        {
            AccountId = accountId,
            OccurredAt = now,
            Category = category,
            Title = title,
            Result = result,
            Message = message,
            CreatedAt = now,
        });
        await context.SaveChangesAsync(ct);
    }

    public async Task<IEnumerable<ActivityLog>> GetRecentAsync(int accountId, int limit = 200, CancellationToken ct = default) =>
        await context.ActivityLogs
            .Where(x => x.AccountId == accountId || x.AccountId == SwingTraderDbContext.SystemAccountId)
            .OrderByDescending(x => x.OccurredAt)
            .Take(limit)
            .ToListAsync(ct);
}
