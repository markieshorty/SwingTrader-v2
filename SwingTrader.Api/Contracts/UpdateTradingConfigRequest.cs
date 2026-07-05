using SwingTrader.Core.Enums;

namespace SwingTrader.Api.Contracts;

public record UpdateTradingConfigRequest(TradingMode TradingMode, bool ApprovalRequired);

public record AddNotificationRecipientRequest(string Email, NotificationCategory Categories);
