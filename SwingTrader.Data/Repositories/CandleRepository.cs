using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class CandleRepository(SwingTraderDbContext context) : ICandleRepository
{
    public async Task SaveCandlesAsync(int accountId, IEnumerable<StockCandle> candles)
    {
        foreach (var candle in candles)
        {
            var exists = await context.StockCandles.AnyAsync(c =>
                c.Symbol == candle.Symbol &&
                c.Resolution == candle.Resolution &&
                c.Timestamp == candle.Timestamp);

            if (!exists)
            {
                candle.AccountId = accountId;
                context.StockCandles.Add(candle);
            }
        }

        await context.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<StockCandle>> GetCandlesAsync(string symbol, string resolution, DateTime from, DateTime to) =>
        await context.StockCandles
            .Where(c => c.Symbol == symbol.ToUpperInvariant()
                     && c.Resolution == resolution
                     && c.Timestamp >= from
                     && c.Timestamp <= to)
            .OrderBy(c => c.Timestamp)
            .ToListAsync();

    public Task<DateTime?> GetLatestCandleDateAsync(string symbol, string resolution) =>
        context.StockCandles
            .Where(c => c.Symbol == symbol.ToUpperInvariant() && c.Resolution == resolution)
            .MaxAsync(c => (DateTime?)c.Timestamp);
}
