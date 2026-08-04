using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IBacktestRunRepository
{
    Task<BacktestRun> AddAsync(BacktestRun run);
    Task<BacktestRun?> GetByIdAsync(int accountId, int id);
    Task UpdateAsync(BacktestRun run);

    // Persist ONLY the progress counters and return the run's CURRENT DB
    // status. The consumer's periodic progress saves used to save the whole
    // tracked entity, which clobbered a user-set 'Cancelled' back to
    // 'Running' - this is both the clobber fix and the cancellation poll
    // (the consumer aborts when the returned status says Cancelled).
    Task<string?> SaveProgressAsync(BacktestRun run, CancellationToken ct = default);

    // Discard an in-flight (Queued/Running) run: the consumer's periodic
    // progress poll aborts a Running sweep mid-run, and the redelivery check
    // drops a Queued message. False = not found or already finished.
    Task<bool> CancelAsync(int accountId, int id, CancellationToken ct = default);

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

    // History-tab projection: only the JSON SLICE the apply extractor needs
    // ($.winner for a sweep, $.candidates for an A/B) is cut out of
    // ResultJson server-side. A full sweep ResultJson carries ~1,200
    // candidates (MBs); pulling 20 of those over the Basic-tier DB is what
    // made the history tabs crawl.
    Task<List<BacktestHistorySlice>> GetHistorySlicesAsync(int accountId, string mode, int limit, CancellationToken ct = default);

    // In-flight runs plus anything completed/failed after `since` - the
    // toolbar job-status indicator's feed. Excludes payload columns.
    Task<List<BacktestRun>> GetActiveOrRecentAsync(int accountId, DateTime since, CancellationToken ct = default);

    // Latest COMPLETED run of a mode whose stamped ConfigFingerprint matches -
    // the strategy-share evidence lookup: "has this exact live config passed
    // validate / been Monte Carlo'd?".
    Task<BacktestRun?> GetLatestCompletedByFingerprintAsync(
        int accountId, string mode, string fingerprint, CancellationToken ct = default);
}
