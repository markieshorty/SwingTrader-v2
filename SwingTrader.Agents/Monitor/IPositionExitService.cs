using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Agents.Monitor;

public record PositionExitResult(bool Success, string? ErrorMessage, decimal? ExitPrice, decimal? RealizedPnl);

// The only auto-execution path in the app that closes a position: a T212
// market sell (negative quantity) rather than the buy-only path used by
// IExecutionService. Handles every per-position exit reason (stop loss,
// target, trailing stop, time exit, momentum health). CircuitBreaker is the
// one exception — a mass-liquidation event across the whole portfolio stays
// flag-only in MonitorService rather than auto-selling everything at once.
public interface IPositionExitService
{
    Task<PositionExitResult> ClosePositionAsync(
        int accountId,
        Trade trade,
        ITrading212Client t212,
        decimal currentPrice,
        ExitReason exitReason,
        string reasonDetail,
        CancellationToken ct = default);
}
