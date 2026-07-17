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

        if (suggestion.IsShadowMode)
            return new ApplyRefinementResult(false, "Cannot apply a shadow-mode suggestion — enable Refinement:Active first", null);

        // A non-Pending suggestion (already Applied/Rejected/Superseded) can
        // still be RE-applied from the history list - it just creates a fresh
        // active weights row from that suggestion's weights. The old
        // suggestion's status is left untouched in that case (only a Pending
        // suggestion transitions to Applied below), so the audit trail of what
        // happened when is preserved.
        var isReApply = suggestion.Status != RefinementStatus.Pending;

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

        NormaliseForwardWeights(suggested);

        var newWeights = new StrategyWeights
        {
            AccountId = accountId,
            RsiWeight = suggested.RsiWeight,
            MacdWeight = suggested.MacdWeight,
            VolumeWeight = suggested.VolumeWeight,
            SetupQualityWeight = suggested.SetupQualityWeight,
            RelativeStrengthWeight = suggested.RelativeStrengthWeight,
            PriceLevelWeight = suggested.PriceLevelWeight,
            ForwardSentimentWeight = suggested.ForwardSentimentWeight,
            ForwardFundamentalWeight = suggested.ForwardFundamentalWeight,
            ForwardFilingWeight = suggested.ForwardFilingWeight,
            BuyThreshold = suggested.BuyThreshold,
            WatchThreshold = suggested.WatchThreshold,
            StopLossPctDefault = suggested.StopLossPctDefault,
            IsActive = false,
            // Provenance carried onto the weights row itself, so "why are the
            // weights what they are" is answerable from either table.
            Source = suggestion.Origin == RefinementOrigin.StrategyLab ? "StrategyLab" : "RefinementAgent",
            ApplicableRegime = specificRegime,
            Notes = specificRegime is null
                ? $"{(isReApply ? "Re-applied" : "Applied")} from {(suggestion.Origin == RefinementOrigin.StrategyLab ? "Strategy Lab" : "auto-refinement")} suggestion #{suggestion.Id} generated {suggestion.GeneratedAt:yyyy-MM-dd}"
                : $"{(isReApply ? "Re-applied" : "Applied")} ({specificRegime} only) from RefinementSuggestion #{suggestion.Id} generated {suggestion.GeneratedAt:yyyy-MM-dd}"
        };

        var saved = await weightsRepo.AddAsync(newWeights);
        if (specificRegime is null)
            await weightsRepo.SetActiveAsync(accountId, saved.Id);
        else
            await weightsRepo.SetRegimeActiveAsync(accountId, saved.Id, specificRegime.Value);

        // Only transition a Pending suggestion to Applied when the general
        // weights were applied - a regime-only apply leaves it Pending so other
        // regimes can still be applied, and a RE-apply of an already-terminal
        // suggestion leaves its recorded status/history alone.
        if (specificRegime is null && !isReApply)
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

    // Stored SuggestedWeightsJson predating a forward-weight schema change
    // deserializes into a MIX of stored values and new-property initializer
    // defaults, so the forward trio can sum to something other than 1.0 (e.g.
    // pre-FD2 JSON: stored 0.60/0.40 + the initializer's 0.25 filing = 1.25),
    // and Validate() would reject the apply. Rescale the trio to sum 1.0,
    // preserving the stored proportions - a no-op for well-formed rows.
    internal static void NormaliseForwardWeights(StrategyWeights w)
    {
        var sum = w.ForwardSentimentWeight + w.ForwardFundamentalWeight + w.ForwardFilingWeight;
        if (sum <= 0m || Math.Abs(sum - 1.0m) <= 0.001m) return;
        w.ForwardSentimentWeight = Math.Round(w.ForwardSentimentWeight / sum, 4);
        w.ForwardFundamentalWeight = Math.Round(w.ForwardFundamentalWeight / sum, 4);
        // Drift from rounding lands on the filing component so the sum is exact.
        w.ForwardFilingWeight = 1.0m - w.ForwardSentimentWeight - w.ForwardFundamentalWeight;
    }
}
