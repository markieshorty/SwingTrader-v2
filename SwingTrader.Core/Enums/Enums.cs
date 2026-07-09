namespace SwingTrader.Core.Enums;

public enum SetupType
{
    Unknown,
    OversoldRecovery,
    Breakout,
    MomentumContinuation,
    VolumeSpike,
    TrendFollowing
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

public enum CapitalTier
{
    Tier1,
    Tier2,
    Tier3
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

public enum MarketRegime
{
    Bull,
    Neutral,
    Bear,
    Crisis
}

public enum DataMaturityLevel
{
    EarlyStage,    // < 30 scored trades
    Developing,    // 30-60 trades
    Established,   // 60-100 trades
    Mature         // 100+ trades
}

public enum ReadinessStatus
{
    NotReady,
    Approaching,   // > 70% of criteria met
    Ready,         // all criteria met
    AlreadyEnabled,
    NoDataRequirement
}

public enum FeatureRiskLevel
{
    Low,      // additive, safe to enable
    Medium,   // changes behaviour
    High      // involves real money
}

public enum MilestoneStatus
{
    Completed,
    Estimated,
    MarketDependent, // Bear regime — can't estimate
    RequiresCode     // Phase 8 — not yet built
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
    AiManaged, // Watchlist Agent refreshes weekly
    Manual,    // User fully controls
    Mixed,     // AI adds but never removes user-added symbols
}

public enum TradePhase
{
    Probation, // Days 1..MinHoldDays — momentum health not yet checked
    Confirmed, // Passed the momentum health check — normal exit rules only
    Exiting,   // Failed the momentum health check — flagged for manual close
}
