using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Research;

// The two-stage funnel's scores (docs/funnel-plan). Phase F1: computed and
// persisted as SHADOW values alongside the legacy 8-component blend, driving
// nothing. Pure and deterministic given their inputs.
public static class FunnelScores
{
    public sealed record ForwardResult(decimal Score, bool Degraded);

    // Everything the pipeline persists per signal - the gate score
    // (earnings-adjusted), the forward score (catalyst-adjusted; null when
    // stage-2 was skipped for a sub-Watch gate under FunnelEnabled), and the
    // at-signal-time decisions the funnel would make / has made.
    public sealed record FunnelShadow(
        decimal GateScore, decimal? ForwardScore, bool ForwardScoreDegraded,
        bool WouldPassGate, bool WouldBeVetoed);

    // Stage 1: the six backtestable components only, with the two
    // forward-looking components pinned at neutral 0.5. DELIBERATELY not a
    // renormalized 6-weight blend: pinning the dead pair keeps this number
    // bit-identical to what HistoricBacktester computes, so every historical
    // sweep result, threshold and conviction-band analysis carries over
    // without translation. This one-liner exists so the definition has a
    // name, a home and tests.
    public static decimal Gate(
        StrategyWeights weights,
        decimal rsiScore, decimal macdScore, decimal volumeScore, decimal setupScore,
        decimal relativeStrengthScore, decimal priceLevelScore) =>
        ConvictionScorer.Calculate(
            weights, rsiScore, macdScore, volumeScore,
            sentimentScore: 0.5m, setupScore,
            relativeStrengthScore, priceLevelScore,
            fundamentalMomentumScore: 0.5m);

    // Stage 2: the forward-looking pair blended and rescaled to 0..10. A null
    // component (sentiment fetch failed, fundamentals unavailable) contributes
    // neutral 0.5 and marks the result Degraded - a degraded score may still
    // size (multiplier falls back to 1 in F2) but must never veto (F3).
    public static ForwardResult Forward(
        decimal? sentimentComponent01, decimal? fundamentalMomentum01,
        decimal sentimentWeight, decimal fundamentalWeight)
    {
        var degraded = sentimentComponent01 is null || fundamentalMomentum01 is null;
        var blend01 =
            sentimentWeight * (sentimentComponent01 ?? 0.5m) +
            fundamentalWeight * (fundamentalMomentum01 ?? 0.5m);
        var score = Math.Round(Math.Clamp(blend01 * 10m, 0m, 10m), 1);
        return new ForwardResult(score, degraded);
    }
}
