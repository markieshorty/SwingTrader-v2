using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Research;

// Pure scoring functions extracted from ResearchPipeline.CalculateConviction (Phase 9a).
// Each Score* method maps a raw indicator value to a normalised 0.0-1.0 component score.
// No dependencies, no I/O — fully unit testable in isolation.
public static class ConvictionScorer
{
    public static decimal ScoreRsi(decimal? rsi)
    {
        if (rsi is null) return 0.5m;
        return rsi switch
        {
            < 25m => 0.5m,
            < 35m => Lerp(rsi.Value, 25, 35, 0.5m, 1.0m),
            < 45m => Lerp(rsi.Value, 35, 45, 1.0m, 0.75m),
            < 55m => Lerp(rsi.Value, 45, 55, 0.75m, 0.5m),
            < 65m => Lerp(rsi.Value, 55, 65, 0.5m, 0.25m),
            < 75m => Lerp(rsi.Value, 65, 75, 0.25m, 0.0m),
            _ => 0.0m
        };
    }

    public static decimal ScoreMacd(decimal? histogram, decimal? previousHistogram)
    {
        if (histogram is null) return 0.5m;
        bool rising = previousHistogram.HasValue && histogram > previousHistogram;
        if (histogram > 0 && rising) return 1.0m;
        if (histogram > 0 && !rising) return 0.6m;
        if (histogram < 0 && rising) return 0.3m;
        return 0.0m;
    }

    public static decimal ScoreVolume(decimal? volumeRatio)
    {
        if (volumeRatio is null) return 0.5m;
        return volumeRatio switch
        {
            >= 2.0m => 1.0m,
            >= 1.5m => Lerp(volumeRatio.Value, 1.5m, 2.0m, 0.75m, 1.0m),
            >= 1.0m => Lerp(volumeRatio.Value, 1.0m, 1.5m, 0.5m, 0.75m),
            _ => Lerp(Math.Max(volumeRatio.Value, 0m), 0m, 1.0m, 0.0m, 0.5m)
        };
    }

    public static decimal ScoreSentiment(decimal? sentiment)
    {
        if (sentiment is null) return 0.5m;
        return Math.Clamp((sentiment.Value + 1.0m) / 2.0m, 0.0m, 1.0m);
    }

    public static decimal ScoreSetupQuality(SetupType setup)
    {
        return setup switch
        {
            SetupType.OversoldRecovery => 1.0m,
            SetupType.Breakout => 0.9m,
            SetupType.MomentumContinuation => 0.75m,
            SetupType.VolumeSpike => 0.6m,
            SetupType.TrendFollowing => 0.5m,
            SetupType.Unknown => 0.0m,
            _ => 0.0m
        };
    }

    private static decimal Lerp(decimal value, decimal fromMin, decimal fromMax, decimal toMin, decimal toMax)
    {
        var t = (value - fromMin) / (fromMax - fromMin);
        t = Math.Clamp(t, 0m, 1m);
        return toMin + t * (toMax - toMin);
    }

    // The gate score: the six-component technical blend (0-10) that decides
    // Buy/Watch/Hold/Avoid. relativeStrengthScore and priceLevelScore default
    // to neutral 0.5 for callers that don't supply them. Sentiment and
    // fundamental momentum are NOT part of the gate - they drive the funnel's
    // Forward score instead (see FunnelScores.Forward).
    public static decimal Calculate(
        StrategyWeights weights,
        decimal rsiScore, decimal macdScore, decimal volumeScore, decimal setupScore,
        decimal relativeStrengthScore = 0.5m, decimal priceLevelScore = 0.5m)
    {
        var raw =
            weights.RsiWeight * rsiScore +
            weights.MacdWeight * macdScore +
            weights.VolumeWeight * volumeScore +
            weights.SetupQualityWeight * setupScore +
            weights.RelativeStrengthWeight * relativeStrengthScore +
            weights.PriceLevelWeight * priceLevelScore;

        return Math.Round(Math.Clamp(raw * 10m, 0m, 10m), 1);
    }
}
