using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Backtesting;

// Maps an A/B run's experimental trading-RULE overrides onto the live risk
// profile when the owner ticks "Apply risk settings". Only the fields the A/B
// run actually overrode (non-null) are written - a null rule field means the
// run used the production value, so it must leave the live value untouched.
// Backtest-only knobs with no 1:1 risk-profile home (ExcludedSetups -
// handled separately as setup Enabled toggles by SetupTacticsRuleMapper -
// SimulateProbation, ActiveCapitalPct, MaxPositionPctOfActive) are
// deliberately not mapped. The caller validates the profile before saving.
public static class BacktestRiskRuleMapper
{
    public static void Apply(AccountRiskProfile profile, HistoricTradingRules rules)
    {
        if (rules.MaxHoldDays is { } maxHold) profile.MaxHoldDays = maxHold;
        if (rules.MinHoldDays is { } minHold) profile.MinHoldDays = minHold;
        if (rules.MaxOpenPositions is { } maxOpen) profile.MaxOpenPositions = maxOpen;
        if (rules.TrailingActivationPct is { } trailAct) profile.TrailingActivationPct = (double)trailAct;
        if (rules.TrailingDistancePct is { } trailDist) profile.TrailingDistancePct = (double)trailDist;
        if (rules.StopLossPct is { } stop) profile.StopLossPct = stop;
        if (rules.TargetPct is { } target) profile.TargetPct = target;
        if (rules.MomentumHealthThreshold is { } momo) profile.MomentumHealthThreshold = momo;
        // Sizing parity (18 Jul 2026 audit): these DO have live homes and were
        // silently skipped - a run that tested a different position size or
        // locked-capital reserve applied everything EXCEPT the sizing that
        // shaped its return and drawdown.
        if (rules.PositionFraction is { } posFrac) profile.FlatPositionPct = posFrac;
        if (rules.LockedCapitalPct is { } locked) profile.LockedCapitalPct = locked;
    }
}
