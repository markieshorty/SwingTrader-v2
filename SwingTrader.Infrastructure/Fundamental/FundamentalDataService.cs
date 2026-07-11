using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Enums;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.RateLimiting;

namespace SwingTrader.Infrastructure.Fundamental;

public class FundamentalDataService(
    IFinnhubRateLimiter rateLimiter,
    IMemoryCache cache,
    IOptions<FundamentalConfig> config,
    ILogger<FundamentalDataService> logger) : IFundamentalDataService
{
    public async Task<FundamentalSnapshot> GetSnapshotAsync(
        IFinnhubClient finnhub, string symbol, List<FinnhubEarningsEvent> earningsHistory, CancellationToken ct)
    {
        var cacheKey = $"fundamental_{symbol}";
        if (cache.TryGetValue(cacheKey, out FundamentalSnapshot? cached) && cached is not null)
            return cached;

        var cfg = config.Value;

        var (analystTrend, analystCount) = await GetAnalystTrendAsync(finnhub, symbol, cfg, ct);
        var (insiderActivity, buyerCount, sellerCount, netShares, mspr) = await GetInsiderActivityAsync(finnhub, symbol, cfg, ct);
        var (earningsConsistency, surpriseTrendPct) = GetEarningsConsistency(earningsHistory, cfg);
        var revenueDirection = await GetRevenueDirectionAsync(finnhub, symbol, cfg, ct);

        var snapshot = new FundamentalSnapshot(
            symbol, analystTrend, insiderActivity, earningsConsistency, revenueDirection,
            analystCount, buyerCount, sellerCount, netShares, DateTime.UtcNow,
            InsiderMspr: mspr, SurpriseTrendPct: surpriseTrendPct);

        cache.Set(cacheKey, snapshot, TimeSpan.FromDays(cfg.CacheDurationDays));
        return snapshot;
    }

    private async Task<(AnalystTrend Trend, int AnalystCount)> GetAnalystTrendAsync(IFinnhubClient finnhub, string symbol, FundamentalConfig cfg, CancellationToken ct)
    {
        try
        {
            await rateLimiter.WaitAsync(ct);
            var recs = await finnhub.GetAnalystRecommendationsAsync(symbol);
            var periods = recs.OrderBy(r => r.Period).TakeLast(cfg.AnalystLookbackMonths).ToList();

            var totalAnalysts = periods.Sum(p => p.StrongBuy + p.Buy + p.Hold + p.Sell + p.StrongSell);
            if (periods.Count == 0 || totalAnalysts < cfg.MinAnalystsForTrend)
                return (AnalystTrend.Insufficient, totalAnalysts);

            decimal NetScore(AnalystRecommendation r)
            {
                var total = r.StrongBuy + r.Buy + r.Hold + r.Sell + r.StrongSell;
                if (total == 0) return 0m;
                var bullish = (r.StrongBuy * 2m + r.Buy) / (decimal)total;
                var bearish = (r.Sell + r.StrongSell * 2m) / (decimal)total;
                return bullish - bearish;
            }

            var oldest = NetScore(periods[0]);
            var mostRecent = NetScore(periods[^1]);
            var mostRecentTotal = periods[^1].StrongBuy + periods[^1].Buy + periods[^1].Hold + periods[^1].Sell + periods[^1].StrongSell;

            var trend = ClassifyAnalystTrend(oldest, mostRecent, cfg.AnalystVelocityBullish, cfg.AnalystVelocityStrong);
            return (trend, mostRecentTotal);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Analyst recommendations fetch failed for {Symbol}", symbol);
            return (AnalystTrend.Insufficient, 0);
        }
    }

    // Revision VELOCITY drives the classification: the net-bullishness level
    // mostly lags price (analysts chase moves), but the CHANGE in it leads.
    // Previously any hair's-width improvement counted as "trending up" and
    // the level did the real work; now the velocity must clear a real
    // threshold, and the Strongly* tiers need either strong velocity or
    // meaningful velocity on top of an already-positive/negative level.
    internal static AnalystTrend ClassifyAnalystTrend(
        decimal oldestNet, decimal latestNet, decimal velocityBullish, decimal velocityStrong)
    {
        var velocity = latestNet - oldestNet;

        if (velocity >= velocityStrong || (velocity >= velocityBullish && latestNet > 0.3m))
            return AnalystTrend.StronglyBullish;
        if (velocity >= velocityBullish)
            return AnalystTrend.Bullish;
        if (velocity <= -velocityStrong || (velocity <= -velocityBullish && latestNet < -0.3m))
            return AnalystTrend.StronglyBearish;
        if (velocity <= -velocityBullish)
            return AnalystTrend.Bearish;
        return AnalystTrend.Neutral;
    }

    private async Task<(InsiderActivity Activity, int BuyerCount, int SellerCount, decimal? NetShares, decimal? Mspr)> GetInsiderActivityAsync(
        IFinnhubClient finnhub, string symbol, FundamentalConfig cfg, CancellationToken ct)
    {
        var activity = InsiderActivity.Neutral;
        var buyerCount = 0;
        var sellerCount = 0;
        decimal? netShares = null;

        try
        {
            await rateLimiter.WaitAsync(ct);
            var response = await finnhub.GetInsiderTransactionsAsync(symbol);
            var cutoff = DateTime.UtcNow.AddDays(-cfg.InsiderLookbackDays);

            var openMarket = response.Data
                .Where(t => (t.TransactionCode == "P" || t.TransactionCode == "S")
                    && DateTime.TryParse(t.TransactionDate, out var d) && d >= cutoff)
                .ToList();

            if (openMarket.Count > 0)
            {
                netShares = openMarket.Sum(t => t.Change);
                buyerCount = openMarket.Where(t => t.TransactionCode == "P").Select(t => t.Name).Distinct().Count();
                sellerCount = openMarket.Where(t => t.TransactionCode == "S").Select(t => t.Name).Distinct().Count();

                activity = (buyerCount, netShares, sellerCount) switch
                {
                    ( >= 2, > 0, _) => InsiderActivity.StrongBuying,
                    ( >= 1, > 0, _) => InsiderActivity.Buying,
                    (_, _, >= 3) => InsiderActivity.ClusterSelling,
                    _ => InsiderActivity.Neutral,
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Insider transactions fetch failed for {Symbol}", symbol);
        }

        var mspr = await GetAverageMsprAsync(finnhub, symbol, cfg, ct);
        return (CombineWithMspr(activity, mspr, cfg.MsprBullishThreshold, cfg.MsprBearishThreshold),
            buyerCount, sellerCount, netShares, mspr);
    }

    // Finnhub's aggregated Monthly Share Purchase Ratio over the recent
    // months; null = unavailable, and the clustering classification stands
    // unmodified - exactly the pre-MSPR behaviour.
    private async Task<decimal?> GetAverageMsprAsync(
        IFinnhubClient finnhub, string symbol, FundamentalConfig cfg, CancellationToken ct)
    {
        try
        {
            await rateLimiter.WaitAsync(ct);
            var to = DateTime.UtcNow;
            var from = to.AddMonths(-cfg.InsiderMsprLookbackMonths);
            var response = await finnhub.GetInsiderSentimentAsync(
                symbol, from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
            if (response.Data.Count == 0) return null;
            return Math.Round(response.Data.Average(e => e.Mspr), 1);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Insider sentiment (MSPR) fetch failed for {Symbol} — clustering only", symbol);
            return null;
        }
    }

    // MSPR is the tiebreaker/amplifier, never the sole author: a strongly
    // agreeing MSPR upgrades the clustering classification one notch, a
    // strongly disagreeing one downgrades it, and anything between the
    // thresholds (or unavailable) leaves the clustering verdict alone.
    internal static InsiderActivity CombineWithMspr(
        InsiderActivity clustering, decimal? avgMspr, decimal bullishThreshold, decimal bearishThreshold)
    {
        if (avgMspr is not { } mspr) return clustering;

        if (mspr >= bullishThreshold)
        {
            return clustering switch
            {
                InsiderActivity.Buying => InsiderActivity.StrongBuying,
                InsiderActivity.Neutral => InsiderActivity.Buying,
                InsiderActivity.ClusterSelling => InsiderActivity.Neutral, // conflicting reads cancel out
                _ => clustering,
            };
        }

        if (mspr <= bearishThreshold)
        {
            return clustering switch
            {
                InsiderActivity.StrongBuying => InsiderActivity.Buying,
                InsiderActivity.Buying => InsiderActivity.Neutral,
                InsiderActivity.Neutral => InsiderActivity.ClusterSelling,
                _ => clustering,
            };
        }

        return clustering;
    }

    internal static (EarningsConsistency Consistency, decimal? SurpriseTrendPct) GetEarningsConsistency(
        List<FinnhubEarningsEvent> earningsHistory, FundamentalConfig cfg)
    {
        var quarters = earningsHistory
            .Where(e => e.EpsActual.HasValue)
            .Take(cfg.EarningsHistoryQuarters)
            .ToList();

        if (quarters.Count < 2) return (EarningsConsistency.Insufficient, null);

        var beats = quarters.Count(q => (q.SurprisePercent ?? 0m) > 0m);
        var lastQuarterBeat = (quarters[0].SurprisePercent ?? 0m) > 0m;
        var lastQuarterMissed = (quarters[0].SurprisePercent ?? 0m) < 0m;

        var consistency =
            beats is 3 or 4 ? EarningsConsistency.ConsistentBeater :
            beats >= 2 && lastQuarterBeat ? EarningsConsistency.RecentBeater :
            beats == 2 ? EarningsConsistency.Mixed :
            beats >= 1 && lastQuarterMissed ? EarningsConsistency.RecentMiss :
            EarningsConsistency.ConsistentMisser;

        return (consistency, ComputeSurpriseTrend(quarters));
    }

    // Surprise ACCELERATION from the same quarters (newest first): average
    // surprise % of the recent half minus the older half. A company beating
    // by more each quarter carries post-earnings-drift momentum the flat
    // beat count can't see; a shrinking beat is often the first crack.
    // Needs at least 3 quarters with a surprise figure to say anything.
    internal static decimal? ComputeSurpriseTrend(IReadOnlyList<FinnhubEarningsEvent> quartersNewestFirst)
    {
        var surprises = quartersNewestFirst
            .Where(q => q.SurprisePercent.HasValue)
            .Select(q => q.SurprisePercent!.Value)
            .ToList();
        if (surprises.Count < 3) return null;

        var half = surprises.Count / 2;
        var recent = surprises.Take(half).Average();
        var older = surprises.Skip(half).Average();
        return Math.Round(recent - older, 2);
    }

    private async Task<RevenueDirection> GetRevenueDirectionAsync(IFinnhubClient finnhub, string symbol, FundamentalConfig cfg, CancellationToken ct)
    {
        if (!cfg.RevenueEstimatesEnabled)
            return RevenueDirection.Insufficient;

        try
        {
            await rateLimiter.WaitAsync(ct);
            var response = await finnhub.GetRevenueEstimatesAsync(symbol);
            var estimates = response.Data.OrderBy(e => e.Period).Take(3).ToList();

            if (estimates.Count < 2) return RevenueDirection.Insufficient;

            var increasing = true;
            var decreasing = true;
            for (var i = 1; i < estimates.Count; i++)
            {
                if (estimates[i].RevenueAvg <= estimates[i - 1].RevenueAvg) increasing = false;
                if (estimates[i].RevenueAvg >= estimates[i - 1].RevenueAvg) decreasing = false;
            }

            if (increasing) return RevenueDirection.Accelerating;
            if (decreasing) return RevenueDirection.Decelerating;
            return RevenueDirection.Stable;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Revenue estimates fetch failed for {Symbol}", symbol);
            return RevenueDirection.Insufficient;
        }
    }
}
