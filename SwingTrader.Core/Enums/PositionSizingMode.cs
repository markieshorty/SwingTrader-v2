namespace SwingTrader.Core.Enums;

// How live positions are budgeted (AccountRiskProfile.SizingMode).
public enum PositionSizingMode
{
    // Default: budget from the earned capital tier's active pool
    // (Tier1 10% / Tier2 20% / Tier3 50% of the account), each position
    // capped at MaxPositionPctOfActive of that pool.
    TierLadder = 0,

    // Deliberate override: every position is FlatPositionPct of the whole
    // portfolio. Bypasses the tier pool - never the locked-capital ceiling,
    // cash buffer, max-open-positions or the daily circuit breaker.
    Flat = 1,
}
