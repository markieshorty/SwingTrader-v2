using SwingTrader.Infrastructure.Fundamental;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Agents.Research;

public record FundamentalScore(decimal Score, string Reasoning);

public interface IFundamentalScoringService
{
    Task<FundamentalScore> ScoreAsync(IClaudeClient claude, string symbol, FundamentalSnapshot snapshot, CancellationToken ct);
}
