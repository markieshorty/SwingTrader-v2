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

            if (stockCandles.Count < 5)
            {
                logger.LogWarning("Insufficient candles for {Symbol} to calculate relative strength", symbol);
                return null;
            }

            var stockFrom = stockCandles[^5].Close;
            var stockTo = stockCandles[^1].Close;
            var stockReturn5d = (stockTo - stockFrom) / stockFrom * 100m;

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

            if (etfCloses.Count < 5)
            {
                logger.LogWarning("Insufficient ETF candles for {Etf} to calculate relative strength", etf);
                return null;
            }

            var etfFrom = etfCloses[^5];
            var etfTo = etfCloses[^1];
            var etfReturn5d = (etfTo - etfFrom) / etfFrom * 100m;

            var relativeReturn = stockReturn5d - etfReturn5d;
            var score = ScoreRelativeReturn(relativeReturn);

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

    private static decimal ScoreRelativeReturn(decimal rel) =>
        rel >= 3.0m ? 1.00m
            : rel >= 1.0m ? Lerp(rel, 1.0m, 3.0m, 0.80m, 1.00m)
            : rel >= 0.0m ? Lerp(rel, 0.0m, 1.0m, 0.60m, 0.80m)
            : rel >= -1.0m ? Lerp(rel, -1.0m, 0.0m, 0.40m, 0.60m)
            : rel >= -3.0m ? Lerp(rel, -3.0m, -1.0m, 0.20m, 0.40m)
            : 0.00m;

    private static decimal Lerp(decimal value, decimal min, decimal max, decimal outMin, decimal outMax)
    {
        var t = (value - min) / (max - min);
        return outMin + t * (outMax - outMin);
    }
}
