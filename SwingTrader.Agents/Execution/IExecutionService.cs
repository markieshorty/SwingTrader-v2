using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Agents.Execution;

public record ExecutionResult(
    int OrdersPlaced,
    int OrdersFailed,
    int SignalsSkipped,
    string Summary,
    IReadOnlyList<string> PlacedSymbols
);

public interface IExecutionService
{
    Task<ExecutionResult> RunAsync(
        int accountId,
        IFinnhubClient finnhub,
        ITiingoClient tiingo,
        ITrading212Client t212,
        DateOnly date,
        CancellationToken ct = default);
}
