using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IFilingRepository
{
    // Latest stored filing per (symbol, type) - the sync's "what do we
    // already have" read, and the previous-filing lookup for the hash gate.
    Task<Filing?> GetLatestAsync(string symbol, string filingType, CancellationToken ct = default);

    Task<bool> ExistsAsync(string accessionNumber, CancellationToken ct = default);
    Task<Filing> AddAsync(Filing filing, CancellationToken ct = default);

    Task<FilingDelta> AddDeltaAsync(FilingDelta delta, CancellationToken ct = default);

    // Most recent non-zero delta for a symbol - the research pipeline decays
    // this into the shadow FilingDeltaScore.
    Task<FilingDelta?> GetLatestNonZeroDeltaAsync(string symbol, CancellationToken ct = default);

    // Deltas scored in the window - the daily report's visibility line.
    Task<List<FilingDelta>> GetDeltasSinceAsync(DateTime sinceUtc, CancellationToken ct = default);
}
