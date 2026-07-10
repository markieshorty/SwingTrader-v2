using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Refit;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.Market;

namespace SwingTrader.Agents.Backtesting;

public record CandleSyncResult(bool Configured, int SymbolsSynced, int SymbolsSkipped, int SymbolsFailed, int RowsAdded, string Summary);

public interface ICandleSyncService
{
    Task<CandleSyncResult> SyncAsync(CancellationToken ct = default);
}

// Fills the shared HistoricalCandles table (platform-level market data - one
// copy for all accounts) with adjusted daily bars for the screening universe
// plus SPY. Uses the PLATFORM Tiingo key (Tiingo:PlatformApiKey, Key Vault:
// Tiingo--PlatformApiKey), never per-user keys: free-tier Tiingo caps unique
// symbols per month far below the ~1,500-symbol universe, so individual users
// could never sync it - and the data is identical for everyone anyway.
// Incremental: only bars newer than each symbol's latest stored date are
// fetched, so the weekly run is cheap after the initial 3-year load.
public class CandleSyncService(
    IHistoricalCandleRepository candleRepo,
    IMarketUniverseService universe,
    IConfiguration config,
    ILogger<CandleSyncService> logger) : ICandleSyncService
{
    private const string TiingoBaseUrl = "https://api.tiingo.com";
    // 5 years so the optimizer's train/holdout split has enough trades on both
    // sides to distinguish a real edge from noise (was 3).
    private const int HistoryYears = 5;

    public async Task<CandleSyncResult> SyncAsync(CancellationToken ct = default)
    {
        var apiKey = config["Tiingo:PlatformApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("CandleSync skipped — Tiingo:PlatformApiKey is not configured");
            return new CandleSyncResult(false, 0, 0, 0, 0,
                "Platform Tiingo key not configured — historic market data cannot sync.");
        }

        // SPY anchors the trading calendar; the sector ETFs feed the
        // backtester's relative-strength component (same SectorEtfMap the live
        // scorer uses - a stock's RS is its 5d return vs its sector ETF's).
        var symbols = (await universe.GetUniverseAsync(ct))
            .Prepend("SPY")
            .Concat(SectorEtfMap.AllEtfs())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var latestDates = await candleRepo.GetLatestDatesAsync(ct);
        var earliestDates = await candleRepo.GetEarliestDatesAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri(TiingoBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Token", apiKey);
        var tiingo = RestService.For<ITiingoClient>(http);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var endDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
        int synced = 0, skipped = 0, failed = 0, rows = 0;

        var windowStart = today.AddYears(-HistoryYears);

        foreach (var symbol in symbols)
        {
            if (ct.IsCancellationRequested) break;

            // Forward fill: newer bars than the latest we hold.
            var from = latestDates.TryGetValue(symbol, out var latest)
                ? latest.AddDays(1)
                : windowStart;

            // Backfill: when the configured window grows (3y -> 5y), symbols
            // that already hold data also need the older slice fetched once.
            // A ~40-day tolerance avoids re-requesting symbols that simply
            // didn't trade in the window's first few weeks (new listings).
            var needsBackfill = earliestDates.TryGetValue(symbol, out var earliest)
                && earliest > windowStart.AddDays(40);

            if (from >= today && !needsBackfill) { skipped++; continue; } // already current

            try
            {
                var anyFetched = false;
                if (from < today)
                    anyFetched |= await FetchRangeAsync(tiingo, symbol, from, DateOnly.FromDateTime(DateTime.UtcNow),
                        d => !latestDates.TryGetValue(symbol, out var l) || d > l, r => rows += r, ct);

                if (needsBackfill)
                    anyFetched |= await FetchRangeAsync(tiingo, symbol, windowStart, earliest.AddDays(-1),
                        d => d < earliest, r => rows += r, ct);

                if (anyFetched || from < today || needsBackfill) synced++;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogDebug(ex, "CandleSync failed for {Symbol} — continuing", symbol);
            }

            // Pace inside Tiingo Power's allowance; irrelevant for the
            // incremental weekly run, matters on the initial multi-year load.
            await Task.Delay(350, ct);
        }

        var summary = $"CandleSync: {synced} synced, {skipped} already current, {failed} failed, {rows:N0} bars added.";
        logger.LogInformation("{Summary}", summary);
        return new CandleSyncResult(true, synced, skipped, failed, rows, summary);
    }

    // Fetches one date range for one symbol and stores the bars that pass the
    // keep-filter (guards the unique (Symbol, Date) index against overlap at
    // the range edges). Returns whether any rows were stored.
    private async Task<bool> FetchRangeAsync(
        ITiingoClient tiingo, string symbol, DateOnly from, DateOnly to,
        Func<DateOnly, bool> keep, Action<int> addRows, CancellationToken ct)
    {
        if (from > to) return false;
        var prices = await tiingo.GetDailyPricesAsync(symbol, from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
        if (prices is not { Count: > 0 }) return false;

        var candles = prices
            .Select(p => new HistoricalCandle
            {
                Symbol = symbol.ToUpperInvariant(),
                Date = DateOnly.FromDateTime(p.Date),
                Open = p.AdjOpen, High = p.AdjHigh, Low = p.AdjLow, Close = p.AdjClose,
                Volume = p.AdjVolume,
            })
            .Where(c => keep(c.Date))
            .ToList();

        if (candles.Count == 0) return false;
        await candleRepo.AddRangeAsync(candles, ct);
        addRows(candles.Count);
        return true;
    }
}
