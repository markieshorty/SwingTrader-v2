namespace SwingTrader.Core.Constants;

public static class CapitalRules
{
    public const decimal Tier1CapitalPct = 0.10m;
    public const decimal Tier2CapitalPct = 0.20m;
    public const decimal Tier3CapitalPct = 0.50m;
    // Deployment defaults raised 2026-07-10 (locked 0.70 -> 0.60, max position
    // 0.20 -> 0.40) after the backtest deployment sweep: scaling SIZE on the
    // same top-3 daily trades preserved the per-trade edge, and 20%-of-
    // portfolio positions at Tier3 (50% x 40% = 60% deployed) doubled total
    // return (+14.1% -> +27.0%) at near-identical risk-efficiency
    // (return/maxDD 1.22 vs 1.24, ~22% max drawdown). Pushing to 100%
    // deployed decayed efficiency, and adding position SLOTS (the day's
    // 4th/5th picks) diluted expectancy - scale by size, never slot count.
    // Tier ladder discipline unchanged: Tier1 = 10% x 40% = 4%/position,
    // earning up to 20%/position at Tier3.
    public const decimal LockedCapitalPct = 0.60m;
    public const decimal MaxPositionPctOfActive = 0.40m;
    public const int MaxOpenPositions = 3;
    public const int Tier1UnlockMinTrades = 30;
    public const decimal Tier1UnlockMinWinRate = 0.55m;
    public const int Tier2UnlockMinTrades = 60;
    public const decimal Tier2UnlockMinWinRate = 0.58m;
    public const decimal DailyLossCircuitBreakerPct = 0.05m;
    public const decimal DowngradeWinRateThreshold = 0.40m;
    public const decimal DowngradeAvgReturnThreshold = -2.0m;

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
    public const decimal MinMaxPositionPctOfActive = 0.05m;
    public const decimal MaxMaxPositionPctOfActive = 0.40m;
    public const int MinMaxOpenPositions = 1;
    public const int MaxMaxOpenPositions = 10;
    public const decimal MinDailyLossCircuitBreakerPct = 0.02m;
    public const decimal MaxDailyLossCircuitBreakerPct = 0.15m;
    public const int MinTier1UnlockMinTrades = 20;
    public const int MaxTier1UnlockMinTrades = 100;
    public const int MaxTier2UnlockMinTrades = 200;
    public const decimal MinTier1UnlockMinWinRate = 0.50m;
    public const decimal MaxTier1UnlockMinWinRate = 0.80m;
    public const decimal MaxTier2UnlockMinWinRate = 0.85m;
}
