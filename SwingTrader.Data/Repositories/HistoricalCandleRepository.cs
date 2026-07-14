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

    // SetCommandTimeout is a relational-only method - guarded so it no-ops on
    // the in-memory provider the unit tests use rather than throwing.
    private void SetHeavyTimeout()
    {
        if (db.Database.IsRelational())
            db.Database.SetCommandTimeout(HeavyReadTimeoutSeconds);
    }

    public async Task<Dictionary<string, DateOnly>> GetLatestDatesAsync(CancellationToken ct = default)
    {
        SetHeavyTimeout();
        return await db.HistoricalCandles
            .GroupBy(c => c.Symbol)
            .Select(g => new { Symbol = g.Key, Max = g.Max(c => c.Date) })
            .ToDictionaryAsync(x => x.Symbol, x => x.Max, StringComparer.OrdinalIgnoreCase, ct);
    }

    public async Task<Dictionary<string, DateOnly>> GetEarliestDatesAsync(CancellationToken ct = default)
    {
        SetHeavyTimeout();
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

    // Loading the whole candle table (10y of daily bars for ~1,500 symbols +
    // SPY + sector ETFs) in a SINGLE query outgrew even the 300s command
    // timeout on the Basic tier - observed 14 Jul 2026 running 302s and
    // tripping the ceiling, killing the optimizer/backtest. Instead, read it
    // in four sequential passes partitioned by symbol: each command moves a
    // quarter of the rows so none is long enough to time out, and running them
    // one-after-another (not in parallel) keeps DTU pressure flat and is safe
    // on the single scoped DbContext. The assembled in-memory result is
    // identical to the old single-query version.
    private const int LoadPartitions = 4;

    public async Task<Dictionary<string, List<HistoricalCandle>>> GetAllBySymbolAsync(CancellationToken ct = default)
    {
        SetHeavyTimeout();
        var symbols = await db.HistoricalCandles.AsNoTracking()
            .Select(c => c.Symbol).Distinct()
            .OrderBy(s => s)
            .ToListAsync(ct);

        var result = new Dictionary<string, List<HistoricalCandle>>(StringComparer.OrdinalIgnoreCase);
        if (symbols.Count == 0) return result;

        var chunkSize = (int)Math.Ceiling(symbols.Count / (double)LoadPartitions);
        for (var i = 0; i < symbols.Count; i += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = symbols.Skip(i).Take(chunkSize).ToList();

            SetHeavyTimeout();
            var rows = await db.HistoricalCandles.AsNoTracking()
                .Where(c => chunk.Contains(c.Symbol))
                .OrderBy(c => c.Symbol).ThenBy(c => c.Date)
                .ToListAsync(ct);

            foreach (var g in rows.GroupBy(c => c.Symbol, StringComparer.OrdinalIgnoreCase))
                result[g.Key] = g.ToList();
        }

        return result;
    }

    public Task<int> CountAsync(CancellationToken ct = default)
    {
        SetHeavyTimeout();
        return db.HistoricalCandles.CountAsync(ct);
    }

    public async Task<DateOnly?> GetMaxDateAsync(CancellationToken ct = default)
    {
        SetHeavyTimeout();
        return await db.HistoricalCandles.AnyAsync(ct) ? await db.HistoricalCandles.MaxAsync(c => c.Date, ct) : null;
    }
}
