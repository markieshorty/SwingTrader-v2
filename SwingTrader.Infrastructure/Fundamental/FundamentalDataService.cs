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
    IRateLimiter rateLimiter,
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
        var (insiderActivity, buyerCount, sellerCount, netShares) = await GetInsiderActivityAsync(finnhub, symbol, cfg, ct);
        var earningsConsistency = GetEarningsConsistency(earningsHistory, cfg);
        var revenueDirection = await GetRevenueDirectionAsync(finnhub, symbol, cfg, ct);

        var snapshot = new FundamentalSnapshot(
            symbol, analystTrend, insiderActivity, earningsConsistency, revenueDirection,
            analystCount, buyerCount, sellerCount, netShares, DateTime.UtcNow);

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

            var trend = (mostRecent > oldest, mostRecent < oldest) switch
            {
                (true, _) when mostRecent > 0.3m => AnalystTrend.StronglyBullish,
                (true, _) when mostRecent > 0m => AnalystTrend.Bullish,
                (_, true) when mostRecent < -0.3m => AnalystTrend.StronglyBearish,
                (_, true) when mostRecent < 0m => AnalystTrend.Bearish,
                _ => AnalystTrend.Neutral,
            };

            return (trend, mostRecentTotal);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Analyst recommendations fetch failed for {Symbol}", symbol);
            return (AnalystTrend.Insufficient, 0);
        }
    }

    private async Task<(InsiderActivity Activity, int BuyerCount, int SellerCount, decimal? NetShares)> GetInsiderActivityAsync(
        IFinnhubClient finnhub, string symbol, FundamentalConfig cfg, CancellationToken ct)
    {
        try
        {
            await rateLimiter.WaitAsync(ct);
            var response = await finnhub.GetInsiderTransactionsAsync(symbol);
            var cutoff = DateTime.UtcNow.AddDays(-cfg.InsiderLookbackDays);

            var openMarket = response.Data
                .Where(t => (t.TransactionCode == "P" || t.TransactionCode == "S")
                    && DateTime.TryParse(t.TransactionDate, out var d) && d >= cutoff)
                .ToList();

            if (openMarket.Count == 0)
                return (InsiderActivity.Neutral, 0, 0, null);

            var netShares = openMarket.Sum(t => t.Change);
            var buyerCount = openMarket.Where(t => t.TransactionCode == "P").Select(t => t.Name).Distinct().Count();
            var sellerCount = openMarket.Where(t => t.TransactionCode == "S").Select(t => t.Name).Distinct().Count();

            var activity = (buyerCount, netShares, sellerCount) switch
            {
                ( >= 2, > 0, _) => InsiderActivity.StrongBuying,
                ( >= 1, > 0, _) => InsiderActivity.Buying,
                (_, _, >= 3) => InsiderActivity.ClusterSelling,
                _ => InsiderActivity.Neutral,
            };

            return (activity, buyerCount, sellerCount, netShares);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Insider transactions fetch failed for {Symbol}", symbol);
            return (InsiderActivity.Neutral, 0, 0, null);
        }
    }

    private static EarningsConsistency GetEarningsConsistency(List<FinnhubEarningsEvent> earningsHistory, FundamentalConfig cfg)
    {
        var quarters = earningsHistory
            .Where(e => e.EpsActual.HasValue)
            .Take(cfg.EarningsHistoryQuarters)
            .ToList();

        if (quarters.Count < 2) return EarningsConsistency.Insufficient;

        var beats = quarters.Count(q => (q.SurprisePercent ?? 0m) > 0m);
        var lastQuarterBeat = (quarters[0].SurprisePercent ?? 0m) > 0m;
        var lastQuarterMissed = (quarters[0].SurprisePercent ?? 0m) < 0m;

        if (beats is 3 or 4) return EarningsConsistency.ConsistentBeater;
        if (beats >= 2 && lastQuarterBeat) return EarningsConsistency.RecentBeater;
        if (beats == 2) return EarningsConsistency.Mixed;
        if (beats >= 1 && lastQuarterMissed) return EarningsConsistency.RecentMiss;
        return EarningsConsistency.ConsistentMisser;
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
