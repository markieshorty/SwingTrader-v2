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
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();

            try
            {
                await weightsRepo.UpdateWeightsAsync(ctx.AccountId, new StrategyWeightsUpdate(
                    req.RsiWeight, req.MacdWeight, req.VolumeWeight, req.SentimentWeight,
                    req.SetupQualityWeight, req.RelativeStrengthWeight, req.PriceLevelWeight,
                    req.FundamentalMomentumWeight, req.BuyThreshold, req.WatchThreshold, req.StopLossPctDefault));
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
