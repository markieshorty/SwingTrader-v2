using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Agents.Report;

public interface IReportGenerationService
{
    Task<DailyReport> GenerateAsync(
        int accountId,
        IFinnhubClient finnhub,
        ITrading212Client t212,
        IClaudeClient claude,
        DateOnly reportDate,
        CancellationToken ct = default);
}
