using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Refinement;

public class ComponentCorrelationService : IComponentCorrelationService
{
    private static readonly (string Name, Func<StockSignal, decimal?> Selector, Func<StrategyWeights, decimal> WeightSelector)[] Components =
    [
        ("Rsi", s => s.RsiScore, w => w.RsiWeight),
        ("Macd", s => s.MacdScore, w => w.MacdWeight),
        ("Volume", s => s.VolumeScore, w => w.VolumeWeight),
        ("Sentiment", s => s.SentimentComponentScore, w => w.SentimentWeight),
        ("SetupQuality", s => s.SetupQualityScore, w => w.SetupQualityWeight),
        ("RelativeStrength", s => s.RelativeStrengthScore, w => w.RelativeStrengthWeight),
        ("PriceLevel", s => s.PriceLevelScore, w => w.PriceLevelWeight),
    ];

    public CorrelationAnalysisResult Analyse(
        IReadOnlyList<(StockSignal Signal, bool IsWinner)> scoredTrades,
        StrategyWeights currentWeights,
        decimal maxAdjustmentPerCycle)
    {
        var findings = new List<ComponentFinding>();
        var rawWeights = new Dictionary<string, decimal>();

        foreach (var (name, selector, weightSelector) in Components)
        {
            var pairs = scoredTrades
                .Select(t => (Score: selector(t.Signal), t.IsWinner))
                .Where(p => p.Score.HasValue)
                .Select(p => (Score: p.Score!.Value, p.IsWinner))
                .ToList();

            var currentWeight = weightSelector(currentWeights);

            if (pairs.Count < 2)
            {
                findings.Add(new ComponentFinding(name, currentWeight, 0m, 0m, 0m, currentWeight,
                    0m, $"Not enough scored trades ({pairs.Count}) to analyse {name} — weight unchanged."));
                rawWeights[name] = currentWeight;
                continue;
            }

            var winnerScores = pairs.Where(p => p.IsWinner).Select(p => p.Score).ToList();
            var loserScores = pairs.Where(p => !p.IsWinner).Select(p => p.Score).ToList();
            var winnerAvg = winnerScores.Count > 0 ? winnerScores.Average() : 0m;
            var loserAvg = loserScores.Count > 0 ? loserScores.Average() : 0m;

            var correlation = PointBiserialCorrelation(pairs);

            // Blend: raw-correlation-implied weight (normalised 0..1 from correlation strength,
            // preserving sign via magnitude only — a component contributes proportional to
            // |correlation|) 50/50 with the existing weight, then cap the per-cycle delta.
            var correlationImpliedWeight = Math.Abs(correlation);
            var blended = (currentWeight * 0.5m) + (correlationImpliedWeight * 0.5m);
            var delta = blended - currentWeight;
            var cappedDelta = Math.Clamp(delta, -maxAdjustmentPerCycle, maxAdjustmentPerCycle);
            var suggested = Math.Max(0m, currentWeight + cappedDelta);

            rawWeights[name] = suggested;

            var reasoning = BuildReasoning(name, correlation, winnerAvg, loserAvg, cappedDelta);
            findings.Add(new ComponentFinding(name, currentWeight, winnerAvg, loserAvg, correlation,
                suggested, cappedDelta, reasoning));
        }

        // Normalise so the 7 suggested weights sum to 1.0
        var total = rawWeights.Values.Sum();
        var normalised = total > 0
            ? rawWeights.ToDictionary(kv => kv.Key, kv => kv.Value / total)
            : rawWeights;

        var suggestedWeights = new StrategyWeights
        {
            RsiWeight = Round(normalised["Rsi"]),
            MacdWeight = Round(normalised["Macd"]),
            VolumeWeight = Round(normalised["Volume"]),
            SentimentWeight = Round(normalised["Sentiment"]),
            SetupQualityWeight = Round(normalised["SetupQuality"]),
            RelativeStrengthWeight = Round(normalised["RelativeStrength"]),
            PriceLevelWeight = Round(normalised["PriceLevel"]),
            BuyThreshold = currentWeights.BuyThreshold,
            WatchThreshold = currentWeights.WatchThreshold,
            StopLossPctDefault = currentWeights.StopLossPctDefault,
            Source = "RefinementAgent",
        };

        // Rounding can leave the sum a hair off 1.0 — nudge the largest weight to absorb it.
        FixRoundingDrift(suggestedWeights);

        // Update findings with the final (post-normalisation) suggested weight/delta
        findings = findings.Select(f =>
        {
            var finalWeight = f.ComponentName switch
            {
                "Rsi" => suggestedWeights.RsiWeight,
                "Macd" => suggestedWeights.MacdWeight,
                "Volume" => suggestedWeights.VolumeWeight,
                "Sentiment" => suggestedWeights.SentimentWeight,
                "SetupQuality" => suggestedWeights.SetupQualityWeight,
                "RelativeStrength" => suggestedWeights.RelativeStrengthWeight,
                "PriceLevel" => suggestedWeights.PriceLevelWeight,
                _ => f.SuggestedWeight
            };
            return f with { SuggestedWeight = finalWeight, WeightDelta = finalWeight - f.CurrentWeight };
        }).ToList();

        return new CorrelationAnalysisResult(findings, suggestedWeights);
    }

