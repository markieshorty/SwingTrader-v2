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
}
