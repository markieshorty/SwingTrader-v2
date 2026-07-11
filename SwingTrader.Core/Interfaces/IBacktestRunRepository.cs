using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IBacktestRunRepository
{
    Task<BacktestRun> AddAsync(BacktestRun run);
    Task<BacktestRun?> GetByIdAsync(int accountId, int id);
    Task UpdateAsync(BacktestRun run);

    // Latest completed run of a given request mode ("sweep", "ab", ...).
    // Results are persisted forever in ResultJson but the run id only ever
    // lived in the Angular component's memory - so an hour-long optimizer
    // run's result was effectively lost on page refresh. This lets the UI
    // reload the most recent one on tab load.
    Task<BacktestRun?> GetLatestCompletedByModeAsync(int accountId, string mode, CancellationToken ct = default);
}
