using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IShadowOutcomeRepository
{
    // Upsert on the identity key (symbol + signal date + setup + dial set +
    // dataset). Replay must be idempotent: a re-run under unchanged inputs
    // refreshes rows rather than growing the table, so a failed or partial
    // backfill can simply be run again.
    Task<int> UpsertRangeAsync(IReadOnlyCollection<ShadowOutcome> outcomes, CancellationToken ct = default);

    // Identity keys already stored for a dial set + dataset, so a backfill can
    // skip completed work without loading whole rows.
    Task<HashSet<string>> GetStoredKeysAsync(string dialSetVersion, int datasetVersion, CancellationToken ct = default);

    Task<List<ShadowOutcome>> GetForCalibrationAsync(
        string dialSetVersion, int datasetVersion, CancellationToken ct = default);

    Task<int> CountAsync(string dialSetVersion, int datasetVersion, CancellationToken ct = default);
}
