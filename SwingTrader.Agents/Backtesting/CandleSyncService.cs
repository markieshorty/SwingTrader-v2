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
// fetched, so the weekly run is cheap after the initial multi-year load.
public class CandleSyncService(
    IHistoricalCandleRepository candleRepo,
    IMarketUniverseService universe,
    IConfiguration config,
    ILogger<CandleSyncService> logger) : ICandleSyncService
{
    private const string TiingoBaseUrl = "https://api.tiingo.com";
    // Config-driven (Tiingo:SyncDelayMs) since the right pace depends on the
    // key's plan. 400ms default = 2.5 req/s = 9,000/hr, just inside Power's
    // 10k/hr ceiling (the previous hardcoded 350ms was ~10,300/hr - OVER it).
    private const int DefaultSyncDelayMs = 400;
    // 10 years (was 5, was 3): the first optimizer run at 5y failed
    // out-of-sample with a classic small-sample curve-fit - a few hundred
    // train-window trades simply can't discriminate 1,200 candidates.
    // Doubling the window roughly doubles the trades, covers genuinely
    // different regimes (2018 vol spike, 2020 crash, 2022 bear) on both
    // sides of the split, and grows the holdout to ~3 years. The trade-off
    // is amplified survivorship bias (today's universe over a longer past
    // keeps only the winners) - fine for comparing configs against each
    // other, which is all the Lab claims to do, but the absolute numbers
    // inflate further. The existing backfill path below fetches the older
    // slice automatically on the next sync.
    private const int HistoryYears = 10;

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

        var syncDelayMs = int.TryParse(config["Tiingo:SyncDelayMs"], out var d) && d > 0 ? d : DefaultSyncDelayMs;
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

            // Pace inside the plan's allowance; irrelevant for the incremental
            // weekly run, matters on the initial multi-year load.
            await Task.Delay(syncDelayMs, ct);
        }

        // VIX daily history from CBOE's free CSV (Tiingo's EOD endpoint doesn't
        // serve index data). VIX is what lets the backtester detect CRISIS
        // regimes historically - price structure alone can't (see
        // MarketRegimeService.ClassifyRegime: Crisis is VIX > 35). Stored under
        // symbol "VIX" in the same HistoricalCandles table; the backtest's bar
        // load picks it up automatically. Best-effort: a CBOE outage costs the
        // Crisis detection for that run, never the equity sync above.
        try
        {
            rows += await SyncVixAsync(latestDates, windowStart, ct);
            synced++;
        }
        catch (Exception ex)
        {
            failed++;
            logger.LogWarning(ex, "VIX history sync failed — Crisis detection uses existing VIX bars only");
        }

        var summary = $"CandleSync: {synced} synced, {skipped} already current, {failed} failed, {rows:N0} bars added.";
        logger.LogInformation("{Summary}", summary);
        return new CandleSyncResult(true, synced, skipped, failed, rows, summary);
    }

    private const string VixHistoryUrl = "https://cdn.cboe.com/api/global/us_indices/daily_prices/VIX_History.csv";

    private async Task<int> SyncVixAsync(
        Dictionary<string, DateOnly> latestDates, DateOnly windowStart, CancellationToken ct)
    {
        // The full CSV is small (~35 years of daily rows, ~1MB) - fetch whole,
        // filter to the incremental slice locally. No API key needed.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var csv = await http.GetStringAsync(VixHistoryUrl, ct);

        var newerThan = latestDates.TryGetValue("VIX", out var latest) ? latest : DateOnly.MinValue;
        var candles = ParseVixCsv(csv)
            .Where(c => c.Date > newerThan && c.Date >= windowStart)
            .ToList();

        if (candles.Count == 0) return 0;
        await candleRepo.AddRangeAsync(candles, ct);
        logger.LogInformation("VIX history: {Count} bars added (through {Latest})", candles.Count, candles[^1].Date);
        return candles.Count;
    }

    // CBOE CSV format: header "DATE,OPEN,HIGH,LOW,CLOSE" then one row per
    // trading day (DATE = M/d/yyyy). Internal static so the parse is testable.
    internal static List<HistoricalCandle> ParseVixCsv(string csv)
    {
        var result = new List<HistoricalCandle>();
        foreach (var line in csv.Split('\n').Skip(1))
        {
            var parts = line.Trim().Split(',');
            if (parts.Length < 5) continue;
            if (!DateOnly.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var date)) continue;
            if (!decimal.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var open)
                || !decimal.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var high)
                || !decimal.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out var low)
                || !decimal.TryParse(parts[4], System.Globalization.CultureInfo.InvariantCulture, out var close)) continue;
            result.Add(new HistoricalCandle
            {
                Symbol = "VIX", Date = date,
                Open = open, High = high, Low = low, Close = close, Volume = 0,
            });
        }
        return result.OrderBy(c => c.Date).ToList();
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
