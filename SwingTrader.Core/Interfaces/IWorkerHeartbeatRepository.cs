using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

// Worker heartbeats are process-wide (one Functions app), not per-account.
public interface IWorkerHeartbeatRepository
{
    Task UpsertAsync(string workerName, string result, string? message);
    Task<WorkerHeartbeat?> GetAsync(string workerName);
    Task<IEnumerable<WorkerHeartbeat>> GetAllAsync();
    Task<IEnumerable<WorkerRunLog>> GetRunLogsAsync(int limit = 100);
}
