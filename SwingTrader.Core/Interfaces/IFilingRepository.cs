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

    // Deltas joined with their filing's identity (type, EDGAR coordinates) -
    // the Intelligence page's filings tab, where every AI-scored change must
    // link back to the source document in one click.
    Task<List<FilingDeltaView>> GetDeltaViewsSinceAsync(DateTime sinceUtc, CancellationToken ct = default);

    // Filings stored in the window regardless of change - shown as context
    // ("34 checked, 5 changed") so a quiet quarter reads as the hash gate
    // working rather than the pipeline broken.
    Task<int> CountFilingsSinceAsync(DateTime sinceUtc, CancellationToken ct = default);

    // ── Distress flags (FD3) ────────────────────────────────────────────────
    // Idempotent per (accession, reason) so a re-run of the sync never
    // duplicates a flag. Returns true when a new flag was actually added.
    Task<bool> AddDistressFlagAsync(DistressFlag flag, CancellationToken ct = default);

    // Flags whose FiledAt falls on/after activeSince - "active" per the
    // caller's DistressWindowDays. One symbol (research veto)...
    Task<List<DistressFlag>> GetActiveDistressFlagsAsync(string symbol, DateOnly activeSince, CancellationToken ct = default);

    // ...or a set (the monitor's one-query check across open positions).
    Task<List<DistressFlag>> GetActiveDistressFlagsAsync(IReadOnlyCollection<string> symbols, DateOnly activeSince, CancellationToken ct = default);

    // Flags created in the window - report/Intelligence visibility.
    Task<List<DistressFlag>> GetDistressFlagsSinceAsync(DateTime sinceUtc, CancellationToken ct = default);
}

public sealed record FilingDeltaView(
    FilingDelta Delta, string FilingType, string Cik, string AccessionNumber, string PrimaryDocument);
