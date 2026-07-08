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
    IAccountRepository accountRepo,
    IMarketUniverseService universeService,
    IOptions<WatchlistConfig> config,
    ILogger<StockScreener> logger) : IStockScreener
{
    public async Task<ScreenResult> ScreenAsync(int accountId, IFinnhubClient finnhub, CancellationToken ct = default)
    {
        var cfg = config.Value;

        var account = await accountRepo.GetAsync(accountId, ct)
            ?? throw new InvalidOperationException($"Account {accountId} not found.");

        // Dynamic universe (live S&P 500/Nasdaq 100 constituents, cached for
        // UniverseCacheDays) replaces the old hardcoded symbol list, so the
        // screening pool stays current and captures index-rebalance
        // momentum automatically rather than going stale between builds.
        var fullUniverse = await universeService.GetUniverseAsync(ct);
        if (fullUniverse.Count == 0)
        {
            logger.LogError("Universe fetch failed — watchlist refresh aborted. Check Finnhub index endpoints.");
            return new ScreenResult([], 0, 0);
        }

        var activeSymbols = (await watchlist.GetActiveAsync(accountId))
            .Select(w => w.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var openTradeSymbols = (await trades.GetOpenTradesAsync(accountId, account.TradingMode))
            .Select(t => t.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var universe = fullUniverse
            .Where(s => !activeSymbols.Contains(s) && !openTradeSymbols.Contains(s))
            .ToList();

        logger.LogInformation("Screening {Count} symbols from universe via Finnhub", universe.Count);

        var candidates = new List<ScreenedCandidate>();
        var semaphore = new SemaphoreSlim(5);
        var failedQuotes = new System.Collections.Concurrent.ConcurrentBag<string>();

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
                failedQuotes.Add(symbol);
                logger.LogDebug(ex, "Quote fetch failed for {Symbol} — skipping", symbol);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        var failedCount = failedQuotes.Count;
        if (failedCount > 0)
        {
            // A handful of failures per run is normal noise (delisted tickers,
            // transient network blips). A large chunk failing together is more
            // likely a systemic Finnhub problem (rate limiting, outage) quietly
            // shrinking the candidate pool with no other signal - same concern
            // ResearchConsumerFunction already surfaces for its own per-symbol
            // failures via "N of M symbol(s) could not be rescored".
            var failedPct = (double)failedCount / universe.Count;
            if (failedPct > 0.2)
                logger.LogWarning(
                    "Screener failed to fetch quotes for {Failed} of {Total} universe symbols ({Pct:P0}) — " +
                    "candidate pool may be smaller than usual this run",
                    failedCount, universe.Count, failedPct);
            else
                logger.LogDebug("Screener failed to fetch quotes for {Failed} of {Total} universe symbols", failedCount, universe.Count);
        }

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
        return new ScreenResult(results, universe.Count, failedCount);
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
