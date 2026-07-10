using System.Text.Json;
using Microsoft.Extensions.Logging;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Refinement;

public class ApplyRefinementService(
    IRefinementSuggestionRepository suggestionRepo,
    IStrategyWeightsRepository weightsRepo,
    ILogger<ApplyRefinementService> logger) : IApplyRefinementService
{
    public async Task<ApplyRefinementResult> ApplyAsync(int accountId, int suggestionId, MarketRegime? specificRegime = null, CancellationToken ct = default)
    {
        var suggestion = await suggestionRepo.GetByIdAsync(accountId, suggestionId);
        if (suggestion is null)
            return new ApplyRefinementResult(false, "Suggestion not found", null);

        if (suggestion.Status != RefinementStatus.Pending)
            return new ApplyRefinementResult(false, $"Suggestion is {suggestion.Status}, not Pending", null);

        if (suggestion.IsShadowMode)
            return new ApplyRefinementResult(false, "Cannot apply a shadow-mode suggestion — enable Refinement:Active first", null);

        StrategyWeights suggested;
        if (specificRegime is null)
        {
            suggested = JsonSerializer.Deserialize<StrategyWeights>(suggestion.SuggestedWeightsJson)
                ?? throw new InvalidOperationException("Failed to deserialize suggested weights");
        }
        else
        {
            if (string.IsNullOrEmpty(suggestion.SuggestedRegimeWeightsJson))
                return new ApplyRefinementResult(false, "This suggestion has no regime-specific weights", null);

            var regimeWeights = JsonSerializer.Deserialize<Dictionary<MarketRegime, StrategyWeights>>(suggestion.SuggestedRegimeWeightsJson)
                ?? throw new InvalidOperationException("Failed to deserialize regime weights");

            if (!regimeWeights.TryGetValue(specificRegime.Value, out var regimeSuggested))
                return new ApplyRefinementResult(false, $"No suggested weights for {specificRegime} in this suggestion", null);

            suggested = regimeSuggested;
        }

        var newWeights = new StrategyWeights
        {
            AccountId = accountId,
            RsiWeight = suggested.RsiWeight,
            MacdWeight = suggested.MacdWeight,
            VolumeWeight = suggested.VolumeWeight,
            SentimentWeight = suggested.SentimentWeight,
            SetupQualityWeight = suggested.SetupQualityWeight,
            RelativeStrengthWeight = suggested.RelativeStrengthWeight,
            PriceLevelWeight = suggested.PriceLevelWeight,
            // Omitting this left it at the class default (0.10) while the
            // other 8 were copied from a set normalised to sum to 1.0 -
            // Validate() then rejected every apply.
            FundamentalMomentumWeight = suggested.FundamentalMomentumWeight,
            BuyThreshold = suggested.BuyThreshold,
            WatchThreshold = suggested.WatchThreshold,
            StopLossPctDefault = suggested.StopLossPctDefault,
            IsActive = false,
            // Provenance carried onto the weights row itself, so "why are the
            // weights what they are" is answerable from either table.
            Source = suggestion.Origin == RefinementOrigin.StrategyLab ? "StrategyLab" : "RefinementAgent",
            ApplicableRegime = specificRegime,
            Notes = specificRegime is null
                ? $"Applied from {(suggestion.Origin == RefinementOrigin.StrategyLab ? "Strategy Lab" : "auto-refinement")} suggestion #{suggestion.Id} generated {suggestion.GeneratedAt:yyyy-MM-dd}"
                : $"Applied ({specificRegime} only) from RefinementSuggestion #{suggestion.Id} generated {suggestion.GeneratedAt:yyyy-MM-dd}"
        };

        var saved = await weightsRepo.AddAsync(newWeights);
        if (specificRegime is null)
            await weightsRepo.SetActiveAsync(accountId, saved.Id);
        else
            await weightsRepo.SetRegimeActiveAsync(accountId, saved.Id, specificRegime.Value);

        // Only mark the whole suggestion Applied when the general weights were applied —
        // a regime-only apply leaves the suggestion Pending so other regimes can still be applied.
        if (specificRegime is null)
        {
            suggestion.Status = RefinementStatus.Applied;
            suggestion.AppliedAt = DateTime.UtcNow;
            suggestion.AppliedWeightsId = saved.Id;
            await suggestionRepo.UpdateAsync(suggestion);
        }

        logger.LogInformation("Refinement suggestion #{Id} applied for account {AccountId} ({Regime}) — new active StrategyWeights #{WeightsId}",
            suggestion.Id, accountId, specificRegime?.ToString() ?? "General", saved.Id);

        return new ApplyRefinementResult(true, null, saved.Id);
    }

    public async Task<ApplyRefinementResult> RejectAsync(int accountId, int suggestionId, string? note, CancellationToken ct = default)
    {
        var suggestion = await suggestionRepo.GetByIdAsync(accountId, suggestionId);
        if (suggestion is null)
            return new ApplyRefinementResult(false, "Suggestion not found", null);

        if (suggestion.Status != RefinementStatus.Pending)
            return new ApplyRefinementResult(false, $"Suggestion is {suggestion.Status}, not Pending", null);

        suggestion.Status = RefinementStatus.Rejected;
        suggestion.RejectedAt = DateTime.UtcNow;
        suggestion.RejectionNote = note;
        await suggestionRepo.UpdateAsync(suggestion);

        logger.LogInformation("Refinement suggestion #{Id} rejected for account {AccountId}", suggestion.Id, accountId);

        return new ApplyRefinementResult(true, null, null);
    }
}
