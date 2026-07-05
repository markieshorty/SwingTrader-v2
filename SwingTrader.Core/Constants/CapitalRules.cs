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
}
