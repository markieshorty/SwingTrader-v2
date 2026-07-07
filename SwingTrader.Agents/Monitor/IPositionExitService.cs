using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Agents.Monitor;

public record PositionExitResult(bool Success, string? ErrorMessage, decimal? ExitPrice, decimal? RealizedPnl);

// The only auto-execution path in the app that closes a position: a T212
// market sell (negative quantity) rather than the buy-only path used by
// IExecutionService. Scoped deliberately narrow — only MomentumHealthExit
// calls this today. Stop loss / target / trailing stop / time exit remain
// flag-only (see MonitorService) until each has been reviewed the same way.
public interface IPositionExitService
{
    Task<PositionExitResult> ClosePositionAsync(
        int accountId,
        Trade trade,
        ITrading212Client t212,
        decimal currentPrice,
        string reason,
        CancellationToken ct = default);
}
