using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Interfaces;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.RateLimiting;

namespace SwingTrader.Agents.Watchlist;

public class StockScreener(
    IRateLimiter rateLimiter,
    IWatchlistRepository watchlist,
    ITradeRepository trades,
    IOptions<WatchlistConfig> config,
    ILogger<StockScreener> logger) : IStockScreener
{
    public async Task<List<ScreenedCandidate>> ScreenAsync(int accountId, IFinnhubClient finnhub, CancellationToken ct = default)
    {
        var cfg = config.Value;

        var activeSymbols = (await watchlist.GetActiveAsync(accountId))
            .Select(w => w.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var openTradeSymbols = (await trades.GetOpenTradesAsync(accountId))
            .Select(t => t.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var universe = StockUniverse.Symbols
            .Where(s => !activeSymbols.Contains(s) && !openTradeSymbols.Contains(s))
            .ToList();

        logger.LogInformation("Screening {Count} symbols from universe via Finnhub", universe.Count);

        var candidates = new List<ScreenedCandidate>();
        var semaphore = new SemaphoreSlim(5);

        var tasks = universe.Select(async symbol =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                await rateLimiter.WaitAsync(ct);
                var quote = await finnhub.GetQuoteAsync(symbol);

                if (quote.CurrentPrice is null or <= 0 || quote.PreviousClose is null or <= 0) return;

                var price = quote.CurrentPrice.Value;
                var changePerc = quote.PercentChange ?? 0m;
                var absChange = Math.Abs(changePerc);

                if (price < cfg.MinPrice || price > cfg.MaxPrice) return;
                if (absChange < cfg.MinAbsChangePercent || absChange > cfg.MaxAbsChangePercent) return;

                lock (candidates)
                {
                    candidates.Add(new ScreenedCandidate(
                        symbol, symbol, price, changePerc, 0m, string.Empty));
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Quote fetch failed for {Symbol} — skipping", symbol);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        var results = candidates
            .OrderByDescending(c => Math.Abs(c.ChangePercent))
            .Take(cfg.MaxCandidatesForClaude)
            .ToList();

        logger.LogInformation("Screener produced {Count} candidates from {Universe} universe symbols",
            results.Count, universe.Count);
        return results;
    }
}