    public RegimeCorrelationResult AnalyseByRegime(
        IReadOnlyList<(Trade Trade, StockSignal Signal, bool IsWinner)> scoredTrades,
        StrategyWeights currentWeights,
        decimal maxAdjustmentPerCycle,
        int minRegimeSampleSize,
        int analysisPeriodDays)
    {
        // Step A — market-adjusted return per trade (falls back to raw P&L% for older
        // trades that predate SPY-return capture).
        decimal MarketAdjustedReturnPct((Trade Trade, StockSignal Signal, bool IsWinner) t)
        {
            var denom = t.Trade.EntryPrice * t.Trade.Quantity;
            var tradePct = denom == 0m ? 0m : t.Trade.RealizedPnl!.Value / denom * 100m;
            return t.Trade.SpyReturnDuringTrade.HasValue
                ? tradePct - t.Trade.SpyReturnDuringTrade.Value
                : tradePct;
        }

        var marketAdjustedWinRate = scoredTrades.Count == 0
            ? 0m
            : (decimal)scoredTrades.Count(t => MarketAdjustedReturnPct(t) > 0m) / scoredTrades.Count;

        // Step C — market condition warning based on average SPY return during the analysis window
        var spyReturns = scoredTrades.Where(t => t.Trade.SpyReturnDuringTrade.HasValue)
            .Select(t => t.Trade.SpyReturnDuringTrade!.Value).ToList();
        bool unusualMarketConditions = false;
        string? warning = null;
        if (spyReturns.Count > 0 && analysisPeriodDays > 0)
        {
            var periodSpyReturn = spyReturns.Average();
            var annualisedSpy = periodSpyReturn * (252m / analysisPeriodDays);
            unusualMarketConditions = Math.Abs(annualisedSpy) > 20m;
            warning = annualisedSpy switch
            {
                > 20m => $"Analysis period included strong bull conditions (SPY {annualisedSpy:+0.0;-0.0}% annualised). " +
                         "Component weights may be optimised for rising markets. Consider waiting for neutral " +
                         "conditions before applying regime-general weight changes.",
                < -20m => $"Analysis period included bearish conditions (SPY {annualisedSpy:+0.0;-0.0}% annualised). " +
                          "Correlation findings may reflect crisis behaviour. Weight changes should be applied cautiously.",
                _ => null
            };
        }

        // Step D — per-regime breakdown, reusing the same correlation logic per subgroup
        var regimeBreakdown = new Dictionary<MarketRegime, RegimeAnalysis>();
        var suggestedRegimeWeights = new Dictionary<MarketRegime, StrategyWeights>();

        var regimeGroups = scoredTrades
            .Where(t => t.Trade.MarketRegimeAtEntry.HasValue)
            .GroupBy(t => t.Trade.MarketRegimeAtEntry!.Value);

        foreach (var group in regimeGroups)
        {
            var count = group.Count();
            var winRate = (decimal)group.Count(t => t.IsWinner) / count;
            var avgSpyReturn = group.Where(t => t.Trade.SpyReturnDuringTrade.HasValue)
                .Select(t => t.Trade.SpyReturnDuringTrade!.Value).DefaultIfEmpty(0m).Average();
            var avgMarketAdjusted = group.Select(MarketAdjustedReturnPct).DefaultIfEmpty(0m).Average();

            if (count < minRegimeSampleSize)
            {
                regimeBreakdown[group.Key] = new RegimeAnalysis(
                    group.Key, count, winRate, avgSpyReturn, avgMarketAdjusted,
                    [], false,
                    $"Only {count} trades in {group.Key} conditions — need {minRegimeSampleSize} for reliable correlation");
                continue;
            }

            var pairs = group.Select(t => (t.Signal, t.IsWinner)).ToList();
            var subAnalysis = Analyse(pairs, currentWeights, maxAdjustmentPerCycle);

            regimeBreakdown[group.Key] = new RegimeAnalysis(
                group.Key, count, winRate, avgSpyReturn, avgMarketAdjusted,
                subAnalysis.Findings, true, null);
            suggestedRegimeWeights[group.Key] = subAnalysis.SuggestedWeights;
        }

        return new RegimeCorrelationResult(regimeBreakdown, suggestedRegimeWeights, marketAdjustedWinRate, unusualMarketConditions, warning);
    }

