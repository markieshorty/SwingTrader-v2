using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Agents.Risk;

public interface ITierEvaluationService
{
    Task<TierEvaluationRecord> EvaluateAsync(int accountId, IClaudeClient claude, CancellationToken ct = default);
}
