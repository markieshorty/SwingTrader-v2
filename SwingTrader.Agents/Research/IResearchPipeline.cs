using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Agents.Research;

public interface IResearchPipeline
{
    Task<StockSignal?> RunAsync(
        int accountId,
        IFinnhubClient finnhub,
        ITiingoClient tiingo,
        IClaudeClient claude,
        string symbol,
        CancellationToken ct = default);
}
