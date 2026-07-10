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
    private const int HistoryYears = 3;

    public async Task<CandleSyncResult> SyncAsync(CancellationToken ct = default)
    {
        var apiKey = config["Tiingo:PlatformApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("CandleSync skipped — Tiingo:PlatformApiKey is not configured");
            return new CandleSyncResult(false, 0, 0, 0, 0,
                "Platform Tiingo key not configured — historic market data cannot sync.");
        }

        var symbols = (await universe.GetUniverseAsync(ct)).Prepend("SPY")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var latestDates = await candleRepo.GetLatestDatesAsync(ct);

        using var http = new HttpClient { BaseAddress = new Uri(TiingoBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Token", apiKey);
        var tiingo = RestService.For<ITiingoClient>(http);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var endDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
        int synced = 0, skipped = 0, failed = 0, rows = 0;

        foreach (var symbol in symbols)
        {
            if (ct.IsCancellationRequested) break;

            var from = latestDates.TryGetValue(symbol, out var latest)
                ? latest.AddDays(1)
                : today.AddYears(-HistoryYears);

            if (from >= today) { skipped++; continue; } // already current

            try
            {
                var prices = await tiingo.GetDailyPricesAsync(symbol, from.ToString("yyyy-MM-dd"), endDate);
                if (prices is { Count: > 0 })
                {
                    var candles = prices
                        .Select(p => new HistoricalCandle
                        {
                            Symbol = symbol.ToUpperInvariant(),
                            Date = DateOnly.FromDateTime(p.Date),
                            Open = p.AdjOpen, High = p.AdjHigh, Low = p.AdjLow, Close = p.AdjClose,
                            Volume = p.AdjVolume,
                        })
                        // Belt-and-braces vs the unique (Symbol, Date) index:
                        // Tiingo can include the from-date bar itself.
                        .Where(c => !latestDates.TryGetValue(symbol, out var l) || c.Date > l)
                        .ToList();

                    if (candles.Count > 0)
                    {
                        await candleRepo.AddRangeAsync(candles, ct);
                        rows += candles.Count;
                    }
                }
                synced++;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogDebug(ex, "CandleSync failed for {Symbol} — continuing", symbol);
            }

            // Pace inside Tiingo Power's allowance; irrelevant for the
            // incremental weekly run, matters on the initial 3-year load.
            await Task.Delay(350, ct);
        }

        var summary = $"CandleSync: {synced} synced, {skipped} already current, {failed} failed, {rows:N0} bars added.";
        logger.LogInformation("{Summary}", summary);
        return new CandleSyncResult(true, synced, skipped, failed, rows, summary);
    }
}
