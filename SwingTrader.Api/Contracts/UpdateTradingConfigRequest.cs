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
    decimal SentimentWeight,
    decimal SetupQualityWeight,
    decimal RelativeStrengthWeight,
    decimal PriceLevelWeight,
    decimal FundamentalMomentumWeight,
    decimal BuyThreshold,
    decimal WatchThreshold,
    decimal StopLossPctDefault);

public record UpdateRiskProfileRequest(
    decimal LockedCapitalPct,
    decimal MaxPositionPctOfActive,
    int MaxOpenPositions,
    decimal DailyLossCircuitBreakerPct,
    int Tier1UnlockMinTrades,
    decimal Tier1UnlockMinWinRate,
    int Tier2UnlockMinTrades,
    decimal Tier2UnlockMinWinRate,
    int MaxHoldDays,
    double TrailingActivationPct,
    double TrailingDistancePct,
    int EarningsGateDays,
    int MinHoldDays,
    decimal MomentumHealthThreshold,
    int TargetWatchlistSize,
    bool AutopauseDuringBear = true,
    // Defaults keep older payloads (serialized without these) valid.
    decimal StopLossPct = 0.05m,
    decimal TargetPct = 0.08m,
    string SizingMode = "TierLadder",
    decimal FlatPositionPct = 0.10m,
    // Funnel F2: Forward-score size tilt strength; 0 = off (multiplier 1).
    decimal SizingAggressiveness = 0m);

public record CreateWatchlistRequest(string Name, WatchlistType Type, string? Description);

public record UpdateWatchlistRequest(string Name, string? Description, bool TopMoversEnabled = false);

public record AddWatchlistSymbolRequest(string Symbol);

public record ForceWatchlistSymbolRequest(bool Force);

public record SuspendUserRequest(string? Reason);

public record RetryJobRequest(int JobLogId);
