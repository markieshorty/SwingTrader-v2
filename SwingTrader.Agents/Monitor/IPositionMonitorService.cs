using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Monitor;

public enum ExitReason
{
    None,
    StopLossHit,
    TargetHit,
    TrailingStopHit,
    TimeExit,
    CircuitBreaker,
    MomentumHealthExit,
    ManualClose, // owner clicked "Close early" in the app - not rule-driven
    DistressExit, // active distress flag (FD3): delisting/bankruptcy 8-K or going-concern filing
}

public record PositionCheckResult(
    ExitReason Reason,
    decimal CurrentPrice,
    decimal? UpdatedTrailingStop);

public interface IPositionMonitorService
{
    Task<PositionCheckResult> CheckPositionAsync(
        Trade trade,
        decimal currentPrice,
        int maxHoldDays,
        double trailingActivationPct,
        double trailingDistancePct,
        CancellationToken ct = default);
}
