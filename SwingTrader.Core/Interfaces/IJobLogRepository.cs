using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IJobLogRepository
{
    // Null if this account/jobType/date combination hasn't been enqueued yet.
    Task<JobLogEntry?> FindAsync(int accountId, string jobType, DateOnly jobDate, CancellationToken ct = default);
    Task<JobLogEntry> CreateEnqueuedAsync(int accountId, string jobType, DateOnly jobDate, CancellationToken ct = default);
    Task MarkProcessingAsync(int accountId, string jobType, DateOnly jobDate, CancellationToken ct = default);
    Task MarkCompletedAsync(int accountId, string jobType, DateOnly jobDate, CancellationToken ct = default);
    Task MarkFailedAsync(int accountId, string jobType, DateOnly jobDate, string errorMessage, CancellationToken ct = default);
}
