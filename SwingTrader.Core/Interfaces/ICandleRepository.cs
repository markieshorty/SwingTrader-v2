using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

// Candles are market data, not account-specific (see StockCandle's AccountId
// comment in SwingTraderDbContext) - every account shares the same cache.
public interface ICandleRepository
{
    Task SaveCandlesAsync(int accountId, IEnumerable<StockCandle> candles);
    Task<IReadOnlyList<StockCandle>> GetCandlesAsync(string symbol, string resolution, DateTime from, DateTime to);
    Task<DateTime?> GetLatestCandleDateAsync(string symbol, string resolution);

    // Batch forms of GetLatestCandleDateAsync/GetCandlesAsync - fetch once for
    // every symbol in a Research job up front, before the per-symbol concurrent
    // loop starts. A per-symbol call at that point (one DbContext, up to
    // MaxConcurrentSymbols callers, no delay in front of it) hit EF Core's
    // single-operation-at-a-time guard almost immediately - the same class of
    // bug as the risk profile fix.
    Task<IReadOnlyDictionary<string, DateTime>> GetLatestCandleDatesAsync(IEnumerable<string> symbols, string resolution);

    Task<IReadOnlyDictionary<string, IReadOnlyList<StockCandle>>> GetCandlesForSymbolsAsync(
        IEnumerable<string> symbols, string resolution, DateTime from, DateTime to);
}
