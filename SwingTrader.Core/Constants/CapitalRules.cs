namespace SwingTrader.Core.Constants;

public static class CapitalRules
{
    public const decimal Tier1CapitalPct = 0.10m;
    public const decimal Tier2CapitalPct = 0.20m;
    public const decimal Tier3CapitalPct = 0.50m;
    public const decimal LockedCapitalPct = 0.70m;
    public const decimal MaxPositionPctOfActive = 0.20m;
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

    // Hard safety bounds for AccountRiskProfile.Validate() — a misconfigured
    // profile can never allow a single bad day to wipe an account, no
    // matter what the account owner tries to set it to.
    public const decimal MinLockedCapitalPct = 0.50m;
    public const decimal MaxLockedCapitalPct = 0.90m;
    public const decimal MinMaxPositionPctOfActive = 0.05m;
    public const decimal MaxMaxPositionPctOfActive = 0.33m;
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
