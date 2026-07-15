using SwingTrader.Api.Contracts;
using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;

namespace SwingTrader.Api.Endpoints;

public static class StrategyWeightsEndpoints
{
    public static RouteGroupBuilder MapStrategyWeightsEndpoints(this RouteGroupBuilder api)
    {
        // Manual weight/threshold tuning (e.g. temporarily lowering BuyThreshold to
        // exercise the Execution path on demo data) - an in-place edit of the active
        // row, not a Refinement-style versioned suggestion.
        api.MapGet("/strategy-weights", async (IStrategyWeightsRepository weightsRepo, IAccountContext ctx) =>
        {
            var active = await weightsRepo.GetActiveWeightsAsync(ctx.AccountId);
            return active is null ? Results.NotFound() : Results.Ok(active);
        });

        api.MapPut("/strategy-weights", async (
            UpdateStrategyWeightsRequest req,
            IStrategyWeightsRepository weightsRepo,
            IRefinementSuggestionRepository suggestionRepo,
            IAccountRepository accounts,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();

            try
            {
                await weightsRepo.UpdateWeightsAsync(ctx.AccountId, new StrategyWeightsUpdate(
                    req.RsiWeight, req.MacdWeight, req.VolumeWeight,
                    req.SetupQualityWeight, req.RelativeStrengthWeight, req.PriceLevelWeight,
                    req.ForwardSentimentWeight, req.ForwardFundamentalWeight,
                    req.BuyThreshold, req.WatchThreshold, req.StopLossPctDefault));

                // Any weights change (Settings sliders, Strategy Lab apply)
                // invalidates a pending refinement suggestion: it was computed
                // against the OLD baseline, so applying it later would silently
                // blend back toward obsolete weights. Supersede rather than
                // leave a stale Apply button armed.
                var account = await accounts.GetAsync(ctx.AccountId);
                if (account is not null)
                    await suggestionRepo.SupersedeAllPendingAsync(ctx.AccountId, account.TradingMode);

                return Results.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        return api;
    }
}
