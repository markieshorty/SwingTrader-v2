using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Market;
using SwingTrader.Infrastructure.RateLimiting;
using SwingTrader.Infrastructure.Services;

namespace SwingTrader.Agents.Watchlist;

public class StockScreener(
    IFinnhubRateLimiter rateLimiter,
    IWatchlistRepository watchlist,
    ITradeRepository trades,
    IAccountRepository accountRepo,
    IMarketUniverseService universeService,
    IHistoricalCandleRepository historicCandles,
    IIndicatorService indicators,
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

        // Exclude symbols already tracked on ANY enabled watchlist, not just
        // the default AI-managed one - otherwise a stock manually added to a
        // custom watchlist could still be screened, selected, and added a
        // second time into the AI-managed list (design flaw: the same symbol
        // ends up double-tracked across two watchlists with two separate
        // add reasons/history entries).
        var activeSymbols = (await watchlist.GetAllEnabledSymbolsAsync(accountId, ct))
            .Select(w => w.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var openTradeSymbols = (await trades.GetOpenTradesAsync(accountId, account.TradingMode))
            .Select(t => t.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var universe = fullUniverse
            .Where(s => !activeSymbols.Contains(s) && !openTradeSymbols.Contains(s))
            .ToList();

        // Union pre-filter (docs/screener-union-plan): narrow the universe to
        // per-setup candidates from LOCAL history before spending a quote on
        // anything. This both raises resolution - each detector gets
        // candidates on its own terms instead of sharing one
        // volatility-expansion ranking - and cuts the quote count, since only
        // the union gets fetched rather than all ~1,500 names.
        var surfacedBy = new Dictionary<string, List<SetupType>>(StringComparer.OrdinalIgnoreCase);
        var unionOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (cfg.UnionScreenEnabled)
        {
            var entries = await BuildSetupUnionAsync(universe, cfg, ct);
            if (entries.Count >= cfg.MinUnionCandidates)
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    surfacedBy[entries[i].Symbol] = entries[i].Setups;
                    unionOrder[entries[i].Symbol] = i;
                }
                universe = universe.Where(surfacedBy.ContainsKey).ToList();
                logger.LogInformation("Union screen narrowed the universe to {Count} per-setup candidates", universe.Count);
            }
            else
            {
                // Fail open to the legacy full-universe screen. A thin union
                // means the candle store is stale or empty, and a starved
                // watchlist is far worse than a lower-resolution one.
                logger.LogWarning(
                    "Union screen produced only {Count} candidates (min {Min}) — falling back to the full universe",
                    entries.Count, cfg.MinUnionCandidates);
                surfacedBy.Clear();
                unionOrder.Clear();
            }
        }

        logger.LogInformation("Screening {Count} symbols from universe via Finnhub", universe.Count);

        var candidates = new List<ScreenedCandidate>();
        var semaphore = new SemaphoreSlim(5);
        var failedQuotes = new System.Collections.Concurrent.ConcurrentBag<string>();

        var unionActive = surfacedBy.Count > 0;
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

                // The minimum-move floor is the thing that starved the
                // trend-state setups, so under union mode it must NOT apply -
                // a TrendFollowing candidate on a +0.3% day is exactly what we
                // just went to the trouble of finding. The MAXIMUM stays as a
                // sanity guard against halted or otherwise broken quotes.
                if (absChange > cfg.MaxAbsChangePercent) return;
                if (!unionActive && absChange < cfg.MinAbsChangePercent) return;

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

        // Cross-sectional percentile over the WHOLE screened universe (before
        // the liquidity cut, so the denominator is every name that competed
        // today, not just the survivors). Inert metadata for now - it rides
        // through selection onto watchlist items and signals so the scorecard
        // can judge whether it predicts anything before it drives decisions.
        candidates = CrossSectionalRanker.StampPercentiles(candidates);

        // TopMoverOrderBoost nudges top movers up the ranking without hard-pinning
        // them above everything else regardless of how small their move is.
        // Under union mode the allocation order already balances the setups;
        // re-sorting by price move here would undo it entirely.
        var ranked = unionActive
            ? candidates
                .OrderBy(c => unionOrder.TryGetValue(c.Symbol, out var i) ? i : int.MaxValue)
                .ToList()
            : candidates
                .OrderByDescending(c => Math.Abs(c.ChangePercent) * (c.IsTopMover ? cfg.TopMoverOrderBoost : 1m))
                .ToList();

        // Liquidity floor. Applied here (walking the ranking) rather than in
        // the quote loop above because the quote endpoint returns no volume at
        // all - which is also why the old MinDailyVolume knob never filtered
        // anything - so liquidity needs a candles call per symbol, and those
        // are only worth spending on candidates that would actually make the
        // Claude cut.
        var results = await ApplyLiquidityFloorAsync(ranked, cfg, finnhub, ct);

        logger.LogInformation("Screener produced {Count} candidates from {Universe} universe symbols ({TopMovers} top movers)",
            results.Count, universe.Count, results.Count(c => c.IsTopMover));
        return new ScreenResult(results, universe.Count, failedCount);
    }

    // Per-setup candidate pools computed from the LOCAL candle store - one
    // bulk read, no API calls, no Claude tokens. Indicators are as of the last
    // stored bar, which may trail the live session; that is fine because this
    // only decides who gets LOOKED at. The authoritative classification still
    // happens in ResearchPipeline.DetectSetup on current candles.
    private async Task<List<SetupUnionEntry>> BuildSetupUnionAsync(
        List<string> universe, WatchlistConfig cfg, CancellationToken ct)
    {
        try
        {
            // Enough history for a 26-period MACD and a 20-period Bollinger,
            // with room for weekends and holidays.
            var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-cfg.UnionHistoryDays));
            var bySymbol = await historicCandles.GetAllBySymbolAsync(from, ct);

            var inUniverse = universe.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidacies = new List<SetupCandidacy>();

            foreach (var (symbol, bars) in bySymbol)
            {
                ct.ThrowIfCancellationRequested();
                if (!inUniverse.Contains(symbol) || bars.Count < cfg.UnionMinBars) continue;

                var ordered = bars.OrderBy(b => b.Date).ToList();
                var candles = ordered
                    .Select(b => new CandleData(
                        b.Date.ToDateTime(TimeOnly.MinValue), b.Open, b.High, b.Low, b.Close, (long)b.Volume))
                    .ToList();

                var ind = indicators.Calculate(candles);
                var price = ordered[^1].Close;
                var fourBack = ordered.Count >= 4 ? ordered[^4].Close : (decimal?)null;

                candidacies.AddRange(SetupScreens.Evaluate(symbol, ind, price, fourBack));
            }

            var union = SetupScreens.Union(candidacies, cfg.PerSetupCandidates);

            foreach (var pool in candidacies.GroupBy(c => c.Setup))
                logger.LogInformation("Union screen: {Setup} had {Available} eligible, {Taken} taken",
                    pool.Key, pool.Count(), Math.Min(pool.Count(), cfg.PerSetupCandidates));

            return union;
        }
        catch (Exception ex)
        {
            // Never let the pre-filter break the screen - the legacy path is
            // a complete, working screener on its own.
            logger.LogError(ex, "Union screen failed — falling back to the full universe");
            return [];
        }
    }

    // Walks the ranked candidates keeping only those whose 20-day average
    // dollar volume (avg shares x current price) clears MinDollarVolume, until
    // MaxCandidatesForClaude are kept. This is what makes the S&P 400/600
    // small caps in the widened universe safe to actually trade - an illiquid
    // name can gap through a stop or be impossible to exit at target with a
    // sized position. Kept candidates get their real average volume filled in
    // so Claude's prompt shows meaningful numbers instead of 0.
    //
    // Fail-open on candle errors (keep, volume unknown): a systemic Finnhub
    // blip shrinking the pool to nothing would be worse than occasionally
    // passing an unverified name through. Fail-closed on confirmed-illiquid.
    // Attempts are capped so a pathological day can't turn this into hundreds
    // of extra candle calls.
    private async Task<List<ScreenedCandidate>> ApplyLiquidityFloorAsync(
        List<ScreenedCandidate> ranked, WatchlistConfig cfg, IFinnhubClient finnhub, CancellationToken ct)
    {
        var kept = new List<ScreenedCandidate>(cfg.MaxCandidatesForClaude);
        var maxAttempts = cfg.MaxCandidatesForClaude * 2;
        var attempts = 0;
        var droppedIlliquid = 0;
        var now = DateTimeOffset.UtcNow;
        var from = now.AddDays(-30).ToUnixTimeSeconds();

        foreach (var candidate in ranked)
        {
            if (kept.Count >= cfg.MaxCandidatesForClaude || attempts >= maxAttempts || ct.IsCancellationRequested)
                break;
            attempts++;

            try
            {
                await rateLimiter.WaitAsync(ct);
                var candles = await finnhub.GetCandlesAsync(candidate.Symbol, "D", from, now.ToUnixTimeSeconds());
                if (candles.Status != "ok" || candles.Volume is not { Count: > 0 })
                {
                    kept.Add(candidate); // no data - fail open
                    continue;
                }

                var avgVolume = (decimal)candles.Volume.TakeLast(20).Average();
                var dollarVolume = avgVolume * candidate.LastPrice;
                if (dollarVolume >= cfg.MinDollarVolume)
                {
                    kept.Add(candidate with { Volume = Math.Round(avgVolume) });
                }
                else
                {
                    droppedIlliquid++;
                    logger.LogDebug(
                        "Dropped {Symbol}: avg dollar volume {DollarVolume:N0} below the {Floor:N0} liquidity floor",
                        candidate.Symbol, dollarVolume, cfg.MinDollarVolume);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Liquidity check failed for {Symbol} — keeping (fail-open)", candidate.Symbol);
                kept.Add(candidate);
            }
        }

        if (droppedIlliquid > 0)
            logger.LogInformation("Liquidity floor dropped {Dropped} illiquid candidate(s) (floor: ${Floor:N0}/day)",
                droppedIlliquid, cfg.MinDollarVolume);

        return kept;
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
