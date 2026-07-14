using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Backtesting;

// Maps an A/B run's experimental trading-RULE overrides onto the live risk
// profile when the owner ticks "Apply risk settings". Only the fields the A/B
// run actually overrode (non-null) are written - a null rule field means the
// run used the production value, so it must leave the live value untouched.
// Backtest-only knobs with no 1:1 risk-profile home (ExcludedSetups,
// SimulateProbation, PositionFraction, ActiveCapitalPct) are deliberately not
// mapped. The caller validates the profile before saving.
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
        if (rules.MaxPositionPctOfActive is { } maxPos) profile.MaxPositionPctOfActive = maxPos;
    }
}
