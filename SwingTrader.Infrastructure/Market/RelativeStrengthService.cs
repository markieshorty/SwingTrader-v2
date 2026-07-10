using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SwingTrader.Core.Interfaces;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Infrastructure.Market;

public class RelativeStrengthService(
    ICandleRepository candleRepo,
    IMemoryCache cache,
    ILogger<RelativeStrengthService> logger) : IRelativeStrengthService
{
    // Null = "couldn't compute" (see IRelativeStrengthService) - previously a
    // synthetic neutral 0.5 result, which got persisted on the signal and
    // polluted Refinement's score/outcome correlations with fake data points.
    public async Task<RelativeStrengthResult?> CalculateAsync(ITiingoClient tiingo, string symbol, CancellationToken ct)
    {
        try
        {
            var stockCandles = (await candleRepo.GetCandlesAsync(
                    symbol, "D", DateTime.UtcNow.AddDays(-10), DateTime.UtcNow))
                .OrderBy(c => c.Timestamp)
                .ToList();

            if (stockCandles.Count < RelativeStrengthCalculator.WindowDays)
            {
                logger.LogWarning("Insufficient candles for {Symbol} to calculate relative strength", symbol);
                return null;
            }

            var etf = SectorEtfMap.GetEtf(symbol);

            // Date in the key so the cached window can never lag a day behind
            // the fresh stock candles it's compared against - a 24h TTL alone
            // let the two 5-day windows be offset by one trading day.
            var cacheKey = $"etf_candles_{etf}_{DateTime.UtcNow:yyyyMMdd}";

            if (!cache.TryGetValue(cacheKey, out List<decimal>? etfCloses) || etfCloses is null)
            {
                var from = DateTime.UtcNow.AddDays(-10).ToString("yyyy-MM-dd");
                var to = DateTime.UtcNow.ToString("yyyy-MM-dd");
                var etfPrices = await tiingo.GetDailyPricesAsync(etf, from, to);
                etfCloses = etfPrices.OrderBy(p => p.Date).Select(p => p.AdjClose).ToList();
                cache.Set(cacheKey, etfCloses, TimeSpan.FromHours(24));
            }

            // Shared algorithm (RelativeStrengthCalculator) - the exact same
            // code the historic backtester runs, so live and backtest can
            // never drift apart.
            var outcome = RelativeStrengthCalculator.Compute(
                stockCandles.Select(c => c.Close).ToList(), etfCloses);
            if (outcome is null)
            {
                logger.LogWarning("Insufficient ETF candles for {Etf} to calculate relative strength", etf);
                return null;
            }

            var (stockReturn5d, etfReturn5d, relativeReturn, score) = outcome;

            var label = score >= 0.80m
                ? $"Outperforming {etf} by {relativeReturn:+0.0;-0.0}%"
                : score >= 0.60m
                    ? $"In line with {etf} ({relativeReturn:+0.0;-0.0}%)"
                    : score >= 0.40m
                        ? $"Slight underperformance vs {etf} ({relativeReturn:+0.0;-0.0}%)"
                        : $"Underperforming {etf} by {relativeReturn:+0.0;-0.0}%";

            return new RelativeStrengthResult(etf, stockReturn5d, etfReturn5d, relativeReturn, score, label);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Relative strength calculation failed for {Symbol} — treating as unavailable", symbol);
            return null;
        }
    }

}
