namespace SwingTrader.Core.Enums;

// How live positions are budgeted (AccountRiskProfile.SizingMode). Both modes
// size each position as FlatPositionPct of the whole portfolio; they differ
// only in whether the funnel's Forward-score tilt is applied.
public enum PositionSizingMode
{
    // Every position is the same flat slice (FlatPositionPct of the portfolio).
    // Never exceeds the locked-capital ceiling, cash buffer, max-open-positions
    // or the daily circuit breaker.
    Flat = 0,

    // Flat base, tilted by the funnel Forward score (SizingAggressiveness):
    // higher-conviction entries size up, lower-conviction size down, within the
    // same risk budget. Falls back to flat when aggressiveness is 0 or the
    // forward score is missing/degraded.
    Funnel = 1,
}
