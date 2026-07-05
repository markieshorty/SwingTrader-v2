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
    All = DailyReport | Execution | PositionClosed | CircuitBreaker | MonthlySummary,
}
