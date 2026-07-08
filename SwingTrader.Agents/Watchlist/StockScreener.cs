using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Interfaces;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Market;
using SwingTrader.Infrastructure.RateLimiting;

namespace SwingTrader.Agents.Watchlist;

public class StockScreener(
    IFinnhubRateLimiter rateLimiter,
    IWatchlistRepository watchlist,
    ITradeRepository trades,
    IMarketUniverseService universeService,
    IOptions<WatchlistConfig> config,
    ILogger<StockScreener> logger) : IStockScreener
{
    public async Task<List<ScreenedCandidate>> ScreenAsync(int accountId, IFinnhubClient finnhub, CancellationToken ct = default)
    {
        var cfg = config.Value;

        // Dynamic universe (live S&P 500/Nasdaq 100 constituents, cached for
        // UniverseCacheDays) replaces the old hardcoded symbol list, so the
        // screening pool stays current and captures index-rebalance
        // momentum automatically rather than going stale between builds.
        var fullUniverse = await universeService.GetUniverseAsync(ct);
        if (fullUniverse.Count == 0)
        {
            logger.LogError("Universe fetch failed — watchlist refresh aborted. Check Finnhub index endpoints.");
            return [];
        }

        var activeSymbols = (await watchlist.GetActiveAsync(accountId))
            .Select(w => w.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var openTradeSymbols = (await trades.GetOpenTradesAsync(accountId))
            .Select(t => t.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var universe = fullUniverse
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

        // Per-account toggle (Watchlist.TopMoversEnabled on the default
        // AiManaged watchlist), settable from the /watchlists UI - not a
        // global on/off switch, since different accounts may want a wider
        // or narrower candidate net.
        if (await watchlist.IsTopMoversEnabledAsync(accountId, ct))
            await MergeTopMoversAsync(candidates, activeSymbols, openTradeSymbols, cfg, finnhub, ct);

        // TopMoverOrderBoost nudges top movers up the ranking without hard-pinning
        // them above everything else regardless of how small their move is.
        var results = candidates
            .OrderByDescending(c => Math.Abs(c.ChangePercent) * (c.IsTopMover ? cfg.TopMoverOrderBoost : 1m))
            .Take(cfg.MaxCandidatesForClaude)
            .ToList();

        logger.LogInformation("Screener produced {Count} candidates from {Universe} universe symbols ({TopMovers} top movers)",
            results.Count, universe.Count, results.Count(c => c.IsTopMover));
        return results;
    }

    // Supplementary candidate source: Finnhub's top gainers/losers/most-active
    // lists, layered on top of the index-based universe rather than
    // replacing it. Off by default (WatchlistConfig.TopMoversEnabled) since
    // it can surface symbols outside the usual S&P 500/Nasdaq 100 universe.
    private async Task MergeTopMoversAsync(
        List<ScreenedCandidate> candidates,
        HashSet<string> activeSymbols,
        HashSet<string> openTradeSymbols,
        WatchlistConfig cfg,
        IFinnhubClient finnhub,
        CancellationToken ct)
    {
        List<MarketMoverItem> movers;
        try
        {
            await rateLimiter.WaitAsync(ct);
            var gainers = await finnhub.GetTopGainersAsync();
            await rateLimiter.WaitAsync(ct);
            var losers = await finnhub.GetTopLosersAsync();
            await rateLimiter.WaitAsync(ct);
            var mostActive = await finnhub.GetMostActiveAsync();
            movers = gainers.Concat(losers).Concat(mostActive).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Top movers fetch failed — continuing with the index universe only");
            return;
        }

        var byIndex = candidates.ToDictionary(c => c.Symbol, StringComparer.OrdinalIgnoreCase);

        foreach (var mover in movers.DistinctBy(m => m.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            if (activeSymbols.Contains(mover.Symbol) || openTradeSymbols.Contains(mover.Symbol)) continue;

            var absChange = Math.Abs(mover.ChangePercent);
            if (mover.Price < cfg.MinPrice || mover.Price > cfg.MaxPrice) continue;
            if (absChange < cfg.MinAbsChangePercent || absChange > cfg.MaxAbsChangePercent) continue;

            if (byIndex.TryGetValue(mover.Symbol, out var existing))
            {
                // Already in the pool from the index universe - just flag it,
                // rather than adding a duplicate entry.
                var upgraded = existing with { IsTopMover = true };
                candidates.Remove(existing);
                candidates.Add(upgraded);
                byIndex[mover.Symbol] = upgraded;
            }
            else
            {
                var added = new ScreenedCandidate(
                    mover.Symbol, mover.Name, mover.Price, mover.ChangePercent, mover.Volume, string.Empty, IsTopMover: true);
                candidates.Add(added);
                byIndex[mover.Symbol] = added;
            }
        }
    }
}
