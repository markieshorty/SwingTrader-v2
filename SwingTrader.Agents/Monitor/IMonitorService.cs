using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Agents.Monitor;

public record FlaggedExit(string Symbol, ExitReason Reason, decimal CurrentPrice);

// Positions actually closed automatically this cycle — currently only ever
// MomentumHealthExit. Distinct from FlaggedExits, which still require manual
// closing in Trading212.
public record ExecutedExit(string Symbol, ExitReason Reason, decimal ExitPrice, decimal? RealizedPnl);

public record MonitorCycleResult(
    int PositionsChecked,
    int TrailingStopsUpdated,
    List<FlaggedExit> FlaggedExits,
    bool CircuitBreakerTriggered,
    List<ExecutedExit>? ExecutedExits = null);

public interface IMonitorService
{
    // tiingo is optional: it exists solely for the bear-market regime check
    // (SPY history comes from Tiingo). When null the bear autopause/resume
    // step is skipped; everything else in the cycle is unaffected.
    Task<MonitorCycleResult> RunCycleAsync(
        int accountId,
        IFinnhubClient finnhub,
        ITrading212Client t212,
        ITiingoClient? tiingo = null,
        CancellationToken ct = default);
}
