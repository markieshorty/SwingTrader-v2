using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class ActivityLogRepository(SwingTraderDbContext context) : IActivityLogRepository
{
    public async Task LogAsync(int accountId, string category, string title, string result, string? message = null, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // Resolved here (rather than threading TradingMode through every one
        // of LogAsync's ~20 call sites across Agents/Functions/Api) since
        // most callers don't otherwise need the account row at all. Doesn't
        // apply to SystemAccountId - those entries aren't tied to one
        // account's mode.
        var tradingMode = accountId == SwingTraderDbContext.SystemAccountId
            ? default
            : (await context.Accounts.FindAsync([accountId], ct))?.TradingMode ?? default;

        context.ActivityLogs.Add(new ActivityLog
        {
            AccountId = accountId,
            TradingMode = tradingMode,
            OccurredAt = now,
            Category = category,
            Title = title,
            Result = result,
            Message = message,
            CreatedAt = now,
        });
        await context.SaveChangesAsync(ct);
    }

    public async Task<IEnumerable<ActivityLog>> GetRecentAsync(int accountId, TradingMode tradingMode, int limit = 200, CancellationToken ct = default) =>
        await context.ActivityLogs
            .Where(x => (x.AccountId == accountId && x.TradingMode == tradingMode) || x.AccountId == SwingTraderDbContext.SystemAccountId)
            .OrderByDescending(x => x.OccurredAt)
            .Take(limit)
            .ToListAsync(ct);
}
