using SwingTrader.Core.Enums;

namespace SwingTrader.Agents.Refinement;

public record ApplyRefinementResult(bool Success, string? Error, int? NewWeightsId);

public interface IApplyRefinementService
{
    // specificRegime null = apply the general weights. A regime value applies only that
    // regime's suggested weights, requires SuggestedRegimeWeightsJson to contain that regime.
    Task<ApplyRefinementResult> ApplyAsync(int accountId, int suggestionId, MarketRegime? specificRegime = null, CancellationToken ct = default);
    Task<ApplyRefinementResult> RejectAsync(int accountId, int suggestionId, string? note, CancellationToken ct = default);
}
