using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Infrastructure.Configuration;

namespace SwingTrader.Infrastructure.Market;

public class PriceLevelService(
    ICandleRepository candleRepo,
    IOptions<PriceLevelConfig> config,
    ILogger<PriceLevelService> logger) : IPriceLevelService
{
    public async Task<PriceLevelResult> AnalyseAsync(string symbol, decimal currentPrice, CancellationToken ct)
    {
        try
        {
            var cfg = config.Value;

            // Step 1 — load candles
            var candles = (await candleRepo.GetCandlesAsync(
                    symbol, "D",
                    DateTime.UtcNow.AddDays(-cfg.LookbackDays),
                    DateTime.UtcNow))
                .OrderBy(c => c.Timestamp)
                .ToList();

            if (candles.Count < cfg.MinCandles)
            {
                return new PriceLevelResult(
                    PriceLevelContext.InsufficientData, null, null, 0.5m,
                    "Insufficient price history");
            }

            // Step 2 — significant highs (swing-point detection: higher than 2 candles each side)
            var sigHighs = new List<decimal>();
            for (int i = 2; i < candles.Count - 2; i++)
            {
                var h = candles[i].High;
                if (h > candles[i - 1].High && h > candles[i - 2].High &&
                    h > candles[i + 1].High && h > candles[i + 2].High)
                    sigHighs.Add(h);
            }

            // Step 3 — significant lows
            var sigLows = new List<decimal>();
            for (int i = 2; i < candles.Count - 2; i++)
            {
                var l = candles[i].Low;
                if (l < candles[i - 1].Low && l < candles[i - 2].Low &&
                    l < candles[i + 1].Low && l < candles[i + 2].Low)
                    sigLows.Add(l);
            }

            // Step 4 — cluster nearby levels (keep most recent = last seen, so sort descending and keep first of each cluster)
            var clusteredHighs = Cluster(sigHighs, cfg.ClusterPct);
            var clusteredLows = Cluster(sigLows, cfg.ClusterPct);

            // Step 5 — nearest levels relative to current price
            var resistance = clusteredHighs.Where(h => h > currentPrice).OrderBy(h => h).ToList();
            var support = clusteredLows.Where(l => l < currentPrice).OrderByDescending(l => l).ToList();

            var nearestResistance = resistance.Count > 0 ? resistance[0] : (decimal?)null;
            var nearestSupport = support.Count > 0 ? support[0] : (decimal?)null;

            // Step 6 — breakout detection
            bool breakoutDetected = false;
            decimal breakoutLevel = 0m;
            if (candles.Count >= 2)
            {
                var yesterdayClose = candles[^2].Close;
                var todayAvgVol = candles.TakeLast(20).Average(c => (decimal)c.Volume);
                var todayVol = (decimal)candles[^1].Volume;
                var volRatio = todayAvgVol > 0 ? todayVol / todayAvgVol : 0m;

                foreach (var level in clusteredHighs)
                {
                    if (yesterdayClose < level && currentPrice > level && volRatio >= cfg.BreakoutVolumeRatio)
                    {
                        breakoutDetected = true;
                        breakoutLevel = level;
                        break;
                    }
                }
            }

            // Step 7 — context and score (priority order)
            if (breakoutDetected)
            {
                return new PriceLevelResult(
                    PriceLevelContext.JustBrokeResistance, nearestSupport, nearestResistance,
                    1.0m, $"Just broke through resistance at ${breakoutLevel:F2} on volume — confirmed");
            }

            if (nearestSupport.HasValue)
            {
                var pctFromSupport = (currentPrice - nearestSupport.Value) / nearestSupport.Value * 100m;
                if (pctFromSupport <= cfg.ProximityPct)
                {
                    return new PriceLevelResult(
                        PriceLevelContext.NearSupport, nearestSupport, nearestResistance,
                        0.85m, $"Trading near support at ${nearestSupport.Value:F2} ({pctFromSupport:F1}% above)");
                }
            }

            if (nearestResistance.HasValue)
            {
                var pctFromResistance = (nearestResistance.Value - currentPrice) / currentPrice * 100m;
                if (pctFromResistance <= cfg.ProximityPct)
                {
                    return new PriceLevelResult(
                        PriceLevelContext.NearResistance, nearestSupport, nearestResistance,
                        0.15m, $"Approaching resistance at ${nearestResistance.Value:F2} ({pctFromResistance:F1}% away)");
                }
            }

            if (nearestResistance is null)
            {
                return new PriceLevelResult(
                    PriceLevelContext.AtNewHigh, nearestSupport, null,
                    0.60m, "Above all recent resistance — clear runway but no history");
            }

            var supportLabel = nearestSupport.HasValue ? $"${nearestSupport.Value:F2}" : "none";
            var resistLabel = nearestResistance.HasValue ? $"${nearestResistance.Value:F2}" : "clear";
            return new PriceLevelResult(
                PriceLevelContext.BetweenLevels, nearestSupport, nearestResistance,
                0.50m, $"Between support ({supportLabel}) and resistance ({resistLabel})");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Price level analysis failed for {Symbol} — using neutral fallback", symbol);
            return new PriceLevelResult(PriceLevelContext.InsufficientData, null, null, 0.5m, "Price level analysis unavailable");
        }
    }

    private static List<decimal> Cluster(List<decimal> levels, decimal clusterPct)
    {
        if (levels.Count == 0) return levels;

        var sorted = levels.OrderByDescending(l => l).ToList();
        var kept = new List<decimal> { sorted[0] };

        for (int i = 1; i < sorted.Count; i++)
        {
            var prev = kept[^1];
            var pctDiff = Math.Abs(sorted[i] - prev) / prev * 100m;
            if (pctDiff > clusterPct)
                kept.Add(sorted[i]);
        }

        return kept;
    }
}
