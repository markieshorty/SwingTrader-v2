using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class CandleRepository(SwingTraderDbContext context) : ICandleRepository
{
    public async Task SaveCandlesAsync(int accountId, IEnumerable<StockCandle> candles)
    {
        var incoming = candles.ToList();
        if (incoming.Count == 0) return;

        // Batch the dedupe check: ONE query per (symbol, resolution) group to
        // read the timestamps already stored in the incoming range, then add
        // only the missing bars. The previous per-candle AnyAsync was an N+1
        // that fired ~60 existence queries per symbol every research run and
        // hammered the Basic-tier (5 DTU) DB into a crawl.
        var added = false;
        foreach (var group in incoming.GroupBy(c => new { c.Symbol, c.Resolution }))
        {
            var symbol = group.Key.Symbol;
            var resolution = group.Key.Resolution;
            var min = group.Min(c => c.Timestamp);
            var max = group.Max(c => c.Timestamp);

            var existing = (await context.StockCandles
                    .Where(c => c.Symbol == symbol && c.Resolution == resolution
                             && c.Timestamp >= min && c.Timestamp <= max)
                    .Select(c => c.Timestamp)
                    .ToListAsync())
                .ToHashSet();

            foreach (var candle in group)
            {
                if (existing.Contains(candle.Timestamp)) continue;
                candle.AccountId = accountId;
                context.StockCandles.Add(candle);
                added = true;
            }
        }

        if (added) await context.SaveChangesAsync();
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
