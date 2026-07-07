using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class WorkerHeartbeatRepository(SwingTraderDbContext context, IActivityLogRepository activityLog) : IWorkerHeartbeatRepository
{
    public async Task UpsertAsync(int accountId, string workerName, string result, string? message)
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

        await context.SaveChangesAsync();

        await activityLog.LogAsync(accountId, "WorkerRun", workerName, result, message);
    }

    public Task<WorkerHeartbeat?> GetAsync(string workerName) =>
        context.WorkerHeartbeats.FirstOrDefaultAsync(x => x.WorkerName == workerName);

    public async Task<IEnumerable<WorkerHeartbeat>> GetAllAsync() =>
        await context.WorkerHeartbeats.OrderBy(x => x.WorkerName).ToListAsync();
}
