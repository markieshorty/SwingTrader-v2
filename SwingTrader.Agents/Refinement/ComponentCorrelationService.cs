using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Refinement;

public class ComponentCorrelationService : IComponentCorrelationService
{
    // The six GATE components. Sentiment and fundamental momentum aren't here:
    // they drive the funnel's Forward score (sizing/veto), not the gate blend,
    // so the correlation engine that re-weights the gate must not touch them.
    private static readonly (string Name, Func<StockSignal, decimal?> Selector, Func<StrategyWeights, decimal> WeightSelector)[] Components =
    [
        ("Rsi", s => s.RsiScore, w => w.RsiWeight),
        ("Macd", s => s.MacdScore, w => w.MacdWeight),
        ("Volume", s => s.VolumeScore, w => w.VolumeWeight),
        ("SetupQuality", s => s.SetupQualityScore, w => w.SetupQualityWeight),
        ("RelativeStrength", s => s.RelativeStrengthScore, w => w.RelativeStrengthWeight),
        ("PriceLevel", s => s.PriceLevelScore, w => w.PriceLevelWeight),
    ];

    public CorrelationAnalysisResult Analyse(
        IReadOnlyList<(StockSignal Signal, decimal ReturnPct)> scoredTrades,
        StrategyWeights currentWeights,
        decimal maxAdjustmentPerCycle)
    {
        var findings = new List<ComponentFinding>();
        var rawWeights = new Dictionary<string, decimal>();

        foreach (var (name, selector, weightSelector) in Components)
        {
            var pairs = scoredTrades
                .Select(t => (Score: selector(t.Signal), t.ReturnPct))
                .Where(p => p.Score.HasValue)
                .Select(p => (Score: p.Score!.Value, p.ReturnPct))
                .ToList();

            var currentWeight = weightSelector(currentWeights);

            if (pairs.Count < 2)
            {
                findings.Add(new ComponentFinding(name, currentWeight, 0m, 0m, 0m, currentWeight,
                    0m, $"Not enough scored trades ({pairs.Count}) to analyse {name} — weight unchanged."));
                rawWeights[name] = currentWeight;
                continue;
            }

            var winnerScores = pairs.Where(p => p.ReturnPct > 0m).Select(p => p.Score).ToList();
            var loserScores = pairs.Where(p => p.ReturnPct <= 0m).Select(p => p.Score).ToList();
            var winnerAvg = winnerScores.Count > 0 ? winnerScores.Average() : 0m;
            var loserAvg = loserScores.Count > 0 ? loserScores.Average() : 0m;

            // Pearson r between the component score and the trade's
            // market-adjusted return - using return magnitude rather than a
            // binary win/loss means a component that predicts frequent small
            // wins but occasional large losses is penalised, not rewarded.
            var correlation = PearsonCorrelation(pairs);

            // Weights only ever *add* conviction, so the direction matters:
            // positive correlation pulls the weight toward |r|, but a
            // negatively correlated component (high score predicts losses)
            // must shrink toward zero, not get boosted by its magnitude -
            // Math.Abs here previously rewarded anti-predictive components in
            // proportion to how reliably wrong they were.
            var correlationImpliedWeight = Math.Max(0m, correlation);
            var blended = (currentWeight * 0.5m) + (correlationImpliedWeight * 0.5m);
            var delta = blended - currentWeight;
            var cappedDelta = Math.Clamp(delta, -maxAdjustmentPerCycle, maxAdjustmentPerCycle);

            // Significance gate: with n samples the standard error of r is
            // roughly 1/sqrt(n), so anything inside ~2 standard errors is
            // statistically indistinguishable from zero. Adjusting on it
            // anyway made the weights random-walk on noise every cycle
            // (at the minimum sample of 20 that gated nothing below |r|=0.45,
            // yet deltas still applied for r as low as 0.02).
            var significanceThreshold = 2m / (decimal)Math.Sqrt(pairs.Count);
            var isSignificant = Math.Abs(correlation) >= significanceThreshold;
            if (!isSignificant)
                cappedDelta = 0m;

            var suggested = Math.Max(0m, currentWeight + cappedDelta);

            rawWeights[name] = suggested;

            var reasoning = BuildReasoning(name, correlation, winnerAvg, loserAvg, cappedDelta, isSignificant, pairs.Count);
            findings.Add(new ComponentFinding(name, currentWeight, winnerAvg, loserAvg, correlation,
                suggested, cappedDelta, reasoning));
        }

        // Normalise so the 6 suggested gate weights sum to 1.0
        var total = rawWeights.Values.Sum();
        var normalised = total > 0
            ? rawWeights.ToDictionary(kv => kv.Key, kv => kv.Value / total)
            : rawWeights;

        var suggestedWeights = new StrategyWeights
        {
            RsiWeight = Round(normalised["Rsi"]),
            MacdWeight = Round(normalised["Macd"]),
            VolumeWeight = Round(normalised["Volume"]),
            SetupQualityWeight = Round(normalised["SetupQuality"]),
            RelativeStrengthWeight = Round(normalised["RelativeStrength"]),
            PriceLevelWeight = Round(normalised["PriceLevel"]),
            // Forward blend is not refined here - carry it forward unchanged.
            ForwardSentimentWeight = currentWeights.ForwardSentimentWeight,
            ForwardFundamentalWeight = currentWeights.ForwardFundamentalWeight,
            ForwardFilingWeight = currentWeights.ForwardFilingWeight,
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

            var pairs = group.Select(t => (t.Signal, ReturnPct: MarketAdjustedReturnPct(t))).ToList();
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
        var total = w.RsiWeight + w.MacdWeight + w.VolumeWeight +
                    w.SetupQualityWeight + w.RelativeStrengthWeight + w.PriceLevelWeight;
        var drift = 1.0m - total;
        if (Math.Abs(drift) < 0.0001m) return;
        w.VolumeWeight = Math.Round(w.VolumeWeight + drift, 4);
    }

    private static string BuildReasoning(string name, decimal correlation, decimal winnerAvg, decimal loserAvg, decimal delta, bool isSignificant, int sampleSize)
    {
        if (!isSignificant)
            return $"{name} correlation with returns (r={correlation:F2}) is not statistically " +
                   $"distinguishable from zero at n={sampleSize} — weight held steady.";

        var direction = delta > 0 ? "increased" : delta < 0 ? "decreased" : "held steady";
        var strength = Math.Abs(correlation) switch
        {
            >= 0.5m => "strong",
            >= 0.3m => "moderate",
            _ => "weak"
        };
        var sign = correlation >= 0 ? "positive" : "negative";
        return $"{name} shows {strength} {sign} correlation with market-adjusted returns (r={correlation:F2}; " +
               $"winners avg {winnerAvg:F2} vs losers avg {loserAvg:F2}) — weight {direction} by {Math.Abs(delta):F3}.";
    }

    // Pearson correlation between a component score and the trade's market-adjusted return %.
    private static decimal PearsonCorrelation(List<(decimal Score, decimal ReturnPct)> pairs)
    {
        var n = pairs.Count;
        var meanScore = pairs.Average(p => p.Score);
        var meanReturn = pairs.Average(p => p.ReturnPct);

        decimal covariance = 0m, scoreVar = 0m, returnVar = 0m;
        foreach (var (score, ret) in pairs)
        {
            var ds = score - meanScore;
            var dr = ret - meanReturn;
            covariance += ds * dr;
            scoreVar += ds * ds;
            returnVar += dr * dr;
        }

        if (scoreVar == 0m || returnVar == 0m) return 0m;

        var r = covariance / ((decimal)Math.Sqrt((double)scoreVar) * (decimal)Math.Sqrt((double)returnVar));
        return Math.Clamp(r, -1m, 1m);
    }
}
