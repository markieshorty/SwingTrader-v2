using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

// Worker heartbeats are process-wide, not per-account, but WorkerHeartbeat
// still carries AccountId via BaseEntity - always the 'system' account since
// there's no meaningful account to attribute a Functions-host-level
// heartbeat to.
public class WorkerHeartbeatRepository(SwingTraderDbContext context) : IWorkerHeartbeatRepository
{
    public async Task UpsertAsync(string workerName, string result, string? message)
    {
        var existing = await context.WorkerHeartbeats
            .FirstOrDefaultAsync(x => x.WorkerName == workerName);

        var now = DateTime.UtcNow;
        if (existing is null)
        {
            context.WorkerHeartbeats.Add(new WorkerHeartbeat
            {
                AccountId = SwingTraderDbContext.SystemAccountId,
                WorkerName = workerName,
                LastHeartbeatAt = now,
                LastRunResult = result,
                LastRunMessage = message,
                CreatedAt = now,
            });
        }
        else
        {
            existing.LastHeartbeatAt = now;
            existing.LastRunResult = result;
            existing.LastRunMessage = message;
            existing.UpdatedAt = now;
        }

        context.WorkerRunLogs.Add(new WorkerRunLog
        {
            AccountId = SwingTraderDbContext.SystemAccountId,
            WorkerName = workerName,
            RanAt = now,
            Result = result,
            Message = message,
            CreatedAt = now,
        });

        await context.SaveChangesAsync();
    }

    public Task<WorkerHeartbeat?> GetAsync(string workerName) =>
        context.WorkerHeartbeats.FirstOrDefaultAsync(x => x.WorkerName == workerName);

    public async Task<IEnumerable<WorkerHeartbeat>> GetAllAsync() =>
        await context.WorkerHeartbeats.OrderBy(x => x.WorkerName).ToListAsync();

    public async Task<IEnumerable<WorkerRunLog>> GetRunLogsAsync(int limit = 100) =>
        await context.WorkerRunLogs
            .OrderByDescending(x => x.RanAt)
            .Take(limit)
            .ToListAsync();
}
