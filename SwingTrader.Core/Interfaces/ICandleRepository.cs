using SwingTrader.Core.Models;

namespace SwingTrader.Core.Interfaces;

// Candles are market data, not account-specific (see StockCandle's AccountId
// comment in SwingTraderDbContext) - every account shares the same cache.
public interface ICandleRepository
{
    Task SaveCandlesAsync(int accountId, IEnumerable<StockCandle> candles);
    Task<IReadOnlyList<StockCandle>> GetCandlesAsync(string symbol, string resolution, DateTime from, DateTime to);
    Task<DateTime?> GetLatestCandleDateAsync(string symbol, string resolution);
}
