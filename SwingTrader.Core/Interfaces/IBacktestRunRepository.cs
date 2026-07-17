using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IBacktestRunRepository
{
    Task<BacktestRun> AddAsync(BacktestRun run);
    Task<BacktestRun?> GetByIdAsync(int accountId, int id);
    Task UpdateAsync(BacktestRun run);

    // Latest run of a given request mode ("sweep", "ab", ...) - an in-flight
    // (Queued/Running) run preferred over the newest completed one. Results
    // are persisted forever in ResultJson but the run id only ever lived in
    // the Angular component's memory - so a page refresh lost both an
    // hour-long optimizer run's RESULT and, mid-run, the poll tracking it.
    // This lets the UI restore the former and reattach to the latter.
    Task<BacktestRun?> GetLatestByModeAsync(int accountId, string mode, CancellationToken ct = default);

    // Completed runs of a mode, newest first - the Strategy Lab history tabs
    // (Optimizer History / A/B History).
    Task<List<BacktestRun>> GetCompletedByModeAsync(int accountId, string mode, int limit, CancellationToken ct = default);

    // Latest COMPLETED run of a mode whose stamped ConfigFingerprint matches -
    // the strategy-share evidence lookup: "has this exact live config passed
    // validate / been Monte Carlo'd?".
    Task<BacktestRun?> GetLatestCompletedByFingerprintAsync(
        int accountId, string mode, string fingerprint, CancellationToken ct = default);
}
