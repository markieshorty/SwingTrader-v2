using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class HistoricalCandleRepository(SwingTraderDbContext db) : IHistoricalCandleRepository
{
    // The whole-table reads below scan millions of rows (10y of daily bars for
    // ~1,500 symbols + SPY + sector ETFs). On the Basic-tier SQL database
    // (5 DTU) that legitimately takes longer than ADO.NET's 30s default
    // command timeout, so these queries were failing with "Execution Timeout
    // Expired" - killing the optimizer on its very first step. A generous
    // per-query timeout lets the big reads finish on a throttled tier; it's
    // only ever hit by these known-heavy candle queries, everything else keeps
    // the sensible 30s default. (The real fix is a bigger DB tier, but this
    // keeps the Lab working without it.)
    private const int HeavyReadTimeoutSeconds = 300;

    public async Task<Dictionary<string, DateOnly>> GetLatestDatesAsync(CancellationToken ct = default)
    {
        db.Database.SetCommandTimeout(HeavyReadTimeoutSeconds);
        return await db.HistoricalCandles
            .GroupBy(c => c.Symbol)
            .Select(g => new { Symbol = g.Key, Max = g.Max(c => c.Date) })
            .ToDictionaryAsync(x => x.Symbol, x => x.Max, StringComparer.OrdinalIgnoreCase, ct);
    }

    public async Task<Dictionary<string, DateOnly>> GetEarliestDatesAsync(CancellationToken ct = default)
    {
        db.Database.SetCommandTimeout(HeavyReadTimeoutSeconds);
        return await db.HistoricalCandles
            .GroupBy(c => c.Symbol)
            .Select(g => new { Symbol = g.Key, Min = g.Min(c => c.Date) })
            .ToDictionaryAsync(x => x.Symbol, x => x.Min, StringComparer.OrdinalIgnoreCase, ct);
    }

    public async Task AddRangeAsync(IEnumerable<HistoricalCandle> candles, CancellationToken ct = default)
    {
        db.HistoricalCandles.AddRange(candles);
        await db.SaveChangesAsync(ct);
        // Keep the change tracker lean across a 1,500-symbol sync run.
        db.ChangeTracker.Clear();
    }

    public async Task<Dictionary<string, List<HistoricalCandle>>> GetAllBySymbolAsync(CancellationToken ct = default)
    {
        db.Database.SetCommandTimeout(HeavyReadTimeoutSeconds);
        var all = await db.HistoricalCandles.AsNoTracking()
            .OrderBy(c => c.Symbol).ThenBy(c => c.Date)
            .ToListAsync(ct);
        return all.GroupBy(c => c.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    public Task<int> CountAsync(CancellationToken ct = default)
    {
        db.Database.SetCommandTimeout(HeavyReadTimeoutSeconds);
        return db.HistoricalCandles.CountAsync(ct);
    }

    public async Task<DateOnly?> GetMaxDateAsync(CancellationToken ct = default)
    {
        db.Database.SetCommandTimeout(HeavyReadTimeoutSeconds);
        return await db.HistoricalCandles.AnyAsync(ct) ? await db.HistoricalCandles.MaxAsync(c => c.Date, ct) : null;
    }
}
