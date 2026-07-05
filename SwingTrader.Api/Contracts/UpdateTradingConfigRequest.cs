using SwingTrader.Core.Enums;

namespace SwingTrader.Api.Contracts;

public record UpdateTradingConfigRequest(TradingMode TradingMode, bool ApprovalRequired);

public record AddNotificationRecipientRequest(string Email, NotificationCategory Categories);

public record CompleteChecklistRequest(string CheckName, string? Notes);
