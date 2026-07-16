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
    // stage-2 was skipped for a sub-Watch gate), and the
    // at-signal-time decisions the funnel would make / has made.
    public sealed record FunnelShadow(
        decimal GateScore, decimal? ForwardScore, bool ForwardScoreDegraded,
        bool WouldPassGate, bool WouldBeVetoed);

    // Stage 1: the six backtestable technical components blended by the gate
    // weights (which sum to 1). This is exactly what HistoricBacktester
    // computes, so sweep results, thresholds and conviction-band analysis stay
    // comparable between backtest and live.
    public static decimal Gate(
        StrategyWeights weights,
        decimal rsiScore, decimal macdScore, decimal volumeScore, decimal setupScore,
        decimal relativeStrengthScore, decimal priceLevelScore) =>
        ConvictionScorer.Calculate(
            weights, rsiScore, macdScore, volumeScore, setupScore,
            relativeStrengthScore, priceLevelScore);

    // Stage 2: the forward-looking components blended and rescaled to 0..10. A
    // null sentiment/fundamental (fetch failed, data unavailable) contributes
    // neutral 0.5 and marks the result Degraded - a degraded score may still
    // size (multiplier falls back to 1 in F2) but must never veto (F3).
    //
    // Filing (FD2) is deliberately DIFFERENT: most symbols have no fresh scored
    // 10-K/10-Q delta on any given day, so a null filing component contributes
    // neutral 0.5 WITHOUT degrading - otherwise the veto would be disabled for
    // nearly the whole watchlist every day. "No filing news" is the normal
    // state, not an outage.
    public static ForwardResult Forward(
        decimal? sentimentComponent01, decimal? fundamentalMomentum01,
        decimal sentimentWeight, decimal fundamentalWeight,
        decimal? filingComponent01 = null, decimal filingWeight = 0m)
    {
        var degraded = sentimentComponent01 is null || fundamentalMomentum01 is null;
        var blend01 =
            sentimentWeight * (sentimentComponent01 ?? 0.5m) +
            fundamentalWeight * (fundamentalMomentum01 ?? 0.5m) +
            filingWeight * (filingComponent01 ?? 0.5m);
        var score = Math.Round(Math.Clamp(blend01 * 10m, 0m, 10m), 1);
        return new ForwardResult(score, degraded);
    }

    // Maps the signed, time-decayed filing delta (-1..+1, 0 = no change /
    // fully decayed) onto the same 0..1 component scale the other forward
    // inputs use. Null in = null out (no scored filing at all).
    public static decimal? FilingComponent01(decimal? effectiveDelta) =>
        effectiveDelta is { } d ? (Math.Clamp(d, -1m, 1m) + 1m) / 2m : null;

    // Phase F3: the asymmetric veto predicate. True only for a real (non-null,
    // non-degraded) Forward score strictly below the floor - degraded or
    // missing scores never veto (fail-open: a data outage must not block
    // trading), and a floor of 0 can never fire since scores are >= 0.
    public static bool ShouldVeto(decimal? forwardScore, bool degraded, decimal vetoFloor) =>
        forwardScore is { } f && !degraded && f < vetoFloor;
}
