using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class AdminLogRepository(SwingTraderDbContext db) : IAdminLogRepository
{
    public async Task LogAsync(AdminActionLog entry, CancellationToken ct = default)
    {
        entry.PerformedAt = entry.PerformedAt == default ? DateTime.UtcNow : entry.PerformedAt;
        entry.CreatedAt = DateTime.UtcNow;
        entry.UpdatedAt = DateTime.UtcNow;
        db.AdminActionLogs.Add(entry);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<AdminActionLog>> GetRecentAsync(int count = 200, CancellationToken ct = default) =>
        await db.AdminActionLogs
            .OrderByDescending(l => l.PerformedAt)
            .Take(count)
            .ToListAsync(ct);
}
