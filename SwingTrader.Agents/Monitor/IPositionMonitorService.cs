using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Monitor;

public enum ExitReason
{
    None,
    StopLossHit,
    TargetHit,
    TrailingStopHit,
    TimeExit,
    CircuitBreaker
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
