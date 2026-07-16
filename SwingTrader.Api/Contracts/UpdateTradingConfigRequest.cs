using SwingTrader.Core.Enums;

namespace SwingTrader.Api.Contracts;

// Force = admin-only override to switch mode despite open positions in the
// current mode (which Monitor would otherwise stop watching). Ignored for
// non-admins; see the /account/trading-config endpoint.
public record UpdateTradingConfigRequest(TradingMode TradingMode, bool ApprovalRequired, bool Force = false);

public record AddNotificationRecipientRequest(string Email, NotificationCategory Categories);

public record SetTradeApprovalRequest(bool Enabled);

public record UpdateMyEmailRequest(string Email);

public record ApproveTradeApprovalRequest(string? Symbols);

public record CompleteChecklistRequest(string CheckName, string? Notes);

public record ApplyRefinementRequest(int SuggestionId);

public record RejectRefinementRequest(int SuggestionId, string? Note);

public record UpdateStrategyWeightsRequest(
    decimal RsiWeight,
    decimal MacdWeight,
    decimal VolumeWeight,
    decimal SetupQualityWeight,
    decimal RelativeStrengthWeight,
    decimal PriceLevelWeight,
    decimal ForwardSentimentWeight,
    decimal ForwardFundamentalWeight,
    decimal ForwardFilingWeight,
    decimal BuyThreshold,
    decimal WatchThreshold,
    decimal StopLossPctDefault);

public record UpdateRiskProfileRequest(
    decimal LockedCapitalPct,
    int MaxOpenPositions,
    decimal DailyLossCircuitBreakerPct,
    int MaxHoldDays,
    double TrailingActivationPct,
    double TrailingDistancePct,
    int EarningsGateDays,
    int MinHoldDays,
    decimal MomentumHealthThreshold,
    // Which regime book this payload edits, and whether that book auto-pauses
    // new entries while it is the active regime.
    MarketRegime Regime = MarketRegime.Neutral,
    bool AutopauseTrading = false,
    // Defaults keep older payloads (serialized without these) valid.
    decimal StopLossPct = 0.05m,
    decimal TargetPct = 0.08m,
    string SizingMode = "TierLadder",
    decimal FlatPositionPct = 0.10m,
    // Funnel F2: Forward-score size tilt strength; 0 = off (multiplier 1).
    decimal SizingAggressiveness = 0m,
    // Funnel F3: Forward-score floor under gate-passing Buys; 0 = veto off.
    decimal ForwardVetoFloor = 2.5m,
    // Default book only: when true this book overrides regime switching entirely.
    bool Enabled = false);

public record UpdateSetupTacticsRequest(
    string SetupType,
    decimal StopLossPct,
    decimal TargetPct,
    int GuideHoldDays,
    double TrailingActivationPct,
    double TrailingDistancePct,
    bool Enabled = true);

public record UpdateWatchlistTargetSizeRequest(int TargetWatchlistSize);

public record UpdateQualitativeWatchlistSizeRequest(int QualitativeWatchlistSize);

public record CreateWatchlistRequest(string Name, WatchlistType Type, string? Description);

public record UpdateWatchlistRequest(string Name, string? Description, bool TopMoversEnabled = false);

public record AddWatchlistSymbolRequest(string Symbol);

public record ForceWatchlistSymbolRequest(bool Force);

public record SuspendUserRequest(string? Reason);

public record RetryJobRequest(int JobLogId);
