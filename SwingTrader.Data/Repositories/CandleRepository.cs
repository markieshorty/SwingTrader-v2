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

    public async Task<IReadOnlyDictionary<string, DateTime>> GetLatestCandleDatesAsync(IEnumerable<string> symbols, string resolution)
    {
        var upperSymbols = symbols.Select(s => s.ToUpperInvariant()).Distinct().ToList();

        return await context.StockCandles
            .Where(c => upperSymbols.Contains(c.Symbol) && c.Resolution == resolution)
            .GroupBy(c => c.Symbol)
            .Select(g => new { Symbol = g.Key, Latest = g.Max(c => c.Timestamp) })
            .ToDictionaryAsync(x => x.Symbol, x => x.Latest);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<StockCandle>>> GetCandlesForSymbolsAsync(
        IEnumerable<string> symbols, string resolution, DateTime from, DateTime to)
    {
        var upperSymbols = symbols.Select(s => s.ToUpperInvariant()).Distinct().ToList();
        if (upperSymbols.Count == 0)
            return new Dictionary<string, IReadOnlyList<StockCandle>>();

        var candles = await context.StockCandles
            .Where(c => upperSymbols.Contains(c.Symbol)
                     && c.Resolution == resolution
                     && c.Timestamp >= from
                     && c.Timestamp <= to)
            .OrderBy(c => c.Timestamp)
            .ToListAsync();

        return candles
            .GroupBy(c => c.Symbol)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<StockCandle>)g.ToList());
    }
}
