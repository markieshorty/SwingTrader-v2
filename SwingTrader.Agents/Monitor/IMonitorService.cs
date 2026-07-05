using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Agents.Monitor;

public record FlaggedExit(string Symbol, ExitReason Reason, decimal CurrentPrice);

public record MonitorCycleResult(
    int PositionsChecked,
    int TrailingStopsUpdated,
    List<FlaggedExit> FlaggedExits,
    bool CircuitBreakerTriggered);

public interface IMonitorService
{
    Task<MonitorCycleResult> RunCycleAsync(
        int accountId,
        IFinnhubClient finnhub,
        ITrading212Client t212,
        CancellationToken ct = default);
}
