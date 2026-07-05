using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class JobLogRepository(SwingTraderDbContext db) : IJobLogRepository
{
    public Task<JobLogEntry?> FindAsync(int accountId, string jobType, DateOnly jobDate, CancellationToken ct = default) =>
        db.JobLogEntries.FirstOrDefaultAsync(
            j => j.AccountId == accountId && j.JobType == jobType && j.JobDate == jobDate, ct);

    public async Task<JobLogEntry> CreateEnqueuedAsync(int accountId, string jobType, DateOnly jobDate, CancellationToken ct = default)
    {
        var entry = new JobLogEntry
        {
            AccountId = accountId,
            JobType = jobType,
            JobDate = jobDate,
            Status = JobStatus.Enqueued,
            EnqueuedAt = DateTime.UtcNow,
        };
        db.JobLogEntries.Add(entry);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task MarkProcessingAsync(int accountId, string jobType, DateOnly jobDate, CancellationToken ct = default)
    {
        var entry = await FindAsync(accountId, jobType, jobDate, ct);
        if (entry is null) return;
        entry.Status = JobStatus.Processing;
        entry.AttemptCount++;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkCompletedAsync(int accountId, string jobType, DateOnly jobDate, CancellationToken ct = default)
    {
        var entry = await FindAsync(accountId, jobType, jobDate, ct);
        if (entry is null) return;
        entry.Status = JobStatus.Completed;
        entry.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkFailedAsync(int accountId, string jobType, DateOnly jobDate, string errorMessage, CancellationToken ct = default)
    {
        var entry = await FindAsync(accountId, jobType, jobDate, ct);
        if (entry is null) return;
        entry.Status = JobStatus.Failed;
        entry.ErrorMessage = errorMessage;
        await db.SaveChangesAsync(ct);
    }
}