    private static decimal Round(decimal v) => Math.Round(v, 4);

    private static void FixRoundingDrift(StrategyWeights w)
    {
        var total = w.RsiWeight + w.MacdWeight + w.VolumeWeight + w.SentimentWeight +
                    w.SetupQualityWeight + w.RelativeStrengthWeight + w.PriceLevelWeight;
        var drift = 1.0m - total;
        if (Math.Abs(drift) < 0.0001m) return;
        w.VolumeWeight = Math.Round(w.VolumeWeight + drift, 4);
    }

    private static string BuildReasoning(string name, decimal correlation, decimal winnerAvg, decimal loserAvg, decimal delta)
    {
        var direction = delta > 0 ? "increased" : delta < 0 ? "decreased" : "held steady";
        var strength = Math.Abs(correlation) switch
        {
            >= 0.5m => "strong",
            >= 0.3m => "moderate",
            >= 0.1m => "weak",
            _ => "negligible"
        };
        return $"{name} shows {strength} correlation with winning trades (r={correlation:F2}; " +
               $"winners avg {winnerAvg:F2} vs losers avg {loserAvg:F2}) — weight {direction} by {Math.Abs(delta):F3}.";
    }

    // Point-biserial correlation between a continuous score and a binary outcome (winner=1/loser=0).
    private static decimal PointBiserialCorrelation(List<(decimal Score, bool IsWinner)> pairs)
    {
        var n = pairs.Count;
        var winners = pairs.Where(p => p.IsWinner).Select(p => p.Score).ToList();
        var losers = pairs.Where(p => !p.IsWinner).Select(p => p.Score).ToList();
        if (winners.Count == 0 || losers.Count == 0) return 0m;

        var meanWinners = winners.Average();
        var meanLosers = losers.Average();
        var allScores = pairs.Select(p => p.Score).ToList();
        var mean = allScores.Average();
        var variance = allScores.Sum(s => (s - mean) * (s - mean)) / n;
        var stdDev = (decimal)Math.Sqrt((double)variance);
        if (stdDev == 0m) return 0m;

        var p = (decimal)winners.Count / n;
        var q = (decimal)losers.Count / n;
        var pq = (decimal)Math.Sqrt((double)(p * q));

        var r = (meanWinners - meanLosers) / stdDev * pq;
        return Math.Clamp(r, -1m, 1m);
    }
}
