using System.IO.Compression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Refit;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Agents.Backtesting;

public record DelistedBackfillResult(
    bool Configured,
    bool DryRun,
    int CandidateSymbols,
    int SymbolsStored,
    int SymbolsScreenedOut,
    int SymbolsFailed,
    int RowsAdded,
    decimal DbSizeMbBefore,
    decimal ProjectedGrowthMb,
    bool SizeGateBlocked,
    string Summary,
    // Candidates left after this chunk. > 0 = the consumer should enqueue a
    // continuation message; the whole run is a chain of bounded chunks so a
    // host restart or lock expiry only ever costs one chunk of progress.
    int RemainingCandidates = 0);

public interface IDelistedBackfillService
{
    Task<DelistedBackfillResult> RunAsync(bool dryRun, CancellationToken ct = default);
}

// Survivorship-free dataset backfill (docs/survivorship-plan P1): fetches
// bars for US symbols that DELISTED inside the dataset window, so backtests
// can buy the companies that later died - the population an oversold-recovery
// strategy most needs to see. One-time (then rare) manual run, platform
// Tiingo key, gated on the Basic tier's 2 GB database cap.
public class DelistedBackfillService(
    IHistoricalCandleRepository candleRepo,
    ISymbolLifecycleRepository lifecycleRepo,
    IConfiguration config,
    ILogger<DelistedBackfillService> logger) : IDelistedBackfillService
{
    private const string TiingoBaseUrl = "https://api.tiingo.com";
    private const string SupportedTickersUrl = "https://apimedia.tiingo.com/docs/tiingo/daily/supported_tickers.zip";
    private const int DefaultSyncDelayMs = 400;
    private const int HistoryYears = 10;              // mirrors CandleSyncService
    private const int MinListingDays = 180;           // sub-6-month listings can't matter
    // Abort the REAL run if current + projected exceeds this (Basic tier caps
    // at 2,048 MB; leave headroom for normal growth and index overhead).
    private const decimal MaxProjectedDbMb = 1_600m;
    // Dry-run growth estimate per stored symbol: ~700 bars x ~90 bytes of row
    // + index. Deliberately pessimistic; the real run measures as it goes.
    private const decimal EstimatedMbPerSymbol = 0.09m;
    // Screen thresholds mirror HistoricBacktester.BuildWatchlist - a symbol
    // that never passes the screen can never enter a backtest, so its bars
    // are pure dead weight in a capped database.
    private const decimal MinScreenPrice = 15m;
    private const decimal MaxScreenPrice = 500m;
    private const decimal MinDollarVolume = 10_000_000m;
    // Candidates per invocation. ~1s each (Tiingo pacing + inserts) keeps a
    // chunk to ~7-10 minutes - far inside any host recycle window, and each
    // chunk COMPLETES its message so retries never re-walk finished work.
    private const int ChunkSize = 400;

    public async Task<DelistedBackfillResult> RunAsync(bool dryRun, CancellationToken ct = default)
    {
        var apiKey = config["Tiingo:PlatformApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            return new DelistedBackfillResult(false, dryRun, 0, 0, 0, 0, 0, 0, 0, false,
                "Platform Tiingo key not configured — delisted backfill cannot run.");

        var dbSizeMb = await candleRepo.GetDatabaseSizeMbAsync(ct);
        var windowStart = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-HistoryYears);

        // Candidate universe from Tiingo's free supported-tickers file.
        List<TickerListing> candidates;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
        {
            var zipBytes = await http.GetByteArrayAsync(SupportedTickersUrl, ct);
            candidates = ParseSupportedTickers(zipBytes, windowStart, DateOnly.FromDateTime(DateTime.UtcNow));
        }

        // Symbols we already hold bars for (alive universe, or a previous
        // partial backfill) are skipped - and so are candidates already
        // PROCESSED (stored or screened out): every processed candidate gets
        // a lifecycle row, so a resumed run never re-fetches finished work.
        var existing = await candleRepo.GetLatestDatesAsync(ct);
        var processed = await lifecycleRepo.GetAllAsync(ct);
        candidates = candidates
            .Where(c => !existing.ContainsKey(c.Symbol) && !processed.ContainsKey(c.Symbol))
            .ToList();

        var projectedMb = candidates.Count * EstimatedMbPerSymbol;
        var gateBlocked = dbSizeMb + projectedMb > MaxProjectedDbMb;

        if (dryRun)
        {
            var drySummary =
                $"Dry run: {candidates.Count} delisted candidate(s) in window ({windowStart:yyyy-MM-dd}+, ≥{MinListingDays}d listed). " +
                $"DB {dbSizeMb:N0} MB now, ~{projectedMb:N0} MB projected growth (upper bound — screening will store fewer). " +
                (gateBlocked
                    ? $"⚠️ EXCEEDS the {MaxProjectedDbMb:N0} MB gate — raise the liquidity floor or shorten the window before running."
                    : $"Inside the {MaxProjectedDbMb:N0} MB gate — safe to run.");
            logger.LogInformation("{Summary}", drySummary);
            return new DelistedBackfillResult(true, true, candidates.Count, 0, 0, 0, 0, dbSizeMb, projectedMb, gateBlocked, drySummary);
        }

        if (gateBlocked)
        {
            var blocked =
                $"Delisted backfill BLOCKED: DB {dbSizeMb:N0} MB + projected ~{projectedMb:N0} MB exceeds the {MaxProjectedDbMb:N0} MB gate " +
                $"(Basic tier caps at 2,048 MB). Nothing was written.";
            logger.LogWarning("{Summary}", blocked);
            return new DelistedBackfillResult(true, false, candidates.Count, 0, 0, 0, 0, dbSizeMb, projectedMb, true, blocked);
        }

        using var tiingoHttp = new HttpClient { BaseAddress = new Uri(TiingoBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        tiingoHttp.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Token", apiKey);
        var tiingo = RestService.For<ITiingoClient>(tiingoHttp);
        var syncDelayMs = int.TryParse(config["Tiingo:SyncDelayMs"], out var d) && d > 0 ? d : DefaultSyncDelayMs;

        var chunk = candidates.Take(ChunkSize).ToList();
        var remaining = candidates.Count - chunk.Count;

        int stored = 0, screenedOut = 0, failed = 0, rows = 0;
        foreach (var c in chunk)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var from = c.StartDate is { } s && s > windowStart ? s : windowStart;
                var prices = await tiingo.GetDailyPricesAsync(
                    c.Symbol, from.ToString("yyyy-MM-dd"), c.EndDate.ToString("yyyy-MM-dd"));

                var candles = (prices ?? [])
                    .Select(p => new HistoricalCandle
                    {
                        Symbol = c.Symbol.ToUpperInvariant(),
                        Date = DateOnly.FromDateTime(p.Date),
                        Open = p.AdjOpen, High = p.AdjHigh, Low = p.AdjLow, Close = p.AdjClose,
                        Volume = p.AdjVolume,
                    })
                    .OrderBy(x => x.Date)
                    .ToList();

                if (!EverPassesScreen(candles))
                {
                    // Record the verdict so retries and continuation chunks
                    // never re-fetch this symbol's bars again.
                    await lifecycleRepo.AddAsync(new SymbolLifecycle
                    {
                        Symbol = c.Symbol.ToUpperInvariant(),
                        ListedAt = c.StartDate,
                        DelistedAt = c.EndDate,
                        EndReason = null,
                        BarsStored = false,
                    }, ct);
                    screenedOut++;
                }
                else
                {
                    await candleRepo.AddRangeAsync(candles, ct);
                    await lifecycleRepo.AddAsync(new SymbolLifecycle
                    {
                        Symbol = c.Symbol.ToUpperInvariant(),
                        ListedAt = c.StartDate,
                        DelistedAt = c.EndDate,
                        EndReason = null, // unknown - the source carries no reason; P2's haircut applies
                    }, ct);
                    stored++;
                    rows += candles.Count;
                }
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogDebug(ex, "Delisted backfill failed for {Symbol} — continuing", c.Symbol);
            }
            await Task.Delay(syncDelayMs, ct);
        }

        var isFinalChunk = remaining == 0;
        if (isFinalChunk && await lifecycleRepo.CountStoredAsync(ct) > 0)
            await candleRepo.BumpDatasetVersionAsync(ct);

        var dbAfter = await candleRepo.GetDatabaseSizeMbAsync(ct);
        var totalStored = await lifecycleRepo.CountStoredAsync(ct);
        var summary = isFinalChunk
            ? $"Delisted backfill COMPLETE: {totalStored} symbol(s) stored across all chunks, DB now {dbAfter:N0} MB. " +
              "Dataset version bumped — prior backtest results are no longer comparable with new runs."
            : $"Delisted backfill chunk: {stored} stored ({rows:N0} bars), {screenedOut} screened out, {failed} failed " +
              $"— {remaining} candidate(s) remaining, continuing. DB {dbAfter:N0} MB.";
        logger.LogInformation("{Summary}", summary);
        return new DelistedBackfillResult(true, false, candidates.Count, stored, screenedOut, failed, rows, dbSizeMb, dbAfter - dbSizeMb, false, summary, remaining);
    }

    internal sealed record TickerListing(string Symbol, DateOnly? StartDate, DateOnly EndDate);

    // supported_tickers.csv columns: ticker,exchange,assetType,priceCurrency,startDate,endDate
    internal static List<TickerListing> ParseSupportedTickers(byte[] zipBytes, DateOnly windowStart, DateOnly today)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("supported_tickers.zip contained no CSV entry");
        using var reader = new StreamReader(entry.Open());

        var results = new List<TickerListing>();
        string? line = reader.ReadLine(); // header
        var exchanges = new HashSet<string>(["NYSE", "NASDAQ", "AMEX", "NYSE ARCA", "NYSE MKT"], StringComparer.OrdinalIgnoreCase);
        // Symbols must look like real common-stock tickers - warrants/units
        // and preferreds carry suffixes (-WS, -U, -P) that the strategy never
        // trades and the screen would exclude anyway.
        static bool PlainTicker(string t) => t.Length is >= 1 and <= 5 && t.All(char.IsAsciiLetterUpper);

        while ((line = reader.ReadLine()) is not null)
        {
            var parts = line.Split(',');
            if (parts.Length < 6) continue;
            var ticker = parts[0].Trim().ToUpperInvariant();
            var exchange = parts[1].Trim();
            var assetType = parts[2].Trim();
            var currency = parts[3].Trim();

            if (!PlainTicker(ticker)) continue;
            if (!exchanges.Contains(exchange)) continue;
            if (!string.Equals(assetType, "Stock", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase)) continue;

            DateOnly? start = DateOnly.TryParse(parts[4].Trim(), out var sd) ? sd : null;
            if (!DateOnly.TryParse(parts[5].Trim(), out var end)) continue;

            // Delisted = the listing ENDED well before today (a live symbol's
            // endDate tracks the latest bar; 30 days of slack separates the
            // two), inside the dataset window, after a meaningful life.
            if (end >= today.AddDays(-30)) continue;
            if (end < windowStart) continue;
            if (start is { } st && end.DayNumber - st.DayNumber < MinListingDays) continue;

            results.Add(new TickerListing(ticker, start, end));
        }
        return results;
    }

    // Mirrors HistoricBacktester.BuildWatchlist's liquidity/price screen: any
    // bar (with 20 days of history behind it) inside the price band whose
    // 20-day average dollar volume clears the floor. A symbol that never
    // passes can never be selected by a backtest.
    internal static bool EverPassesScreen(IReadOnlyList<HistoricalCandle> candles)
    {
        if (candles.Count < 21) return false;
        for (var i = 20; i < candles.Count; i++)
        {
            var close = candles[i].Close;
            if (close < MinScreenPrice || close > MaxScreenPrice) continue;
            decimal volSum = 0;
            for (var j = i - 19; j <= i; j++) volSum += candles[j].Volume;
            if (volSum / 20m * close >= MinDollarVolume) return true;
        }
        return false;
    }
}
