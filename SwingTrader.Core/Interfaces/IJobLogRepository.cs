using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IJobLogRepository
{
    // Null if this account/jobType/date combination hasn't been enqueued yet.
    Task<JobLogEntry?> FindAsync(int accountId, string jobType, DateOnly jobDate, CancellationToken ct = default);
    Task<JobLogEntry> CreateEnqueuedAsync(int accountId, string jobType, DateOnly jobDate, CancellationToken ct = default);

    // Claim-style insert: returns false instead of throwing when the row
    // already exists (the UNIQUE index on AccountId/JobType/JobDate acts as a
    // distributed lock). Lets the Scheduler claim a job slot BEFORE sending to
    // Service Bus, so two overlapping scheduler executions can't both enqueue
    // the same job - only the insert winner sends.
    Task<bool> TryCreateEnqueuedAsync(int accountId, string jobType, DateOnly jobDate, CancellationToken ct = default);
    Task MarkProcessingAsync(int accountId, string jobType, DateOnly jobDate, CancellationToken ct = default);
    Task MarkCompletedAsync(int accountId, string jobType, DateOnly jobDate, CancellationToken ct = default);
    Task MarkFailedAsync(int accountId, string jobType, DateOnly jobDate, string errorMessage, CancellationToken ct = default);
    Task DeleteAsync(int accountId, string jobType, DateOnly jobDate, CancellationToken ct = default);
}
