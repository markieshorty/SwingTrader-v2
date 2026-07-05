using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Agents.Refinement;

public interface IRefinementService
{
    Task<RefinementSuggestion?> RunAsync(int accountId, IClaudeClient claude, CancellationToken ct = default);
}
