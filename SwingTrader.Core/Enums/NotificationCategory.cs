namespace SwingTrader.Core.Enums;

[Flags]
public enum NotificationCategory
{
    None = 0,
    DailyReport = 1,
    Execution = 2,
    PositionClosed = 4,
    CircuitBreaker = 8,
    MonthlySummary = 16,
    // Separate from DailyReport - the report is informational for anyone with
    // that category on, but the ability to actually approve/reject today's
    // trades is a distinct, deliberately opt-in permission.
    TradeApproval = 32,
    All = DailyReport | Execution | PositionClosed | CircuitBreaker | MonthlySummary | TradeApproval,
}
