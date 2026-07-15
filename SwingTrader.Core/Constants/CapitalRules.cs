namespace SwingTrader.Core.Constants;

public static class CapitalRules
{
    public const decimal LockedCapitalPct = 0.60m;
    public const int MaxOpenPositions = 3;
    public const decimal DailyLossCircuitBreakerPct = 0.05m;

    // Per-account trading behaviour defaults and allowed ranges
    public const int DefaultMaxHoldDays = 10;
    public const int MinMaxHoldDays = 5;
    public const int MaxMaxHoldDays = 30;

    public const double DefaultTrailingActivationPct = 0.05;
    public const double MinTrailingActivationPct = 0.02;
    public const double MaxTrailingActivationPct = 0.15;

    public const double DefaultTrailingDistancePct = 0.03;
    public const double MinTrailingDistancePct = 0.01;
    public const double MaxTrailingDistancePct = 0.10;

    public const int DefaultEarningsGateDays = 5;
    public const int MinEarningsGateDays = 0;
    public const int MaxEarningsGateDays = 14;

    // Probation period (MinHoldDays) — must always be < MaxHoldDays, a
    // cross-field constraint enforced in AccountRiskProfile.Validate() rather
    // than as an individual range check.
    public const int DefaultMinHoldDays = 3;
    public const int AbsoluteMinHoldDays = 1;

    public const decimal DefaultMomentumHealthThreshold = 0.35m;
    public const decimal MinMomentumHealthThreshold = 0.20m;
    public const decimal MaxMomentumHealthThreshold = 0.60m;

    // Flat stop-loss / take-profit settings (replaced EntryLevelCalculator's
    // per-setup / per-conviction tables 2026-07-12). Defaults match the old
    // tables' common case (5% stop, +8% target); bounds keep a fat-fingered
    // value from creating instant-stop-out or never-reachable orders.
    public const decimal DefaultStopLossPct = 0.05m;
    public const decimal MinStopLossPct = 0.02m;
    public const decimal MaxStopLossPct = 0.15m;
    public const decimal DefaultTargetPct = 0.08m;
    public const decimal MinTargetPct = 0.03m;
    public const decimal MaxTargetPct = 0.30m;

    // Position sizing: every position is FlatPositionPct of the whole
    // portfolio (Funnel mode then tilts it by the Forward score). Never
    // exceeds the locked-capital ceiling: Validate() requires
    // FlatPositionPct x MaxOpenPositions <= 1 - locked.
    public const decimal DefaultFlatPositionPct = 0.10m;
    public const decimal MinFlatPositionPct = 0.02m;
    public const decimal MaxFlatPositionPct = 0.25m;

    // How many symbols Claude selects for the weekly AI-managed watchlist
    // refresh (WatchlistSelectionService). Bounded below by having enough
    // breadth for the screener/Research pipeline to be useful, and above by
    // WatchlistConfig.MaxCandidatesForClaude (80) - asking Claude to pick more
    // than the candidate pool it's shown doesn't make sense - and by Research/
    // Monitor cycle time, since every extra symbol here is extra Tiingo/
    // Finnhub calls each cycle against rate limits already tight at 25.
    public const int DefaultTargetWatchlistSize = 25;
    public const int MinTargetWatchlistSize = 10;
    public const int MaxTargetWatchlistSize = 50;

    // Hard safety bounds for AccountRiskProfile.Validate() — a misconfigured
    // profile can never allow a single bad day to wipe an account, no
    // matter what the account owner tries to set it to.
    public const decimal MinLockedCapitalPct = 0.50m;
    public const decimal MaxLockedCapitalPct = 0.90m;
    public const int MinMaxOpenPositions = 1;
    public const int MaxMaxOpenPositions = 10;

    // Funnel Phase F2 (docs/funnel-plan): how strongly the Forward score
    // tilts position size. Aggressiveness 0 = every position gets base size
    // (multiplier exactly 1 - the deploy-safe default); 1 = sizes span
    // (1 - MaxSizingTilt)x .. (1 + MaxSizingTilt)x of the per-position base.
    // The tilt shapes distribution WITHIN the risk budget - pool headroom,
    // the cash buffer and the dust floor still clamp after it.
    public const decimal MinSizingAggressiveness = 0.0m;
    public const decimal MaxSizingAggressiveness = 1.0m;
    public const decimal MaxSizingTilt = 0.5m;

    // Funnel Phase F3 (docs/funnel-plan): floor under the Forward score for
    // gate-passing Buys. Below it a Buy demotes to Watch. 0 disables the veto.
    public const decimal MinForwardVetoFloor = 0.0m;
    public const decimal MaxForwardVetoFloor = 5.0m;
    public const decimal DefaultForwardVetoFloor = 2.5m;
    public const decimal MinDailyLossCircuitBreakerPct = 0.02m;
    public const decimal MaxDailyLossCircuitBreakerPct = 0.15m;
    public const int MinTier1UnlockMinTrades = 20;
    public const int MaxTier1UnlockMinTrades = 100;
    // Tier 2 slider floors. The binding constraint (tier2 > tier1) is enforced
    // relationally in Validate() and by the slider's dynamic min in the UI;
    // these are just the absolute rail ends.
    public const int MinTier2UnlockMinTrades = 30;
    public const int MaxTier2UnlockMinTrades = 200;
    public const decimal MinTier1UnlockMinWinRate = 0.35m;
    public const decimal MaxTier1UnlockMinWinRate = 0.80m;
    public const decimal MinTier2UnlockMinWinRate = 0.36m;
    public const decimal MaxTier2UnlockMinWinRate = 0.85m;
}
