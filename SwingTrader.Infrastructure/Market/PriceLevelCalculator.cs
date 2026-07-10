using SwingTrader.Core.Enums;
using SwingTrader.Infrastructure.Configuration;

namespace SwingTrader.Infrastructure.Market;

// Minimal bar shape the price-level algorithm needs - lets the live service
// (StockCandle) and the backtester (DailyBar) feed the same code without
// referencing each other's types.
public readonly record struct PriceBar(decimal High, decimal Low, decimal Close, decimal Volume);

// The support/resistance ALGORITHM, extracted pure so the live service
// (PriceLevelService, fetching 120 calendar days of candles) and the historic
// backtester (last ~85 stored bars) run the identical code - parity by
// construction. Bars must be oldest-first and contain ONLY data up to and
// including the scoring day (the no-lookahead guarantee is the caller's
// responsibility; the tests pin it).
public static class PriceLevelCalculator
{
    public static PriceLevelResult Compute(IReadOnlyList<PriceBar> bars, decimal currentPrice, PriceLevelConfig cfg)
    {
        if (bars.Count < cfg.MinCandles)
        {
            return new PriceLevelResult(
                PriceLevelContext.InsufficientData, null, null, 0.5m,
                "Insufficient price history");
        }

        // Significant highs/lows: strict swing points, higher/lower than the
        // 2 candles on each side (verbatim from the original live service).
        var sigHighs = new List<decimal>();
        var sigLows = new List<decimal>();
        for (int i = 2; i < bars.Count - 2; i++)
        {
            var h = bars[i].High;
            if (h > bars[i - 1].High && h > bars[i - 2].High &&
                h > bars[i + 1].High && h > bars[i + 2].High)
                sigHighs.Add(h);

            var l = bars[i].Low;
            if (l < bars[i - 1].Low && l < bars[i - 2].Low &&
                l < bars[i + 1].Low && l < bars[i + 2].Low)
                sigLows.Add(l);
        }

        var clusteredHighs = Cluster(sigHighs, cfg.ClusterPct);
        var clusteredLows = Cluster(sigLows, cfg.ClusterPct);

        var resistance = clusteredHighs.Where(h => h > currentPrice).OrderBy(h => h).ToList();
        var support = clusteredLows.Where(l => l < currentPrice).OrderByDescending(l => l).ToList();

        var nearestResistance = resistance.Count > 0 ? resistance[0] : (decimal?)null;
        var nearestSupport = support.Count > 0 ? support[0] : (decimal?)null;

        // Breakout: yesterday closed below a resistance level, price is now
        // above it, and today's volume is >= BreakoutVolumeRatio x 20-bar avg.
        bool breakoutDetected = false;
        decimal breakoutLevel = 0m;
        if (bars.Count >= 2)
        {
            var yesterdayClose = bars[^2].Close;
            var avgVol = bars.Skip(Math.Max(0, bars.Count - 20)).Average(b => b.Volume);
            var todayVol = bars[^1].Volume;
            var volRatio = avgVol > 0 ? todayVol / avgVol : 0m;

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

        // Context priority (verbatim): breakout > near support > near
        // resistance > at new high > between levels.
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
        var resistLabel = $"${nearestResistance.Value:F2}";
        return new PriceLevelResult(
            PriceLevelContext.BetweenLevels, nearestSupport, nearestResistance,
            0.50m, $"Between support ({supportLabel}) and resistance ({resistLabel})");
    }

    // Descending sort; keep the first of each cluster; a level within
    // clusterPct of the previously KEPT level is absorbed into it.
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
