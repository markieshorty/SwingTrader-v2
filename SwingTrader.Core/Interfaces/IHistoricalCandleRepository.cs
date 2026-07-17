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

    // Everything, grouped per symbol ordered by date - the historic backtest
    // loads the whole dataset (~1M skinny rows) into memory once per run.
    Task<Dictionary<string, List<HistoricalCandle>>> GetAllBySymbolAsync(CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);
    Task<DateOnly?> GetMaxDateAsync(CancellationToken ct = default);

    // Bars for a specific symbol set from a date, grouped per symbol ordered by
    // date. The scorecard's counterfactual replays need a few dozen symbols
    // over a few months - a targeted read, NOT the whole-table load above
    // (which is a 300s-timeout query on the Basic tier).
    Task<Dictionary<string, List<HistoricalCandle>>> GetForSymbolsAsync(
        IReadOnlyCollection<string> symbols, DateOnly from, CancellationToken ct = default);
}
