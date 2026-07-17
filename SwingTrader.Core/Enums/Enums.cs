namespace SwingTrader.Core.Enums;

public enum SetupType
{
    Unknown,
    OversoldRecovery,
    Breakout,
    MomentumContinuation,
    VolumeSpike,
    TrendFollowing,
    // Oversold WITHOUT the 4-bar recovery confirmation - deliberately buys
    // while price may still be falling (appended so stored int values are
    // unchanged). Split out 17 Jul 2026 when enforcing the confirmation
    // collapsed OversoldRecovery's backtested edge (235% -> 12%): the early,
    // unconfirmed entry near the low WAS the edge; the knives it catches are
    // capped by the stop and the distress quarantine. Kept as its own setup so
    // the confirmed and loose variants carry separate tactics, live switches
    // and Lab evidence.
    OversoldRecoveryLoose
}

public enum Recommendation
{
    Watch,
    Buy,
    Sell,
    Hold,
    Avoid
}

public enum TradeDirection
{
    Long
}

public enum TradeStatus
{
    Open,
    Closed,
    StoppedOut,
    TargetHit,
    ManuallyClosed,
    // Intent-first placement states (appended so existing stored int values are
    // unchanged). Pending: a Trade row written BEFORE the broker order call so a
    // crash/redelivery between placement and persistence can't duplicate the
    // order or leave an untracked position - it has no EntryOrderId yet and is
    // resolved by Monitor's pending reconciliation (promoted to Open once the
    // order is confirmed in T212 history, or Cancelled if it never reached the
    // broker). Cancelled: a Pending intent confirmed to have never placed.
    Pending = 5,
    Cancelled = 6
}

public enum EarningsSetupType
{
    None,
    UpcomingEarnings,
    PostEarningsBeat,
    PostEarningsMiss,
    PostEarningsNeutral
}

public enum PriceLevelContext
{
    InsufficientData,
    JustBrokeResistance,
    NearSupport,
    BetweenLevels,
    NearResistance,
    AtNewHigh
}

public enum RefinementConfidenceLevel
{
    Low,
    Medium,
    High
}

public enum RefinementStatus
{
    Pending,
    Applied,
    Rejected,
    Superseded
}

// Where a weight-change suggestion came from. Every production weight change
// flows through a RefinementSuggestion row so the refinement page is the one
// audit trail, regardless of which tool proposed the change.
public enum RefinementOrigin
{
    AutoRefinement = 0,   // the scheduled correlation engine
    StrategyLab = 1,      // user-driven apply from the Strategy Lab (A/B run or optimizer sweep)
}

public enum MarketRegime
{
    Bull,
    Neutral,
    Bear,
    Crisis,
    // A master override book. Never DETECTED by the classifier - when its
    // Enabled flag is on it governs every trade regardless of market regime,
    // short-circuiting the Bull/Neutral/Bear/Crisis switch (live and in sims).
    // Appended so existing stored int values (Bull=0..Crisis=3) are unchanged.
    Default = 4,
}

public enum AnalystTrend
{
    StronglyBullish,
    Bullish,
    Neutral,
    Bearish,
    StronglyBearish,
    Insufficient
}

public enum InsiderActivity
{
    StrongBuying,
    Buying,
    Neutral,
    ClusterSelling
}

public enum EarningsConsistency
{
    ConsistentBeater,
    RecentBeater,
    Mixed,
    RecentMiss,
    ConsistentMisser,
    Insufficient
}

public enum RevenueDirection
{
    Accelerating,
    Stable,
    Decelerating,
    Insufficient
}

public enum WatchlistType
{
    AiManaged,     // Watchlist Agent refreshes weekly (technical screener + Claude)
    Manual,        // User fully controls
    Mixed,         // AI adds but never removes user-added symbols
    AiQualitative, // Weekly Claude picks over the whole universe on qualitative grounds (docs/qualitative-watchlist-plan)
}

public enum TradePhase
{
    Probation, // Days 1..MinHoldDays — momentum health not yet checked
    Confirmed, // Passed the momentum health check — normal exit rules only
    Exiting,   // Failed the momentum health check — flagged for manual close
}
