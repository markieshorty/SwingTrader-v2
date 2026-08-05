using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

public interface IHistoricalCandleRepository
{
    // Latest stored date per symbol - the sync job fetches only newer bars.
    Task<Dictionary<string, DateOnly>> GetLatestDatesAsync(CancellationToken ct = default);

    // Earliest stored date per symbol - the sync job backfills older history
    // when the configured window grows (e.g. 3 years -> 5 years).
    Task<Dictionary<string, DateOnly>> GetEarliestDatesAsync(CancellationToken ct = default);

    Task AddRangeAsync(IEnumerable<HistoricalCandle> candles, CancellationToken ct = default);

    // Everything from `from` (null = everything stored), grouped per symbol
    // ordered by date - the historic backtest loads its data window into
    // memory once per run. The from-filter is the deep-history memory guard
    // (docs/deep-history-plan): standard runs never load pre-2016 bars.
    Task<Dictionary<string, List<HistoricalCandle>>> GetAllBySymbolAsync(DateOnly? from = null, CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);
    Task<DateOnly?> GetMaxDateAsync(CancellationToken ct = default);

    // Survivorship-free dataset support (docs/survivorship-plan P1).
    Task<decimal> GetDatabaseSizeMbAsync(CancellationToken ct = default);
    Task<int> GetDatasetVersionAsync(CancellationToken ct = default);
    Task BumpDatasetVersionAsync(CancellationToken ct = default);

    // Bars for a specific symbol set from a date, grouped per symbol ordered by
    // date. The scorecard's counterfactual replays need a few dozen symbols
    // over a few months - a targeted read, NOT the whole-table load above
    // (which is a 300s-timeout query on the Basic tier).
    Task<Dictionary<string, List<HistoricalCandle>>> GetForSymbolsAsync(
        IReadOnlyCollection<string> symbols, DateOnly from, CancellationToken ct = default);
}
