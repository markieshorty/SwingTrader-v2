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

    // No jobDate filter for in-flight entries: a re-run of a prior day's
    // message (Sunday's watchlist job retried Monday morning) is keyed to the
    // ORIGINAL job date, and date-filtering made it invisible in the toolbar.
    // The 24h activity window keeps crashed-host ghosts out instead.
    public Task<List<JobLogEntry>> GetActiveOrRecentAsync(int accountId, DateTime completedSince, CancellationToken ct = default)
    {
        var activeSince = DateTime.UtcNow.AddHours(-24);
        return db.JobLogEntries
            .Where(j => j.AccountId == accountId
                && (((j.Status == JobStatus.Enqueued || j.Status == JobStatus.Processing) && j.UpdatedAt >= activeSince)
                    || (j.CompletedAt != null && j.CompletedAt >= completedSince)))
            .ToListAsync(ct);
    }

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

    public async Task<bool> TryCreateEnqueuedAsync(int accountId, string jobType, DateOnly jobDate, CancellationToken ct = default)
    {
        // Fast path: someone already claimed this slot.
        if (await FindAsync(accountId, jobType, jobDate, ct) is not null) return false;

        var entry = new JobLogEntry
        {
            AccountId = accountId,
            JobType = jobType,
            JobDate = jobDate,
            Status = JobStatus.Enqueued,
            EnqueuedAt = DateTime.UtcNow,
        };
        db.JobLogEntries.Add(entry);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // Lost the race: a concurrent scheduler execution inserted between
            // our Find and SaveChanges - the UNIQUE index on (AccountId,
            // JobType, JobDate) rejected ours. Detach so this scoped context
            // stays usable for the remaining accounts in the same tick.
            db.Entry(entry).State = EntityState.Detached;
            return false;
        }
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

    public async Task DeleteAsync(int accountId, string jobType, DateOnly jobDate, CancellationToken ct = default)
    {
        var entry = await FindAsync(accountId, jobType, jobDate, ct);
        if (entry is null) return;
        db.JobLogEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
    }
}
