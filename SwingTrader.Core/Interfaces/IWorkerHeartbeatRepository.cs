using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

// One heartbeat row per (account, worker) - per-account since 20 Jul 2026
// so concurrent runs for different accounts don't overwrite each other's
// stage breadcrumbs.
public interface IWorkerHeartbeatRepository
{
    Task UpsertAsync(int accountId, string workerName, string result, string? message);
    Task<WorkerHeartbeat?> GetAsync(int accountId, string workerName);
    Task<IEnumerable<WorkerHeartbeat>> GetAllAsync();
}
