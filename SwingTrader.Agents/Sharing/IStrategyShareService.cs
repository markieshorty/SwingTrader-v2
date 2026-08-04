using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Sharing;

public interface IStrategyShareService
{
    // Everything except watchlists: active weights + thresholds, every regime
    // risk book, every setup tactic (incl. enabled toggles). Serialize with
    // StrategyShareService.SnapshotJsonOptions for storage.
    Task<StrategySnapshot> BuildSnapshotAsync(int accountId, CancellationToken ct = default);

    // Fingerprint of the account's CURRENT live settings, resolved exactly the
    // way the backtest consumer resolves an untouched Lab baseline - matching
    // the ConfigFingerprint stamped on validate/MC runs.
    Task<string> ComputeLiveFingerprintAsync(int accountId, CancellationToken ct = default);

    // Overwrites the account's weights (via the refinement audit trail, origin
    // SharedStrategy), all regime risk books and all setup tactics from the
    // snapshot. Caller is responsible for taking a backup first.
    Task ApplySnapshotAsync(int accountId, StrategySnapshot snapshot, string sourceDescription, CancellationToken ct = default);
}

// The frozen share payload (camelCase JSON in StrategyShare.SnapshotJson /
// BackupJson). Plain records so schema drift is explicit, not accidental.
public record StrategySnapshot(
    SnapshotWeights Weights,
    List<SnapshotRiskBook> RiskBooks,
    List<SnapshotSetupTactic> SetupTactics);

public record SnapshotWeights(
    decimal RsiWeight, decimal MacdWeight, decimal VolumeWeight,
    decimal SetupQualityWeight, decimal RelativeStrengthWeight, decimal PriceLevelWeight,
    decimal ForwardSentimentWeight, decimal ForwardFundamentalWeight, decimal ForwardFilingWeight,
    decimal BuyThreshold, decimal WatchThreshold, decimal StopLossPctDefault);

public record SnapshotRiskBook(
    string Regime, bool Enabled, bool AutopauseTrading,
    decimal LockedCapitalPct, int MaxOpenPositions, decimal DailyLossCircuitBreakerPct,
    int MaxHoldDays, double TrailingActivationPct, double TrailingDistancePct,
    int EarningsGateDays, int MinHoldDays, decimal MomentumHealthThreshold,
    decimal StopLossPct, decimal TargetPct,
    string SizingMode, decimal FlatPositionPct, decimal SizingAggressiveness, decimal ForwardVetoFloor,
    // Dials added 31 Jul - 4 Aug 2026. Defaults match the code defaults so
    // snapshots stored BEFORE these existed keep deserializing and apply as
    // "feature off" - a shared strategy is never silently partial.
    string SizingStyle = "FlatPercent",
    decimal RiskPerTradePct = 0.01m,
    decimal AtrStopMultiple = 2.0m,
    decimal AtrTargetMultiple = 3.5m,
    decimal MaxConvictionForBuy = 0m,
    string TargetMode = "Flat",
    decimal TargetBandFloorPct = 0.05m,
    decimal TargetBandCeilingPct = 0.25m,
    string? DisabledSetupsCsv = null);

public record SnapshotSetupTactic(
    string SetupType, bool Enabled,
    decimal StopLossPct, decimal TargetPct, int GuideHoldDays,
    double TrailingActivationPct, double TrailingDistancePct);
